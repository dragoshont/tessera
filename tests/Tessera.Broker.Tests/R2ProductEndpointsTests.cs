using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Providers;
using Tessera.Providers.R2;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class R2ProductEndpointsTests : IAsyncLifetime
{
    private const string DevHeader = "X-Tessera-Dev-Principal";
    private const string Owner = "alice@example.com";
    private const string Other = "bob@example.com";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private InMemoryCredentialStore _custody = null!;
    private ModelTransport _transport = null!;
    private string _directory = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _directory = Directory.CreateTempSubdirectory("tessera-r2-api-test").FullName;
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
        await File.WriteAllTextAsync(grantsPath, "{ \"grants\": [], \"bindings\": [], \"recipes\": [] }");
        _custody = new InMemoryCredentialStore();
        _transport = new ModelTransport();
        var pluginRoot = Path.Combine(_directory, "plugins");
        var packageRoot = Path.Combine(pluginRoot, "reviewed-local");
        Directory.CreateDirectory(packageRoot);
        var reviewedManifest = """{"Id":"reviewed-local","Version":"1.0.0","Name":"Reviewed local","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"reviewed.local.read","Version":"1","Description":"Read reviewed local state","ExecutorKind":"native","AccountRequired":false,"RequiredPermissions":[],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":1000,"MaxResultBytes":4096}]}""";
        var manifestBytes = Encoding.UTF8.GetBytes(reviewedManifest);
        await File.WriteAllBytesAsync(Path.Combine(packageRoot, "manifest.json"), manifestBytes);
        var pluginCatalogPath = Path.Combine(pluginRoot, "catalog.json");
        await File.WriteAllTextAsync(pluginCatalogPath, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["reviewed-local@1.0.0"] = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
        }));
        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = _custody,
            TransportOverride = _transport,
            ProductDatabasePath = Path.Combine(_directory, "product.db"),
            PluginRoot = pluginRoot,
            PluginCatalogPath = pluginCatalogPath,
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task Reviewed_install_route_enforces_auth_key_body_replay_conflict_and_local_source()
    {
        const string path = "/api/v1/integrations/local/reviewed-local/versions/1.0.0/install";
        using var unauthenticated = await _client.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var missingKey = await SendJsonAsync(Owner, HttpMethod.Post, path, new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal("invalid_idempotency_key", (await ReadJsonAsync(missingKey)).GetProperty("code").GetString());

        using var invalidBody = await SendJsonAsync(Owner, HttpMethod.Post, path, new { unexpected = true }, "install-body-key");
        Assert.Equal(HttpStatusCode.BadRequest, invalidBody.StatusCode);
        Assert.Equal("invalid_request", (await ReadJsonAsync(invalidBody)).GetProperty("code").GetString());

        using var first = await SendJsonAsync(Owner, HttpMethod.Post, path, new { }, "install-key");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());
        var firstBody = await first.Content.ReadAsStringAsync();
        using var firstJson = JsonDocument.Parse(firstBody);
        Assert.Equal("INSTALLED", firstJson.RootElement.GetProperty("installState").GetString());

        using var replay = await SendJsonAsync(Owner, HttpMethod.Post, path, new { }, "install-key");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());

        using var conflict = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            "/api/v1/integrations/local/other/versions/1.0.0/install",
            new { },
            "install-key");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await ReadJsonAsync(conflict)).GetProperty("code").GetString());

        using var publicSource = await SendJsonAsync(
            Owner,
            HttpMethod.Post,
            "/api/v1/integrations/github/reviewed-local/versions/1.0.0/install",
            new { },
            "public-install-key");
        Assert.Equal(HttpStatusCode.NotFound, publicSource.StatusCode);

        var owner = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow);
        var installation = Assert.Single(await _app.Services.GetRequiredService<SqliteKernelStore>()
            .ListPluginInstallationsAsync(owner.PrincipalId));
        Assert.Equal("reviewed-local", installation.PluginId);
        Assert.False(installation.Enabled);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Product_readiness_reports_live_database_and_scheduler_health()
    {
        HttpResponseMessage? readiness = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            readiness?.Dispose();
            readiness = await _client.GetAsync(new Uri("/readyz", UriKind.Relative));
            if (readiness.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(50);
        }

        using (readiness)
        {
            Assert.NotNull(readiness);
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        }

        using var status = JsonDocument.Parse(await _client.GetStringAsync(new Uri("/status", UriKind.Relative)));
        var root = status.RootElement;
        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.Equal("ready", root.GetProperty("database").GetProperty("state").GetString());
        Assert.Equal("ready", root.GetProperty("scheduler").GetProperty("state").GetString());
        Assert.Equal(19, root.GetProperty("product").GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Desktop_CORS_allows_only_the_packaged_app_origin_and_required_headers()
    {
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/conversations");
        allowed.Headers.Add("Origin", "app://tessera");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        allowed.Headers.Add(
            "Access-Control-Request-Headers",
            "authorization,content-type,idempotency-key");
        using var allowedResponse = await _client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
        Assert.Equal("app://tessera", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowedHeaders = string.Join(",", allowedResponse.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency-key", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.False(allowedResponse.Headers.Contains("Access-Control-Allow-Credentials"));

        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/v1/conversations");
        denied.Headers.Add("Origin", "https://evil.example");
        denied.Headers.Add("Access-Control-Request-Method", "GET");
        using var deniedResponse = await _client.SendAsync(denied);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Chat_streams_transient_text_and_persists_one_final_assistant_message()
    {
        await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Bootstrap",modelProfileId=(string?)null},"stream-bootstrap");
        var ownerId=PrincipalRef.Create("https://dev.tessera.local","dev",Owner,Owner,DateTimeOffset.UtcNow).PrincipalId;var store=_app.Services.GetRequiredService<SqliteKernelStore>();var now=DateTimeOffset.UtcNow;
        await store.AddPluginInstallationAsync(new(ownerId,"model-provider","1.0.0","Models","Tessera","model-hash",ModelManifest,"{}",true,now,now,1));
        var credentialRef=ConnectedAccountCredentialRef.Create(ownerId,"stream-model");await store.AddConnectedAccountAsync(new(ownerId,"stream-model","openai-compatible","model-provider","1.0.0","Model",null,AccountLifecycle.Connected,credentialRef,AccountHealth.Healthy,null,"{\"endpoint\":\"https://models.example/v1\"}",[],[new("model-provider","1.0.0","model.chat.complete","1")],now,now,1));await _custody.PutBundleAsync(credentialRef,new CredentialBundle(AccessToken:"token"));
        await store.AddModelProfileAsync(new(ownerId,"stream-profile","stream-model","openai-compatible-remote","https://models.example/v1","test-model",8192,true,true,true,now,now,1));
        var conversation=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Streaming",modelProfileId="stream-profile"},"stream-conversation"));var id=conversation.GetProperty("id").GetString()!;
        var accepted=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{id}/messages",new{text="Stream a normal answer",modelProfileId="stream-profile"},"stream-message"));var execution=accepted.GetProperty("executionId").GetString()!;

        var events=await (await SendAsync(Owner,HttpMethod.Get,$"/api/v1/conversations/{id}/events?executionId={execution}")).Content.ReadAsStringAsync();
        var secondReader=await (await SendAsync(Owner,HttpMethod.Get,$"/api/v1/conversations/{id}/events?executionId={execution}")).Content.ReadAsStringAsync();
        var live=_app.Services.GetRequiredService<R2LiveExecutionEvents>();var published=live.ListAfter(ownerId,id,execution,0);Assert.Contains(published,item=>item.Sequence==1&&item.EventType=="text");Assert.Equal(published,live.ListAfter(ownerId,id,execution,0));
        Assert.Contains("event: completed",events,StringComparison.Ordinal);Assert.Contains("event: completed",secondReader,StringComparison.Ordinal);
        var otherConversation=await ReadJsonAsync(await SendJsonAsync(Other,HttpMethod.Post,"/api/v1/conversations",new{title="Other",modelProfileId=(string?)null},"other-stream-conversation"));
        Assert.Equal(HttpStatusCode.NotFound,(await SendAsync(Other,HttpMethod.Get,$"/api/v1/conversations/{otherConversation.GetProperty("id").GetString()}/events?executionId={execution}")).StatusCode);
        var messages=await store.ListMessagesAsync(ownerId,id);var assistant=Assert.Single(messages,item=>item.Role=="ASSISTANT");Assert.Equal("COMPLETED",assistant.Status);Assert.Equal("retried",assistant.Parts.Single(part=>part.Kind=="TEXT").Text);
        Assert.DoesNotContain(await store.ListExecutionEventsAsync(ownerId,execution,0),item=>item.EventType=="text_delta");
    }

    [Fact]
    public async Task Conversation_lifecycle_retry_stop_versions_and_owner_isolation_are_enforced()
    {
        var created = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/conversations", new { title = "Inbox", modelProfileId = (string?)null },"conversation-inbox");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = await ReadJsonAsync(created);
        var id = conversation.GetProperty("conversationId").GetString()!;
        Assert.Equal(id,(await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Inbox",modelProfileId=(string?)null},"conversation-inbox"))).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict,(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Different",modelProfileId=(string?)null},"conversation-inbox")).StatusCode);
        var ownerId = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow).PrincipalId;
        Assert.Equal(HttpStatusCode.BadRequest,(await SendAsync(Owner,HttpMethod.Get,"/api/v1/conversations?cursor=not-issued-by-tessera")).StatusCode);
        for(var index=0;index<26;index++)Assert.Equal(HttpStatusCode.Created,(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title=$"Page {index}",modelProfileId=(string?)null},$"page-{index}")).StatusCode);
        var firstPage=await ReadJsonAsync(await SendAsync(Owner,HttpMethod.Get,"/api/v1/conversations?limit=10"));Assert.Equal(10,firstPage.GetProperty("items").GetArrayLength());var nextCursor=firstPage.GetProperty("nextCursor").GetString();Assert.NotNull(nextCursor);var secondPage=await ReadJsonAsync(await SendAsync(Owner,HttpMethod.Get,$"/api/v1/conversations?limit=10&cursor={Uri.EscapeDataString(nextCursor!)}"));Assert.True(secondPage.GetProperty("items").GetArrayLength()>0);Assert.Equal(HttpStatusCode.BadRequest,(await SendAsync(Other,HttpMethod.Get,$"/api/v1/conversations?limit=10&cursor={Uri.EscapeDataString(nextCursor!)}")).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await SendAsync(Owner,HttpMethod.Get,$"/api/v1/conversations?limit=11&cursor={Uri.EscapeDataString(nextCursor!)}")).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await SendAsync(Owner,HttpMethod.Get,"/api/v1/conversations?limit=101")).StatusCode);

        var patched = await SendJsonAsync(Owner, HttpMethod.Patch, $"/api/v1/conversations/{id}", new { title = "Work", state = "ARCHIVED", expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal(3, (await ReadJsonAsync(patched)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await SendJsonAsync(Owner, HttpMethod.Patch, $"/api/v1/conversations/{id}", new { title = "Stale", state = (string?)null, expectedVersion = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendJsonAsync(Other, HttpMethod.Patch, $"/api/v1/conversations/{id}", new { title = "Other", state = (string?)null, expectedVersion = 3 })).StatusCode);

        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        var now = DateTimeOffset.UtcNow;
        await store.AddPluginInstallationAsync(new(ownerId,"model-provider","1.0.0","Models","Tessera","model-hash",ModelManifest,"{}",true,now,now,1));
        var modelCredentialRef=ConnectedAccountCredentialRef.Create(ownerId,"model-account");
        await store.AddConnectedAccountAsync(new(ownerId, "model-account", "openai-compatible", "model-provider", "1.0.0", "Model", null,
            AccountLifecycle.Connected, modelCredentialRef, AccountHealth.Healthy, null, "{\"endpoint\":\"https://models.example/v1\"}", [], [new("model-provider","1.0.0","model.chat.complete","1")], now, now, 1));
        await _custody.PutBundleAsync(modelCredentialRef, new CredentialBundle(AccessToken: "test-token"));
        await store.AddModelProfileAsync(new(ownerId, "profile-1", "model-account", "openai-compatible-remote", "https://models.example/v1", "test-model", 8192, true, true, true, now, now, 1));
        var retryConversationResponse = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/conversations", new { title = "Retry", modelProfileId = "profile-1" },"conversation-retry");
        var retryConversation = (await ReadJsonAsync(retryConversationResponse)).GetProperty("conversationId").GetString()!;
        await store.AddMessageAsync(new(ownerId, "user-1", retryConversation, "USER", "PERSISTED", null, [new("part-user", 1, "TEXT", "Try again")], now, null, 1));
        await store.AddMessageAsync(new(ownerId, "failed-1", retryConversation, "ASSISTANT", "FAILED", null, [new("part-failed", 1, "FAILURE", null, ErrorCode: "provider_unavailable")], now.AddSeconds(1), now.AddSeconds(1), 1));
        var retry = await SendJsonAsync(Owner, HttpMethod.Post, $"/api/v1/conversations/{retryConversation}/retry", new { messageId = "failed-1" }, "retry-key");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retryReceipt=await ReadJsonAsync(retry);var retried=await WaitForMessageAsync(store,ownerId,retryConversation,retryReceipt.GetProperty("messageId").GetString()!);Assert.Equal("failed-1",retried.RetryOf);Assert.Equal("COMPLETED",retried.Status);

        await store.StartExecutionAsync(ownerId, retryConversation, "execution-stop", "user-1", now);
        var stop = await SendJsonAsync(Owner, HttpMethod.Post, $"/api/v1/conversations/{retryConversation}/stop", new { executionId = "execution-stop" }, "stop-key");
        Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        Assert.True(await store.IsExecutionStoppedAsync(ownerId, "execution-stop"));

        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(Owner, HttpMethod.Delete, $"/api/v1/conversations/{id}", new { expectedVersion = 3 })).StatusCode);
    }

    [Fact]
    public async Task Memory_settings_plugin_and_job_run_routes_are_durable_bounded_and_owner_scoped()
    {
        var bootstrap = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/conversations", new { title = "Bootstrap", modelProfileId = (string?)null },"conversation-bootstrap");
        var bootstrapId=(await ReadJsonAsync(bootstrap)).GetProperty("id").GetString()!;
        var principal = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow);
        var ownerId = principal.PrincipalId;
        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(principal);

        var remembered = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/memory", new { subjectKey = "user", predicate = "preference", value = "quiet", sourceMessageId = "explicit-test" },"remember-quiet");
        var memoryId = (await ReadJsonAsync(remembered)).GetProperty("assertionId").GetString()!;
        var history = await ReadJsonAsync(await SendAsync(Owner, HttpMethod.Get, $"/api/v1/memory/{memoryId}/history"));
        Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await SendJsonAsync(Owner, HttpMethod.Post, $"/api/v1/memory/{memoryId}/stop-using", new { expectedVersion = 1 },"memory-stop-key")).StatusCode);

        var defaults = await ReadJsonAsync(await SendAsync(Owner, HttpMethod.Get, "/api/v1/settings"));
        Assert.Equal(1, defaults.GetProperty("version").GetInt64());
        var settings = await SendJsonAsync(Owner, HttpMethod.Patch, "/api/v1/settings", new { timezone = "America/New_York", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Equal(2, (await ReadJsonAsync(settings)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await SendJsonAsync(Owner, HttpMethod.Patch, "/api/v1/settings", new { timezone = "UTC", expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await SendJsonAsync(Owner,HttpMethod.Patch,"/api/v1/settings",new{approvalDefaults=new{access_token="must-not-persist"},expectedVersion=2})).StatusCode);

        var manifest = new PluginManifest("github", "1.0.0", "GitHub", "Tessera", "2.0.0",
            [new("github.issues.create", "1", "Create issue", "github-rest", true, ["issues:write"], "ExternalCommunication", 30000, 32768)],
            ["repository"]);
        await store.AddPluginInstallationAsync(new(ownerId, "github", "1.0.0", "GitHub", "Tessera", "hash",
            JsonSerializer.Serialize(manifest), "{}", true, now, now, 1));
        var rejectedConfig = await SendJsonAsync(Owner, HttpMethod.Put, "/api/v1/plugins/github/versions/1.0.0/configuration", new { values = new { apiToken = "not-a-real-token" }, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedConfig.StatusCode);
        var rejectedNestedConfig=await SendJsonAsync(Owner,HttpMethod.Put,"/api/v1/plugins/github/versions/1.0.0/configuration",new{values=new{repository=new{access_token="must-not-persist"}},expectedVersion=1});Assert.Equal(HttpStatusCode.BadRequest,rejectedNestedConfig.StatusCode);
        var configured = await SendJsonAsync(Owner, HttpMethod.Put, "/api/v1/plugins/github/versions/1.0.0/configuration", new { values = new { repository = "owner/repo" }, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        var githubCredentialRef=ConnectedAccountCredentialRef.Create(ownerId,"github-account");
        await store.AddConnectedAccountAsync(new(ownerId, "github-account", "github", "github", "1.0.0", "GitHub", null,
            AccountLifecycle.Connected, githubCredentialRef, AccountHealth.Healthy, null,
            "{\"allowedRepositories\":[\"owner/repo\"]}", ["issues:write"],
            [new("github", "1.0.0", "github.issues.create", "1")], now, now, 1));
        await _custody.PutBundleAsync(githubCredentialRef, new CredentialBundle(AccessToken: "test-token"));
        Assert.True(await store.ReplaceConversationGrantsAsync(ownerId,bootstrapId,2,["github-account"],[("github.issues.create","1")]));
        var remove = await SendJsonAsync(Owner, HttpMethod.Delete, "/api/v1/plugins/github/versions/1.0.0", new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal("plugin_in_use", (await ReadJsonAsync(remove)).GetProperty("code").GetString());

        var secondManifest = manifest with { Id = "github-secondary" };
        await store.AddPluginInstallationAsync(new(ownerId, "github-secondary", "1.0.0", "GitHub secondary", "Tessera", "hash-2",
            JsonSerializer.Serialize(secondManifest), "{}", true, now, now, 1));
        var guardSchedule = new JobSchedule("once", now.AddMinutes(2), null, "UTC", null);
        await store.AddJobAsync(new(ownerId, "job-plugin-guard", "Guard", "Read", "ACTIVE", "READY", null, guardSchedule,
            guardSchedule.At, "{}", [], [("github.issues.create", "1")], [], now, now, 1));
        var grantRemove = await SendJsonAsync(Owner, HttpMethod.Delete, "/api/v1/plugins/github-secondary/versions/1.0.0", new { expectedVersion = 1 });
        Assert.Equal("plugin_in_use", (await ReadJsonAsync(grantRemove)).GetProperty("code").GetString());

        var schedule = new JobSchedule("once", now.AddMinutes(1), null, "UTC", null);
        await store.AddJobAsync(new(ownerId, "job-1", "Job", "Read", "ACTIVE", "READY", null, schedule, schedule.At, "{}", [], [], [], now, now, 1));
        var run = await store.CreateRunOccurrenceAsync(ownerId, "job-1", now);
        Assert.NotNull(run);
        var runs = await ReadJsonAsync(await SendAsync(Owner, HttpMethod.Get, "/api/v1/jobs/job-1/runs"));
        Assert.Single(runs.GetProperty("items").EnumerateArray());
        var manual=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/jobs/job-1/run",new{expectedVersion=1},"manual-run-key"));var manualId=manual.GetProperty("id").GetString();var manualReplay=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/jobs/job-1/run",new{expectedVersion=1},"manual-run-key"));Assert.Equal(manualId,manualReplay.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Get, $"/api/v1/job-runs/{run.RunId}")).StatusCode);
        foreach (var child in new[] { "capability-uses", "account-uses", "actions", "outputs", "evidence", "trace" })
        {
            var response = await SendAsync(Owner, HttpMethod.Get, $"/api/v1/job-runs/{run.RunId}/{child}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await ReadJsonAsync(response)).GetProperty("items").GetArrayLength() <= 100);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(Other, HttpMethod.Get, $"/api/v1/job-runs/{run.RunId}")).StatusCode);

    }

    [Fact]
    public async Task Read_only_local_capability_executes_through_coordinator_and_creates_owner_evidence()
    {
        var principal = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow);
        var ownerId = principal.PrincipalId;
        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(principal);
        var manifest = new PluginManifest("local", "1.0.0", "Local utilities", "Tessera", "2.0.0",
            [new("local.time", "1", "Current date and time", "local-date-time", false, [], "ReadOnly", 1000, 4096)]);
        await store.AddPluginInstallationAsync(new(ownerId, "local", "1.0.0", "Local utilities", "Tessera", "hash-local",
            JsonSerializer.Serialize(manifest), "{}", true, now, now, 1));
        var conversationResponse = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/conversations", new
        {
            title = "Capability result",
            modelProfileId = (string?)null,
        }, "capability-conversation");
        var conversationId = (await ReadJsonAsync(conversationResponse)).GetProperty("id").GetString()!;

        var response = await SendJsonAsync(Owner, HttpMethod.Post, "/api/v1/capabilities/local.time/invoke", new
        {
            capabilityId = "local.time",
            capabilityVersion = "1",
            pluginId = "local",
            pluginVersion = "1.0.0",
            accountId = (string?)null,
            target = "UTC",
            input = new { timeZone = "UTC" },
            conversationId,
            messageId = (string?)null,
        }, "local-time-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadJsonAsync(response);
        Assert.Equal("UTC", result.GetProperty("result").GetProperty("timeZone").GetString());
        var evidenceId = Assert.Single(result.GetProperty("evidenceRefs").EnumerateArray()).GetString();
        var executionId=result.GetProperty("executionId").GetString();
        Assert.Contains(await store.ListCapabilityCallsAsync(ownerId,null),call=>call.ExecutionId==executionId&&call.CapabilityId=="local.time"&&call.State=="SUCCEEDED");
        Assert.Contains(await store.ListEvidenceAsync(ownerId), item => item.EvidenceId == evidenceId
            && item.SourceType == "capability.result");
        Assert.Contains(await store.ListMessagesAsync(ownerId, conversationId), item => item.Role == "CAPABILITY"
            && item.Parts.Any(part => part.Kind == "CAPABILITY_RESULT"
                && part.EvidenceRefs!.Contains(evidenceId, StringComparer.Ordinal)));

        var other = await SendJsonAsync(Other, HttpMethod.Post, "/api/v1/capabilities/local.time/invoke", new
        {
            capabilityId = "local.time",
            capabilityVersion = "1",
            pluginId = "local",
            pluginVersion = "1.0.0",
            accountId = (string?)null,
            target = "UTC",
            input = new { timeZone = "UTC" },
        }, "other-local-time-key");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
    }

    [Fact]
    public async Task Chat_model_can_use_read_capability_and_persists_evidence_in_assistant_message()
    {
        await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Bootstrap",modelProfileId=(string?)null},"chat-bootstrap");
        var ownerId=PrincipalRef.Create("https://dev.tessera.local","dev",Owner,Owner,DateTimeOffset.UtcNow).PrincipalId;var store=_app.Services.GetRequiredService<SqliteKernelStore>();var now=DateTimeOffset.UtcNow;
        await store.AddPluginInstallationAsync(new(ownerId,"model-provider","1.0.0","Models","Tessera","model-hash",ModelManifest,"{}",true,now,now,1));
        var localManifest=new PluginManifest("local","1.0.0","Local utilities","Tessera","2.0.0",[new("local.time","1","Current date and time","local-date-time",false,[],"ReadOnly",1000,4096),new("local.memory.remember","1","Remember","explicit-memory",false,[],"LocalReversible",1000,4096)]);
        await store.AddPluginInstallationAsync(new(ownerId,"local","1.0.0","Local utilities","Tessera","local-hash",JsonSerializer.Serialize(localManifest),"{}",true,now,now,1));
        var credentialRef=ConnectedAccountCredentialRef.Create(ownerId,"model-tools");await store.AddConnectedAccountAsync(new(ownerId,"model-tools","openai-compatible","model-provider","1.0.0","Model",null,AccountLifecycle.Connected,credentialRef,AccountHealth.Healthy,null,"{\"endpoint\":\"https://models.example/v1\"}",[],[new("model-provider","1.0.0","model.chat.complete","1")],now,now,1));await _custody.PutBundleAsync(credentialRef,new CredentialBundle(AccessToken:"test-token"));
        await store.AddModelProfileAsync(new(ownerId,"profile-tools","model-tools","openai-compatible-remote","https://models.example/v1","test-model",8192,true,true,true,now,now,1));
        var conversation=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Tools",modelProfileId="profile-tools"},"chat-tools-conversation"));var conversationId=conversation.GetProperty("id").GetString()!;

        var response=await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{conversationId}/messages",new{text="Use the clock tool",modelProfileId="profile-tools"},"chat-tool-key");

        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var receipt=await ReadJsonAsync(response);var assistant=await WaitForMessageAsync(store,ownerId,conversationId,receipt.GetProperty("messageId").GetString()!);
        var capability=Assert.Single(assistant.Parts,part=>part.Kind=="CAPABILITY_RESULT");Assert.NotNull(capability.CapabilityCallId);var evidenceId=Assert.Single(capability.EvidenceRefs!);
        Assert.Contains(await store.ListEvidenceAsync(ownerId),item=>item.EvidenceId==evidenceId&&item.SourceType=="capability.result");
        Assert.Contains(assistant.Parts,part=>part.Kind=="TEXT"&&part.Text=="The clock result is ready.");
        var modelCall=Assert.Single(await store.ListCapabilityCallsAsync(ownerId,null),call=>call.CapabilityId=="model.chat.complete"&&call.ExecutionId==receipt.GetProperty("executionId").GetString());Assert.DoesNotContain("Use the clock tool",modelCall.InputJson,StringComparison.Ordinal);Assert.Contains("promptPersisted",modelCall.InputJson,StringComparison.Ordinal);
        var requests=_transport.ModelRequestCount;var replay=await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{conversationId}/messages",new{text="Use the clock tool",modelProfileId="profile-tools"},"chat-tool-key");Assert.Equal(HttpStatusCode.Accepted,replay.StatusCode);Assert.True((await ReadJsonAsync(replay)).GetProperty("replayed").GetBoolean());Assert.Equal(requests,_transport.ModelRequestCount);Assert.Equal(2,(await store.ListMessagesAsync(ownerId,conversationId)).Count);
        Assert.Equal(HttpStatusCode.Conflict,(await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{conversationId}/messages",new{text="Different request",modelProfileId="profile-tools"},"chat-tool-key")).StatusCode);Assert.Equal(requests,_transport.ModelRequestCount);
        var waiting=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{conversationId}/messages",new{text="Wait until stopped",modelProfileId="profile-tools"},"chat-stop-key"));
        var active=await ReadJsonAsync(await SendAsync(Owner,HttpMethod.Get,$"/api/v1/conversations/{conversationId}/active-execution"));Assert.Equal(waiting.GetProperty("executionId").GetString(),active.GetProperty("executionId").GetString());
        var stop=await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{conversationId}/stop",new{executionId=waiting.GetProperty("executionId").GetString()},"chat-stop-command");Assert.Equal(HttpStatusCode.Accepted,stop.StatusCode);var stopped=await WaitForMessageAsync(store,ownerId,conversationId,waiting.GetProperty("messageId").GetString()!);Assert.Equal("STOPPED",stopped.Status);Assert.Contains(stopped.Parts,part=>part.ErrorCode=="execution_stopped");Assert.Equal(HttpStatusCode.NoContent,(await SendAsync(Owner,HttpMethod.Get,$"/api/v1/conversations/{conversationId}/active-execution")).StatusCode);
        var rememberReceipt=await ReadJsonAsync(await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/conversations/{conversationId}/messages",new{text="Remember that I prefer morning appointments",modelProfileId="profile-tools"},"chat-memory-key"));await WaitForMessageAsync(store,ownerId,conversationId,rememberReceipt.GetProperty("messageId").GetString()!);var memoryAction=Assert.Single(await store.ListByStateAsync(ownerId,ActionState.Proposed),item=>item.CapabilityId=="local.memory.remember");var memoryApproved=await SendJsonAsync(Owner,HttpMethod.Post,$"/api/v1/actions/{memoryAction.ActionId}/approve",new{expectedVersion=memoryAction.Version},"memory-approval");Assert.Equal(HttpStatusCode.Accepted,memoryApproved.StatusCode);Assert.Equal("EXECUTION_SUCCEEDED",(await ReadJsonAsync(memoryApproved)).GetProperty("state").GetString());Assert.Contains(await store.ListMemoryAsync(ownerId,false),item=>item.Value=="morning");Assert.Contains(await store.ListMessagesAsync(ownerId,conversationId),message=>message.Role=="SYSTEM_EVENT"&&message.Parts.Any(part=>part.ActionId==memoryAction.ActionId));
    }

    [Fact]
    public async Task Durable_job_uses_granted_read_tool_and_exposes_real_run_projections()
    {
        await SendJsonAsync(Owner,HttpMethod.Post,"/api/v1/conversations",new{title="Bootstrap",modelProfileId=(string?)null},"job-bootstrap");var ownerId=PrincipalRef.Create("https://dev.tessera.local","dev",Owner,Owner,DateTimeOffset.UtcNow).PrincipalId;var store=_app.Services.GetRequiredService<SqliteKernelStore>();var now=DateTimeOffset.UtcNow;
        await store.AddPluginInstallationAsync(new(ownerId,"model-provider","1.0.0","Models","Tessera","model-hash",ModelManifest,"{}",true,now,now,1));var localManifest=new PluginManifest("local","1.0.0","Local utilities","Tessera","2.0.0",[new("local.time","1","Current date and time","local-date-time",false,[],"ReadOnly",1000,4096)]);await store.AddPluginInstallationAsync(new(ownerId,"local","1.0.0","Local utilities","Tessera","local-hash",JsonSerializer.Serialize(localManifest),"{}",true,now,now,1));
        var credentialRef=ConnectedAccountCredentialRef.Create(ownerId,"job-model");await store.AddConnectedAccountAsync(new(ownerId,"job-model","openai-compatible","model-provider","1.0.0","Model",null,AccountLifecycle.Connected,credentialRef,AccountHealth.Healthy,null,"{\"endpoint\":\"https://models.example/v1\"}",[],[new("model-provider","1.0.0","model.chat.complete","1")],now,now,1));await _custody.PutBundleAsync(credentialRef,new CredentialBundle(AccessToken:"test-token"));await store.AddModelProfileAsync(new(ownerId,"job-profile","job-model","openai-compatible-remote","https://models.example/v1","test-model",8192,true,true,true,now,now,1));
        await new R2MemoryService(store,store).RememberAsync(ownerId,"user","appointment.preference","morning","job-memory",now);
        var schedule=new JobSchedule("once",now,null,"UTC",null);await store.AddJobAsync(new(ownerId,"job-tools","Tool Job","Use the clock tool","ACTIVE","READY","job-profile",schedule,null,"{}",["job-model"],[("model.chat.complete","1"),("local.time","1")],[],now,now,1));var run=await store.CreateRunOccurrenceAsync(ownerId,"job-tools",now);Assert.NotNull(run);
        Assert.Empty(await store.ListRemoteHostsAsync(ownerId));
        var interruptedJob=await store.GetJobAsync(ownerId,"job-tools");var interruptedTools=await R2ProductEndpoints.JobToolsAsync(store,interruptedJob!,CancellationToken.None);var interruptedPrompt="User-authored state (quoted data):\n- user appointment.preference: morning\n\nJob instruction:\nUse the clock tool";using(var interruptedInput=JsonDocument.Parse(JsonSerializer.Serialize(new{prompt=interruptedPrompt,tools=interruptedTools.Definitions}))){var interrupted=new ExecutionRequest(ownerId,run!.RunId,"model.chat.complete","1","model-provider","1.0.0","job-model","test-model",ActionPayloadHash.Compute(Encoding.UTF8.GetBytes("https://models.example/v1")),interruptedInput.RootElement.Clone(),run.RunId,JobId:"job-tools",JobRunId:run.RunId);await store.BeginCapabilityCallAsync(interrupted,now);Assert.True(await store.TryStartCapabilityCallAsync(interrupted,now));}

        await new R2SchedulerService(store,_custody,new ModelTransport(),NullLogger<R2SchedulerService>.Instance).DispatchQueuedAsync(CancellationToken.None);

        var completedRun=await store.GetJobRunAsync(ownerId,run!.RunId);Assert.True(completedRun!.State=="SUCCEEDED",completedRun.ErrorCode);Assert.NotNull(completedRun.ContextSnapshotRef);Assert.Single(await store.ListJobRunOutputsAsync(ownerId,run.RunId));var calls=await store.ListCapabilityCallsAsync(ownerId,run.RunId);Assert.Equal(3,calls.Count);Assert.Contains(calls,call=>call.CapabilityId=="local.time"&&call.State=="SUCCEEDED");var results=await store.ListCapabilityResultsAsync(ownerId,run.RunId);var evidenceId=Assert.Single(results.Single(result=>result.CallId.EndsWith(":clock-1",StringComparison.Ordinal)).EvidenceRefs);Assert.NotNull(await ((IEvidenceRepository)store).GetAsync(ownerId,evidenceId));
        Assert.Empty(await store.ListRemoteHostsAsync(ownerId));
        var detail=await ReadJsonAsync(await SendAsync(Owner,HttpMethod.Get,$"/api/v1/job-runs/{run.RunId}"));Assert.Equal(3,detail.GetProperty("capabilityUses").GetProperty("items").GetArrayLength());Assert.Equal(2,detail.GetProperty("accountUses").GetProperty("items").GetArrayLength());Assert.Single(detail.GetProperty("evidence").GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Fresh_chat_worker_recovers_persisted_running_execution_after_restart()
    {
        await _app.Services.GetRequiredService<R2ChatExecutionQueue>()
            .StopAsync(CancellationToken.None);
        var owner=PrincipalRef.Create("https://dev.tessera.local","dev","recovery@example.com","recovery@example.com",DateTimeOffset.UtcNow);var store=_app.Services.GetRequiredService<SqliteKernelStore>();await store.AddAsync(owner);var now=DateTimeOffset.UtcNow;await store.AddPluginInstallationAsync(new(owner.PrincipalId,"model-provider","1.0.0","Models","Tessera","model-hash",ModelManifest,"{}",true,now,now,1));var credentialRef=ConnectedAccountCredentialRef.Create(owner.PrincipalId,"recovery-model");await store.AddConnectedAccountAsync(new(owner.PrincipalId,"recovery-model","openai-compatible","model-provider","1.0.0","Model",null,AccountLifecycle.Connected,credentialRef,AccountHealth.Healthy,null,"{\"endpoint\":\"https://models.example/v1\"}",[],[new("model-provider","1.0.0","model.chat.complete","1")],now,now,1));await _custody.PutBundleAsync(credentialRef,new CredentialBundle(AccessToken:"token"));await store.AddModelProfileAsync(new(owner.PrincipalId,"recovery-profile","recovery-model","openai-compatible-remote","https://models.example/v1","test-model",8192,true,true,true,now,now,1));await store.AddConversationAsync(new(owner.PrincipalId,"recovery-conversation","Recovery","ACTIVE","recovery-profile",now,now,1));Assert.True(await store.ReplaceConversationGrantsAsync(owner.PrincipalId,"recovery-conversation",1,["recovery-model"],[("model.chat.complete","1")]));await store.AddMessageAsync(new(owner.PrincipalId,"recovery-user","recovery-conversation","USER","PERSISTED",null,[new("recovery-part",1,"TEXT","Recover this turn")],now,null,1));await store.StartExecutionAsync(owner.PrincipalId,"recovery-conversation","recovery-execution","recovery-user",now);
        var atomicMessage=new ChatMessage(owner.PrincipalId,"recovery-atomic-user","recovery-conversation","USER","PERSISTED",null,[new("recovery-atomic-part",1,"TEXT","Recover this turn")],now,null,1);var atomicEvent=new PublicExecutionEvent(owner.PrincipalId,"recovery-event","recovery-atomic-execution",1,"status",now,atomicMessage.MessageId,null,null,"{\"label\":\"queued\"}");Assert.True(await store.AcceptChatExecutionAsync(atomicMessage,"recovery-atomic-execution","recovery-profile","recovery-key",atomicEvent));
        using(var interruptedInput=JsonDocument.Parse("""{"prompt":"Recover this turn","tools":[]}""")){var interrupted=new ExecutionRequest(owner.PrincipalId,"recovery-atomic-execution","model.chat.complete","1","model-provider","1.0.0","recovery-model","test-model",ActionPayloadHash.Compute(Encoding.UTF8.GetBytes("https://models.example/v1")),interruptedInput.RootElement.Clone(),"recovery-key",ConversationId:"recovery-conversation",MessageId:"recovery-atomic-user");await store.BeginCapabilityCallAsync(interrupted,now);Assert.True(await store.TryStartCapabilityCallAsync(interrupted,now));}
        var worker=new R2ChatExecutionQueue(store,_custody,new ModelTransport(),new R2LiveExecutionEvents(),NullLogger<R2ChatExecutionQueue>.Instance);await worker.StartAsync(CancellationToken.None);
        try
        {
            for(var attempt=0;attempt<100&&(await store.ListMessagesAsync(owner.PrincipalId,"recovery-conversation")).All(message=>message.Role!="ASSISTANT");attempt++)await Task.Delay(20);
            var assistant=(await store.ListMessagesAsync(owner.PrincipalId,"recovery-conversation")).Single(message=>message.Role=="ASSISTANT");Assert.True(assistant.Status=="COMPLETED",assistant.Parts.FirstOrDefault(part=>part.ErrorCode is not null)?.ErrorCode);Assert.False(await store.IsExecutionStoppedAsync(owner.PrincipalId,"recovery-atomic-execution"));Assert.Contains(await store.ListCapabilityCallsAsync(owner.PrincipalId,null),call=>call.ExecutionId=="recovery-atomic-execution"&&call.State=="SUCCEEDED");
        }
        finally{await worker.StopAsync(CancellationToken.None);worker.Dispose();}
    }

    private Task<HttpResponseMessage> SendAsync(string principal,HttpMethod method,string path)
        =>SendAsync(principal,new HttpRequestMessage(method,new Uri(path,UriKind.Relative)));

    private async Task<HttpResponseMessage> SendJsonAsync(string principal,HttpMethod method,string path,object body,string? idempotencyKey=null)
    {var request=new HttpRequestMessage(method,new Uri(path,UriKind.Relative)){Content=JsonContent.Create(body)};if(idempotencyKey is not null)request.Headers.Add("Idempotency-Key",idempotencyKey);return await SendAsync(principal,request);}

    private async Task<HttpResponseMessage> SendAsync(string principal,HttpRequestMessage request)
    {request.Headers.Add(DevHeader,principal);return await _client.SendAsync(request);}

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {var text=await response.Content.ReadAsStringAsync();return JsonDocument.Parse(text).RootElement.Clone();}

    private static async Task<ChatMessage> WaitForMessageAsync(SqliteKernelStore store,string owner,string conversation,string messageId)
    {for(var attempt=0;attempt<100;attempt++){var value=(await store.ListMessagesAsync(owner,conversation)).SingleOrDefault(message=>message.MessageId==messageId);if(value is not null)return value;await Task.Delay(20);}var messages=await store.ListMessagesAsync(owner,conversation);var events=await store.ListConversationEventsAsync(owner,conversation,0);throw new TimeoutException($"The durable Chat worker did not persist its assistant message. Messages={JsonSerializer.Serialize(messages.Select(item=>new{item.MessageId,item.Role,item.Status}))}; Events={JsonSerializer.Serialize(events.Select(item=>new{item.Sequence,item.EventType,item.ExecutionId}))}");}

    private static int FreePort()
    {var listener=new TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}

    private sealed class ModelTransport : IHttpTransport,IStreamingHttpTransport
    {
        public int GitHubPostCount { get; private set; }
        public int GmailPostCount { get; private set; }
        public int ModelRequestCount { get; private set; }
        public Task<TransportResponse> SendAsync(string method,string url,IReadOnlyDictionary<string,string> headers,string? body,CancellationToken cancellationToken=default)
        {
            if (url.Contains("api.github.com", StringComparison.Ordinal))
            {
                if(url.EndsWith("/user",StringComparison.Ordinal))
                    return Task.FromResult(new TransportResponse(200,
                        headers.GetValueOrDefault("Authorization")?.Contains("fine-token",StringComparison.Ordinal)==true
                            ?new Dictionary<string,string>()
                            :new Dictionary<string,string>{{"X-OAuth-Scopes","repo, read:user"}},
                        "{\"id\":42,\"login\":\"octo\",\"name\":\"Octo Cat\"}"));
                if(method=="POST")GitHubPostCount++;
                var responseHeaders = method == "POST"
                    ? new Dictionary<string,string> { ["Location"] = "https://api.github.com/repos/owner/repo/issues/42" }
                    : new Dictionary<string,string>();
                return Task.FromResult(new TransportResponse(method == "POST" ? 201 : 200, responseHeaders, "{\"number\":42}"));
            }
            if(url.Contains("gmail.googleapis.com",StringComparison.Ordinal))
            {
                if(url.EndsWith("/profile",StringComparison.Ordinal))return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"{\"emailAddress\":\"user@example.com\",\"messagesTotal\":10,\"threadsTotal\":7,\"historyId\":\"12345\"}"));
                if(method=="POST"&&url.EndsWith("/messages/send",StringComparison.Ordinal)){GmailPostCount++;return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"{\"id\":\"sent_1\",\"threadId\":\"sent_thread\",\"labelIds\":[\"SENT\"]}"));}
                if(url.Contains("/messages?",StringComparison.Ordinal))return Task.FromResult(url.Contains("newer_than%3A1d",StringComparison.Ordinal)?new TransportResponse(200,new Dictionary<string,string>(),"{\"messages\":[],\"resultSizeEstimate\":0}"):new TransportResponse(200,new Dictionary<string,string>(),"{\"messages\":[{\"id\":\"gmail_1\",\"threadId\":\"thread_1\"}],\"resultSizeEstimate\":1}"));
                if(url.Contains("/messages/sent_1?",StringComparison.Ordinal))return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"{\"id\":\"sent_1\",\"threadId\":\"sent_thread\",\"labelIds\":[\"SENT\"],\"payload\":{\"mimeType\":\"text/plain\",\"headers\":[],\"body\":{\"size\":0,\"data\":\"\"}}}"));
                if(url.Contains("/messages/gmail_1?",StringComparison.Ordinal))return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),url.Contains("format=full",StringComparison.Ordinal)?"{\"id\":\"gmail_1\",\"threadId\":\"thread_1\",\"labelIds\":[\"INBOX\",\"UNREAD\"],\"internalDate\":\"1786406400000\",\"payload\":{\"mimeType\":\"text/plain\",\"headers\":[{\"name\":\"From\",\"value\":\"Sender <sender@example.com>\"},{\"name\":\"Subject\",\"value\":\"Attention needed\"}],\"body\":{\"size\":11,\"data\":\"SGVsbG8gd29ybGQ\"}}}":"{\"id\":\"gmail_1\",\"threadId\":\"thread_1\",\"labelIds\":[\"INBOX\",\"UNREAD\"],\"internalDate\":\"1786406400000\",\"payload\":{\"headers\":[{\"name\":\"From\",\"value\":\"Sender <sender@example.com>\"},{\"name\":\"Subject\",\"value\":\"Attention needed\"}]}}"));
                return Task.FromResult(new TransportResponse(404,new Dictionary<string,string>(),"{}"));
            }
            ModelRequestCount++;
            var current=CurrentPrompt(body);var continuation=body?.Contains("tool_call_id",StringComparison.Ordinal)==true;
            if(IsCurrent(current,"Wait until stopped"))return WaitForCancellation(cancellationToken);
            if(IsCurrent(current,"Remember that I prefer morning appointments")&&!continuation)
                return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"""{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"memory-1","type":"function","function":{"name":"remember_memory","arguments":"{\"subjectKey\":\"user\",\"predicate\":\"appointment.preference\",\"value\":\"morning\"}"}}]}}]}"""));
            if(IsCurrent(current,"Use the clock tool")&&!continuation)
                return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"""{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"clock-1","type":"function","function":{"name":"current_time","arguments":"{\"timeZone\":\"UTC\"}"}}]}}]}"""));
            if(IsCurrent(current,"Prepare the issue tool")&&!continuation)
                return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"""{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"issue-1","type":"function","function":{"name":"create_github_issue","arguments":"{\"repository\":\"owner/repo\",\"title\":\"Review me\",\"body\":\"Created only after approval\"}"}}]}}]}"""));
            if(continuation)
                return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"""{"choices":[{"message":{"role":"assistant","content":"The clock result is ready."}}]}"""));
            return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"{\"choices\":[{\"message\":{\"content\":\"retried\"}}]}"));
        }

        private static async Task<TransportResponse> WaitForCancellation(CancellationToken token)
        {await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new InvalidOperationException("Cancellation was expected.");}

        private static string? CurrentPrompt(string? body)
        {if(string.IsNullOrWhiteSpace(body))return null;try{using var document=JsonDocument.Parse(body);return document.RootElement.GetProperty("messages").EnumerateArray().Last(item=>item.GetProperty("role").GetString()=="user").GetProperty("content").GetString();}catch(Exception exception)when(exception is JsonException or KeyNotFoundException or InvalidOperationException){return null;}}
        private static bool IsCurrent(string? prompt,string expected)=>prompt==expected||prompt?.EndsWith($"Current user request:\n{expected}",StringComparison.Ordinal)==true||prompt?.EndsWith($"Job instruction:\n{expected}",StringComparison.Ordinal)==true;

        public async Task<StreamingTransportResponse> SendStreamingAsync(string method,string url,IReadOnlyDictionary<string,string> headers,string? body,Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask> onChunk,int maximumResponseBytes,CancellationToken cancellationToken=default)
        {
            var response=await SendAsync(method,url,headers,body,cancellationToken);if(response.Status is <200 or >=300)return new(response.Status,response.Headers,response.Body);
            using var document=JsonDocument.Parse(response.Body);var message=document.RootElement.GetProperty("choices")[0].GetProperty("message");object delta;
            if(message.TryGetProperty("tool_calls",out var calls))
                delta=new{tool_calls=calls.EnumerateArray().Select((call,index)=>new{index,id=call.GetProperty("id").GetString(),type="function",function=new{name=call.GetProperty("function").GetProperty("name").GetString(),arguments=call.GetProperty("function").GetProperty("arguments").GetString()}}).ToArray()};
            else delta=new{content=message.GetProperty("content").GetString()};
            var stream=$"data: {JsonSerializer.Serialize(new{choices=new[]{new{delta}}})}\n\ndata: [DONE]\n\n";var bytes=Encoding.UTF8.GetBytes(stream);var split=Math.Max(1,bytes.Length/2);
            await onChunk(bytes.AsMemory(0,split),cancellationToken);await Task.Delay(100,cancellationToken);await onChunk(bytes.AsMemory(split),cancellationToken);
            return new(200,response.Headers,null);
        }
    }

    private const string ModelManifest = """{"Id":"model-provider","Version":"1.0.0","Name":"Models","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"model.chat.complete","Version":"1","Description":"Complete","ExecutorKind":"openai-compatible","AccountRequired":true,"RequiredPermissions":[],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":120000,"MaxResultBytes":1048576}]}""";
}