using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Audit;
using Tessera.Core.Identity;
using Tessera.Core.Model;
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
              "grants": [
                { "caller": "spiffe://tessera.local/chat", "onBehalfOf": "alice@example.com", "target": "health-portal", "actions": ["read:*", "use:book"], "stepUpActions": ["write:*", "use:book"] },
                { "caller": "spiffe://tessera.local/chat", "onBehalfOf": "bob@example.com",   "target": "health-portal", "actions": ["read:appointments"] },
                { "caller": "portal://tessera", "target": "utility-co", "actions": ["read:*"] }
              ],
              "bindings": [
                { "target": "health-portal", "onBehalfOf": "alice@example.com", "credential": "hp-alice", "owner": "user" },
                { "target": "utility-co",    "onBehalfOf": "alice@example.com", "credential": "uc-alice" },
                { "target": "health-portal", "onBehalfOf": "bob@example.com",   "credential": "hp-bob" }
              ],
              "recipes": [
                { "target": "health-portal", "egress": "none", "actions": ["read:selftest"], "description": "Health Portal", "rotation": { "owner": "external", "detail": "a domain MCP keep-warm owns rotation" } }
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
        // ADR 0025: health-portal is present-but-unverified (no longer a false "live")
        // and utility-co is absent — both are non-"live", so both need attention.
        Assert.Equal(2, people[0].GetProperty("needsAttentionCount").GetInt32());

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
        var hp = conns.Single(c => c.GetProperty("provider").GetString() == "health-portal");
        // ADR 0025: present but not exercised ⇒ "unverified" on the wire, not a false "live".
        Assert.Equal("unverified", hp.GetProperty("status").GetString());
        Assert.True(hp.GetProperty("hasRefreshToken").GetBoolean());
        Assert.True(hp.GetProperty("hasCookies").GetBoolean());
        Assert.False(hp.GetProperty("hasAccessToken").GetBoolean());

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
    public async Task Connections_surface_the_credential_owner()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/connections?principal={Admin}"))).Content.ReadAsStringAsync());
        var conns = doc.RootElement.EnumerateArray().ToArray();

        // alice's health-portal binding is owner: user; her utility-co binding defaults to service.
        var health = conns.Single(c => c.GetProperty("connectionId").GetString() == $"health-portal:{Admin}");
        Assert.Equal("user", health.GetProperty("owner").GetString());
        var utility = conns.Single(c => c.GetProperty("connectionId").GetString() == $"utility-co:{Admin}");
        Assert.Equal("service", utility.GetProperty("owner").GetString());
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

        // The recipe's rotation descriptor must survive the persist round-trip
        // (ToDocument), or a reload would silently lose "who owns rotation".
        Assert.Contains("\"owner\"", doc, StringComparison.Ordinal);
        Assert.Contains("external", doc, StringComparison.Ordinal);
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

    // ── Consents + dependents (ADR 0020) ──────────────────────────────────────

    [Fact]
    public async Task Adding_a_connection_records_a_self_scoped_consent()
    {
        // The member seeds their own login → a consent receipt is recorded.
        var add = await _client.SendAsync(AsJson(Member, HttpMethod.Post, "/portal/connections",
            new { provider = "health-portal", principal = Member, credential = "hp-bob-consent" }));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/consents"))).Content.ReadAsStringAsync());
        var receipts = doc.RootElement.EnumerateArray().ToArray();
        Assert.Contains(receipts, c =>
            c.GetProperty("target").GetString() == "health-portal"
            && c.GetProperty("owner").GetString() == "user"
            && c.GetProperty("principal").GetString() == Member);
    }

    [Fact]
    public async Task Consents_require_authentication_and_a_member_cannot_read_anothers()
    {
        var unauth = await _client.GetAsync(new Uri("/portal/consents", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        var forbidden = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/consents?principal={Admin}"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Dependents_endpoint_is_authenticated_and_self_scoped()
    {
        var unauth = await _client.GetAsync(new Uri("/portal/dependents", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // bob has no dependents in the fixture → 200 + empty list, never an error.
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/dependents"))).Content.ReadAsStringAsync());
        Assert.Equal(Member, doc.RootElement.GetProperty("guardian").GetString());
        Assert.Empty(doc.RootElement.GetProperty("dependents").EnumerateArray());

        // A member may not probe another person's dependents.
        var forbidden = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/dependents?principal={Admin}"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ── Activity feed (ADR 0017) ──────────────────────────────────────────────

    /// <summary>Seeds one brokering decision into the live ring via the registered sink.</summary>
    private void SeedAudit(string onBehalfOf, string target, string action, Effect effect = Effect.Allow)
    {
        var sink = _app.Services.GetRequiredService<IAuditSink>();
        var caller = new CallerIdentity("spiffe://tessera.local/chat", VerificationMethod.OidcJwt, "tessera.local");
        var user = new EndUserAssertion(onBehalfOf, "https://issuer.example", VerificationMethod.OidcJwt);
        var decision = effect switch
        {
            Effect.Allow => Decision.Allow("granted"),
            Effect.StepUp => Decision.StepUp("confirm-needed", "approve"),
            _ => Decision.Deny("denied by policy"),
        };
        sink.Record(new AccessRequest(caller, target, action, user), decision, credential: null);
    }

    [Fact]
    public async Task Audit_feed_requires_authentication()
    {
        var response = await _client.GetAsync(new Uri("/portal/audit", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Audit_feed_is_self_scoped_for_a_member()
    {
        SeedAudit(Admin, "health-portal", "read:appointments");
        SeedAudit(Member, "health-portal", "read:appointments");
        SeedAudit(Admin, "utility-co", "read:bill");

        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/audit"))).Content.ReadAsStringAsync());
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        // A member sees ONLY decisions made on their own behalf.
        Assert.Single(entries);
        Assert.Equal(Member, entries[0].GetProperty("onBehalfOf").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_feed_forbids_a_member_reading_another_person()
    {
        var response = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/audit?principal={Admin}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Audit_feed_lets_an_operator_see_everyone_or_one_person()
    {
        SeedAudit(Admin, "health-portal", "read:x");
        SeedAudit(Member, "health-portal", "read:x");

        using var all = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/audit"))).Content.ReadAsStringAsync());
        Assert.Equal(2, all.RootElement.GetProperty("summary").GetProperty("total").GetInt32());

        using var oneDoc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/audit?principal={Member}"))).Content.ReadAsStringAsync());
        var oneEntries = oneDoc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Single(oneEntries);
        Assert.Equal(Member, oneEntries[0].GetProperty("onBehalfOf").GetString());
    }

    [Fact]
    public async Task Audit_feed_summary_counts_effects_and_breakdowns()
    {
        SeedAudit(Admin, "health-portal", "read:x", Effect.Allow);
        SeedAudit(Admin, "health-portal", "read:y", Effect.Allow);
        SeedAudit(Admin, "utility-co", "write:pay", Effect.Deny);
        SeedAudit(Admin, "utility-co", "write:book", Effect.StepUp);

        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/audit"))).Content.ReadAsStringAsync());
        var summary = doc.RootElement.GetProperty("summary");

        Assert.Equal(4, summary.GetProperty("total").GetInt32());
        Assert.Equal(2, summary.GetProperty("allow").GetInt32());
        Assert.Equal(1, summary.GetProperty("deny").GetInt32());
        Assert.Equal(1, summary.GetProperty("stepUp").GetInt32());
        Assert.Equal(2, summary.GetProperty("byTarget").GetProperty("health-portal").GetInt32());
        Assert.Equal(2, summary.GetProperty("byTarget").GetProperty("utility-co").GetInt32());

        // Effects render in the wire vocabulary.
        var effects = doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("effect").GetString()).ToArray();
        Assert.Contains("allow", effects);
        Assert.Contains("deny", effects);
        Assert.Contains("step-up", effects);
    }

    [Fact]
    public async Task Audit_feed_caps_rows_at_limit_but_summary_spans_the_window()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedAudit(Admin, $"target-{i}", "read:x");
        }

        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/audit?limit=2"))).Content.ReadAsStringAsync());

        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
        // The summary is honest about the whole scoped window, not just the shown rows.
        Assert.Equal(5, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_feed_carries_no_secret_value()
    {
        // hp-alice holds a live bundle with secret "RT" in the store; the activity
        // feed must never surface it — it is decoupled from credential material.
        SeedAudit(Admin, "health-portal", "read:appointments");

        var body = await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/audit"))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("RT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"C\"", body, StringComparison.Ordinal);
    }

    // ── Delegations (ADR 0017) ────────────────────────────────────────────────

    [Fact]
    public async Task Delegations_require_authentication()
    {
        var response = await _client.GetAsync(new Uri("/portal/delegations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delegations_are_self_scoped_for_a_member()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/delegations"))).Content.ReadAsStringAsync());
        var delegations = doc.RootElement.EnumerateArray().ToArray();

        // bob sees only the grant that delegates to him — not alice's, not automation.
        Assert.Single(delegations);
        Assert.Equal(Member, delegations[0].GetProperty("onBehalfOf").GetString());
        Assert.Equal("health-portal", delegations[0].GetProperty("target").GetString());
        Assert.False(delegations[0].GetProperty("isAutomation").GetBoolean());
    }

    [Fact]
    public async Task Delegations_forbid_a_member_reading_another_person()
    {
        var response = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/delegations?principal={Admin}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Delegations_surface_step_up_actions_for_the_owner()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/delegations?principal={Admin}"))).Content.ReadAsStringAsync());
        var grant = doc.RootElement.EnumerateArray().Single();

        Assert.Contains("read:*", grant.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("write:*", grant.GetProperty("stepUpActions").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Delegations_surface_the_action_planes()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/delegations?principal={Admin}"))).Content.ReadAsStringAsync());
        var grant = doc.RootElement.EnumerateArray().Single();

        // alice's grant spans read:* + use:book → the read and use planes, ordered.
        var planes = grant.GetProperty("planes").EnumerateArray().Select(p => p.GetString()!).ToArray();
        Assert.Equal(["read", "use"], planes);
    }

    [Fact]
    public async Task Delegations_let_an_operator_see_everyone_including_automation()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/delegations"))).Content.ReadAsStringAsync());
        var delegations = doc.RootElement.EnumerateArray().ToArray();

        // All three grants: alice's, bob's, and the pure-automation utility-co grant.
        Assert.Equal(3, delegations.Length);
        Assert.Contains(delegations, d => d.GetProperty("isAutomation").GetBoolean() && d.GetProperty("onBehalfOf").ValueKind == JsonValueKind.Null);
        Assert.Contains(delegations, d => d.GetProperty("onBehalfOf").GetString() == Admin);
        Assert.Contains(delegations, d => d.GetProperty("onBehalfOf").GetString() == Member);
    }

    [Fact]
    public async Task Delegations_resolve_the_recipe_display_name()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/delegations?principal={Admin}"))).Content.ReadAsStringAsync());
        var grant = doc.RootElement.EnumerateArray().Single();

        // health-portal has a recipe → its description is the display name.
        Assert.Equal("Health Portal", grant.GetProperty("displayName").GetString());
    }

    // ── Modules (ADR 0017) ────────────────────────────────────────────────────

    [Fact]
    public async Task Modules_require_authentication()
    {
        var response = await _client.GetAsync(new Uri("/portal/modules", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Modules_list_loaded_connectors_for_any_authenticated_user()
    {
        // A member may read the shared catalog.
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/modules"))).Content.ReadAsStringAsync());
        var modules = doc.RootElement.EnumerateArray().ToArray();

        var hp = modules.Single(m => m.GetProperty("target").GetString() == "health-portal");
        Assert.Equal("Health Portal", hp.GetProperty("displayName").GetString());
        Assert.Equal("none", hp.GetProperty("egress").GetString());
        // egress.enabled is unset (false) in the test config → never egress-enabled.
        Assert.False(hp.GetProperty("egressEnabled").GetBoolean());
        // alice + bob both have a health-portal binding.
        Assert.Equal(2, hp.GetProperty("connectionCount").GetInt32());
    }

    [Fact]
    public async Task Modules_surface_the_action_planes_of_their_recipe()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Member, HttpMethod.Get, "/portal/modules"))).Content.ReadAsStringAsync());
        var hp = doc.RootElement.EnumerateArray().Single(m => m.GetProperty("target").GetString() == "health-portal");

        // The recipe exposes read:selftest → the read plane.
        var planes = hp.GetProperty("planes").EnumerateArray().Select(p => p.GetString()!).ToArray();
        Assert.Equal(["read"], planes);
    }

    // ── Schedule (ADR 0017) ────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_requires_authentication()
    {
        var response = await _client.GetAsync(new Uri($"/portal/connections/health-portal:{Admin}/schedule", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Schedule_reports_the_external_rotation_owner_without_faked_times()
    {
        using var doc = JsonDocument.Parse(await (await _client.SendAsync(As(Admin, HttpMethod.Get, $"/portal/connections/health-portal:{Admin}/schedule"))).Content.ReadAsStringAsync());

        Assert.Equal("external", doc.RootElement.GetProperty("rotationOwner").GetString());
        Assert.True(doc.RootElement.GetProperty("refreshConfigured").GetBoolean());
        // Tessera does not own external rotation, so it never fabricates run times.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("lastRotatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nextRotationAt").ValueKind);
    }

    [Fact]
    public async Task A_member_cannot_view_another_persons_schedule()
    {
        var response = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/connections/health-portal:{Admin}/schedule"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_member_can_view_their_own_schedule()
    {
        var response = await _client.SendAsync(As(Member, HttpMethod.Get, $"/portal/connections/health-portal:{Member}/schedule"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Schedule_is_404_for_an_unknown_connection()
    {
        var response = await _client.SendAsync(As(Admin, HttpMethod.Get, "/portal/connections/health-portal:nobody@example.com/schedule"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
