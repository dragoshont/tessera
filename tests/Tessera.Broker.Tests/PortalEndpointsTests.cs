using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// End-to-end HTTP tests for the admin-portal endpoints (ADR 0016). Runs the broker
/// in <c>dev</c> mode on loopback so the caller principal can be supplied via the
/// dev header (the same shortcut the broker tolerates only on loopback) — proving
/// the people / connections / live-view wiring without standing up a full OIDC IdP.
/// Generic identities: alice = operator (in <c>portal.admins</c>), bob = member.
/// </summary>
public sealed class PortalEndpointsTests : IAsyncLifetime
{
    private const string DevHeader = "X-Tessera-Dev-Principal";
    private const string Admin = "alice@example.com";
    private const string Member = "bob@example.com";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _dir = Directory.CreateTempSubdirectory("tessera-portal-test").FullName;

        var configPath = Path.Combine(_dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false },
              "portal": { "admins": ["alice@example.com"] }
            }
            """);

        var grantsPath = Path.Combine(_dir, "grants.json");
        File.WriteAllText(grantsPath, """
            {
              "grants": [],
              "bindings": [
                { "target": "health-portal", "onBehalfOf": "alice@example.com", "credential": "hp-alice" },
                { "target": "utility-co",    "onBehalfOf": "alice@example.com", "credential": "uc-alice" },
                { "target": "health-portal", "onBehalfOf": "bob@example.com",   "credential": "hp-bob" }
              ],
              "recipes": [
                { "target": "health-portal", "egress": "none", "description": "Health Portal" }
              ]
            }
            """);

        var store = new InMemoryCredentialStore();
        // alice's health portal is live (has a refresh token); her utility account
        // and bob's health portal are absent (no bundle).
        store.Put("hp-alice", new CredentialBundle(RefreshToken: "RT", Cookies: new Dictionary<string, string> { ["S"] = "C" }));

        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = store,
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static HttpRequestMessage As(string principal, HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        req.Headers.Add(DevHeader, principal);
        return req;
    }

    [Fact]
    public async Task Me_reports_admin_for_the_operator_and_member_for_others()
    {
        using var adminDoc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/me"))).Content.ReadAsStringAsync());
        Assert.Equal("Admin", adminDoc.RootElement.GetProperty("role").GetString());

        using var memberDoc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/me"))).Content.ReadAsStringAsync());
        Assert.Equal("Member", memberDoc.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Me_is_401_without_a_principal()
    {
        var response = await _client.GetAsync(new Uri("/portal/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task People_is_operator_only()
    {
        var forbidden = await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/people"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var ok = await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/people"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task People_lists_admin_first_with_attention_rollup()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/people"))).Content.ReadAsStringAsync());
        var people = doc.RootElement.EnumerateArray().ToArray();

        // alice (admin) first, then bob (member).
        Assert.Equal(Admin, people[0].GetProperty("principal").GetString());
        Assert.Equal("Admin", people[0].GetProperty("role").GetString());
        Assert.Equal(2, people[0].GetProperty("connectionCount").GetInt32());
        Assert.Equal(1, people[0].GetProperty("needsAttentionCount").GetInt32());   // utility-co is absent

        Assert.Equal(Member, people[1].GetProperty("principal").GetString());
        Assert.Equal("Member", people[1].GetProperty("role").GetString());
        Assert.Equal(1, people[1].GetProperty("needsAttentionCount").GetInt32());   // bob's is absent
    }

    [Fact]
    public async Task Connections_default_to_self_and_carry_no_secret_value()
    {
        var body = await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/connections"))).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var conns = doc.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, conns.Length);
        var live = conns.Single(c => c.GetProperty("provider").GetString() == "health-portal");
        Assert.Equal("live", live.GetProperty("status").GetString());
        Assert.True(live.GetProperty("hasRefreshToken").GetBoolean());
        Assert.True(live.GetProperty("hasCookies").GetBoolean());
        Assert.False(live.GetProperty("hasAccessToken").GetBoolean());

        // Secretless: the bundle's real values never appear on the wire.
        Assert.DoesNotContain("RT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"C\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_cannot_read_another_persons_connections()
    {
        var forbidden = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/connections?principal={Admin}"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // …but an operator can.
        var ok = await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/connections?principal={Member}"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Live_view_fails_closed_with_503_when_no_worker_is_wired()
    {
        var response = await _client.SendAsync(As(Admin, HttpMethod.Post, $"/portal/connections/health-portal:{Admin}/live-view"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("not configured", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_cannot_seed_another_persons_connection()
    {
        var response = await _client.SendAsync(As(Member, HttpMethod.Post, $"/portal/connections/health-portal:{Admin}/live-view"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Config_reports_loopback_dev_mode()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync(new Uri("/portal/config", UriKind.Relative)));
        Assert.Equal("dev", doc.RootElement.GetProperty("authMode").GetString());
        Assert.True(doc.RootElement.GetProperty("devLoopback").GetBoolean());
    }

    [Fact]
    public async Task Recipes_list_the_providers_for_the_wizard()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/recipes"))).Content.ReadAsStringAsync());
        var providers = doc.RootElement.EnumerateArray().Select(r => r.GetProperty("provider").GetString()).ToArray();
        Assert.Contains("health-portal", providers);
    }

    [Fact]
    public async Task Adding_a_connection_makes_a_new_person_and_connection_appear()
    {
        // The "add a person to the portal" path: an operator connects a new person
        // (carol) to a provider. She wasn't in any grant/binding before.
        const string newPerson = "carol@example.com";
        var add = await _client.SendAsync(AsJson(Admin, HttpMethod.Post, "/portal/connections",
            new { provider = "health-portal", principal = newPerson, credential = "hp-carol" }));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        using var created = JsonDocument.Parse(await add.Content.ReadAsStringAsync());
        Assert.Equal($"health-portal:{newPerson}", created.RootElement.GetProperty("connectionId").GetString());
        // No bundle for hp-carol yet → honestly "absent", not faked live.
        Assert.Equal("absent", created.RootElement.GetProperty("status").GetString());

        // She now shows up in the Users view …
        using var people = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/people"))).Content.ReadAsStringAsync());
        Assert.Contains(people.RootElement.EnumerateArray(), p => p.GetProperty("principal").GetString() == newPerson);

        // … and her connection is listable.
        using var conns = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/connections?principal={newPerson}"))).Content.ReadAsStringAsync());
        Assert.Contains(conns.RootElement.EnumerateArray(), c => c.GetProperty("provider").GetString() == "health-portal");
    }

    [Fact]
    public async Task Adding_a_connection_persists_to_the_policy_document()
    {
        const string newPerson = "dave@example.com";
        var add = await _client.SendAsync(AsJson(Admin, HttpMethod.Post, "/portal/connections",
            new { provider = "health-portal", principal = newPerson, credential = "hp-dave" }));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        // Files stay the source of truth: the binding is written back to grants.json.
        var doc = await File.ReadAllTextAsync(Path.Combine(_dir, "grants.json"));
        Assert.Contains("hp-dave", doc, StringComparison.Ordinal);
        Assert.Contains(newPerson, doc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_cannot_add_a_connection_for_someone_else()
    {
        var response = await _client.SendAsync(AsJson(Member, HttpMethod.Post, "/portal/connections",
            new { provider = "health-portal", principal = Admin, credential = "hp-x" }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_member_can_add_their_own_connection()
    {
        var response = await _client.SendAsync(AsJson(Member, HttpMethod.Post, "/portal/connections",
            new { provider = "health-portal", principal = Member, credential = "hp-bob-2" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static HttpRequestMessage AsJson(string principal, HttpMethod method, string path, object body)
    {
        var req = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        req.Headers.Add(DevHeader, principal);
        req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
