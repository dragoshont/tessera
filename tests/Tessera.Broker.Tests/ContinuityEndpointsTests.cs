using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class ContinuityEndpointsTests : IAsyncLifetime
{
    private const string DevHeader = "X-Tessera-Dev-Principal";
    private const string Owner = "alice@example.com";
    private const string Other = "bob@example.com";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _directory = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _directory = Directory.CreateTempSubdirectory("tessera-continuity-api-test").FullName;
        var configPath = Path.Combine(_directory, "tessera.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);
        var grantsPath = Path.Combine(_directory, "grants.json");
        await File.WriteAllTextAsync(grantsPath, """
            { "grants": [], "bindings": [], "recipes": [] }
            """);
        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = new InMemoryCredentialStore(),
            ContinuityDatabasePath = Path.Combine(_directory, "continuity.db"),
            EnableContinuityFixtures = true,
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Fact]
    public async Task Continuity_requires_a_verified_principal()
    {
        var response = await _client.GetAsync(new Uri("/portal/continuity/follow-ups", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Continuity_fails_closed_when_local_storage_is_not_configured()
    {
        var port = FreePort();
        var configPath = Path.Combine(_directory, "tessera-no-continuity.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);
        var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = Path.Combine(_directory, "grants.json"),
            StoreOverride = new InMemoryCredentialStore(),
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/portal/continuity/follow-ups", UriKind.Relative));
        request.Headers.Add(DevHeader, Owner);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("continuity_unavailable", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Local_api_preserves_candidate_current_correction_and_context_contracts()
    {
        var imported = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            "/portal/continuity/fixtures/initial/import",
            new { operationId = "api-initial" });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var importedResult = await ReadJsonAsync(imported);
        var followUpId = importedResult.GetProperty("followUpId").GetString()!;
        Assert.Equal(1, importedResult.GetProperty("version").GetInt64());

        var attention = await SendAsync(Owner, HttpMethod.Get, "/portal/continuity/follow-ups?view=attention");
        using (var attentionDocument = JsonDocument.Parse(await attention.Content.ReadAsStringAsync()))
        {
            var item = Assert.Single(attentionDocument.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("attention", item.GetProperty("status").GetString());
            Assert.Equal(3, item.GetProperty("candidateCount").GetInt32());
            Assert.Null(item.GetProperty("deliverable").GetString());
        }

        var accepted = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/accept",
            new { operationId = "api-accept", expectedVersion = 1 });
        Assert.True(
            accepted.StatusCode == HttpStatusCode.OK,
            $"Expected acceptance to succeed: {await accepted.Content.ReadAsStringAsync()}");
        Assert.Equal(2, (await ReadJsonAsync(accepted)).GetProperty("version").GetInt64());

        var corrected = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/correct",
            new
            {
                operationId = "api-correct",
                expectedVersion = 2,
                field = "deliverable",
                value = "lease renewal checklist",
            });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(3, (await ReadJsonAsync(corrected)).GetProperty("version").GetInt64());

        var stale = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/accept",
            new { operationId = "api-stale", expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_version", (await ReadJsonAsync(stale)).GetProperty("code").GetString());

        var monday = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            "/portal/continuity/fixtures/monday/import",
            new { operationId = "api-monday", followUpId, expectedVersion = 3 });
        Assert.Equal(HttpStatusCode.OK, monday.StatusCode);
        Assert.Equal(4, (await ReadJsonAsync(monday)).GetProperty("version").GetInt64());

        var detail = await SendAsync(
            Owner,
            HttpMethod.Get,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}");
        using (var detailDocument = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            var revisions = detailDocument.RootElement.GetProperty("revisions").EnumerateArray().ToArray();
            Assert.Contains(revisions, revision => revision.GetProperty("field").GetString() == "deliverable"
                && revision.GetProperty("value").GetString() == "lease renewal checklist"
                && revision.GetProperty("state").GetString() == "current");
            Assert.Contains(revisions, revision => revision.GetProperty("field").GetString() == "dueAt"
                && revision.GetProperty("value").GetString() == "2026-08-17"
                && revision.GetProperty("state").GetString() == "candidate");
        }

        var why = await SendAsync(
            Owner,
            HttpMethod.Get,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/why");
        var whyText = await why.Content.ReadAsStringAsync();
        Assert.Contains("correctionEvidenceRef", whyText, StringComparison.Ordinal);
        Assert.Contains("followup.fixture.v1", whyText, StringComparison.Ordinal);

        var otherOwner = await SendAsync(
            Other,
            HttpMethod.Get,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}");
        Assert.Equal(HttpStatusCode.NotFound, otherOwner.StatusCode);
        var otherList = await SendAsync(Other, HttpMethod.Get, "/portal/continuity/follow-ups");
        Assert.Empty((await ReadJsonAsync(otherList)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Contextual_fixture_without_an_exact_follow_up_is_rejected()
    {
        var response = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            "/portal/continuity/fixtures/monday/import",
            new { operationId = "api-missing-context" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Detail_and_why_are_bounded_to_100_with_truncation_markers()
    {
        var imported = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            "/portal/continuity/fixtures/initial/import",
            new { operationId = "bounds-initial" });
        var initial = await ReadJsonAsync(imported);
        var followUpId = initial.GetProperty("followUpId").GetString()!;
        var accepted = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/accept",
            new { operationId = "bounds-accept", expectedVersion = 1 });
        var version = (await ReadJsonAsync(accepted)).GetProperty("version").GetInt64();

        for (var index = 0; index < 101; index++)
        {
            var corrected = await SendJsonAsync(
                Owner,
                HttpMethod.Post,
                $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/correct",
                new
                {
                    operationId = $"bounds-correct-{index}",
                    expectedVersion = version,
                    field = "deliverable",
                    value = $"lease renewal checklist {index}",
                });
            Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
            version = (await ReadJsonAsync(corrected)).GetProperty("version").GetInt64();
        }

        var detail = await ReadJsonAsync(await SendAsync(
            Owner,
            HttpMethod.Get,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}"));
        Assert.True(detail.GetProperty("timelineTruncated").GetBoolean());
        Assert.Equal(100, detail.GetProperty("timeline").GetArrayLength());

        var why = await ReadJsonAsync(await SendAsync(
            Owner,
            HttpMethod.Get,
            $"/portal/continuity/follow-ups/{Uri.EscapeDataString(followUpId)}/why"));
        Assert.True(why.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            100,
            why.GetProperty("fields").EnumerateObject().Sum(field => field.Value.GetArrayLength()));
    }

    private async Task<HttpResponseMessage> SendAsync(string principal, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(DevHeader, principal);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        string principal,
        HttpMethod method,
        string path,
        object body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(DevHeader, principal);
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
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