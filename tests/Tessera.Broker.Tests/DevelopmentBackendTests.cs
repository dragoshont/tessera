using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class DevelopmentBackendTests
{
    private const string DevHeader = "X-Tessera-Dev-Principal";

    [Fact]
    public async Task Development_routes_scope_workspaces_reject_profiles_and_replay_exact_task()
    {
        var directory = Directory.CreateTempSubdirectory("tessera-development-api-test").FullName;
        WebApplication? app = null;
        HttpClient? client = null;
        try
        {
            var port = FreePort();
            var configPath = Path.Combine(directory, "tessera.json");
            await File.WriteAllTextAsync(configPath, $$"""
                { "server": { "host": "127.0.0.1", "port": {{port}} },
                  "identity": { "mode": "dev", "trustDomain": "tessera.local" },
                  "policy": { "default": "deny" }, "audit": { "enabled": false } }
                """);
            var policyPath = Path.Combine(directory, "grants.json");
            await File.WriteAllTextAsync(policyPath, "{ \"grants\": [], \"bindings\": [], \"recipes\": [] }");
            var executor = new RecordingDevelopmentExecutor();
            app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
            {
                ConfigPath = configPath,
                PolicyPath = policyPath,
                ProductDatabasePath = Path.Combine(directory, "product.db"),
                DevelopmentExecutor = ExecutorOptions(),
                DevelopmentExecutorOverride = executor,
            });
            var now = DateTimeOffset.UtcNow;
            var owner = PrincipalRef.Create("https://dev.tessera.local", "dev", "owner@example.com", "owner@example.com", now);
            var other = PrincipalRef.Create("https://dev.tessera.local", "dev", "other@example.com", "other@example.com", now);
            var store = app.Services.GetRequiredService<SqliteKernelStore>();
            await store.AddAsync(owner);
            await store.AddAsync(other);
            await store.AddConversationAsync(new(owner.PrincipalId, "conversation-1", "Development", "ACTIVE", null, now, now, 1));
            await store.AddConversationAsync(new(other.PrincipalId, "conversation-1", "Other", "ACTIVE", null, now, now, 1));
            await store.RegisterDevelopmentWorkspaceAsync(new(owner.PrincipalId, "workspace-1", "conversation-1",
                "Repository", "snapshot/one", "sha256:snapshot", "READY", now, 1));
            await store.RegisterDevelopmentWorkspaceAsync(new(other.PrincipalId, "workspace-other", "conversation-1",
                "Other repository", "snapshot/other", "sha256:other", "READY", now, 1));
            await app.StartAsync();
            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using var list = await SendAsync(client, "owner@example.com", HttpMethod.Get,
                "/api/v1/conversations/conversation-1/development-workspaces");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var workspace = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("workspace-1", workspace.GetProperty("id").GetString());
            Assert.False(workspace.TryGetProperty("snapshotRef", out _));

            using var rejected = await SendJsonAsync(client, "owner@example.com",
                "/api/v1/conversations/conversation-1/development-tasks",
                new { name = "Write", workspaceId = "workspace-1", commandProfile = "repository.write", arguments = Array.Empty<string>() }, "write-key");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal("development_command_not_allowed", await ErrorCode(rejected));

            using var hidden = await SendJsonAsync(client, "owner@example.com",
                "/api/v1/conversations/conversation-1/development-tasks",
                new { name = "Status", workspaceId = "workspace-other", commandProfile = "repository.status", arguments = Array.Empty<string>() }, "hidden-key");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal("not_found", await ErrorCode(hidden));

            var body = new { name = "Status", workspaceId = "workspace-1", commandProfile = "repository.status", arguments = Array.Empty<string>() };
            using var first = await SendJsonAsync(client, "owner@example.com",
                "/api/v1/conversations/conversation-1/development-tasks", body, "task-key");
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());
            var firstBody = await first.Content.ReadAsStringAsync();
            using var firstJson = JsonDocument.Parse(firstBody);
            Assert.Equal("DEVELOPMENT", firstJson.RootElement.GetProperty("job").GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, firstJson.RootElement.GetProperty("job").GetProperty("modelProfileId").ValueKind);

            using var replay = await SendJsonAsync(client, "owner@example.com",
                "/api/v1/conversations/conversation-1/development-tasks", body, "task-key");
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());

            using var conflict = await SendJsonAsync(client, "owner@example.com",
                "/api/v1/conversations/conversation-1/development-tasks", body with { name = "Changed" }, "task-key");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("idempotency_conflict", await ErrorCode(conflict));
        }
        finally
        {
            client?.Dispose();
            if (app is not null) await app.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Scheduler_dispatches_without_model_and_persists_bounded_redacted_output_and_event()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tessera-development-scheduler-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteKernelStore(path);
            await store.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var owner = PrincipalRef.Create("https://issuer.example", "tenant", "owner", "owner", now);
            await store.AddAsync(owner);
            await store.AddConversationAsync(new(owner.PrincipalId, "conversation-1", "Development", "ACTIVE", null, now, now, 1));
            await store.RegisterDevelopmentWorkspaceAsync(new(owner.PrincipalId, "workspace-1", "conversation-1",
                "Repository", "snapshot/one", "sha256:snapshot", "READY", now, 1));
            Assert.True(DevelopmentCommandProfiles.TryResolve("repository.status", [], out var profile));
            var hash = DevelopmentCommandProfiles.CanonicalRequestHash("Status", "workspace-1", profile!.Id, []);
            var task = await store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-1", hash,
                "job-1", "run-1", "Status", "workspace-1", profile, ExecutorOptions().ImageDigest, now);
            Assert.NotNull(task.Creation);
            var executor = new RecordingDevelopmentExecutor
            {
                Result = new("SUCCEEDED",
                    Encoding.UTF8.GetBytes("Authorization: Bearer secret-value\0\ntoken=second-secret\n" + new string('x', 40_000))),
                Release = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var scheduler = new R2SchedulerService(store, new InMemoryCredentialStore(), new NoopTransport(),
                NullLogger<R2SchedulerService>.Instance, developmentExecutor: executor);

            await scheduler.DispatchQueuedAsync(CancellationToken.None);
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("RUNNING", (await store.GetJobRunAsync(owner.PrincipalId, "run-1"))!.State);
            executor.Release.SetResult();
            await scheduler.WaitForDevelopmentDispatchesAsync();

            Assert.Equal("SUCCEEDED", (await store.GetJobRunAsync(owner.PrincipalId, "run-1"))!.State);
            Assert.Single(executor.Requests);
            var outputs = await store.ListJobRunOutputsAsync(owner.PrincipalId, "run-1");
            Assert.Equal("DEVELOPMENT_LOG", Assert.Single(outputs).Kind);
            Assert.InRange(outputs.Sum(item => Encoding.UTF8.GetByteCount(item.Text!)), 1, 32 * 1024);
            Assert.DoesNotContain("secret-value", string.Join('\n', outputs.Select(item => item.Text)), StringComparison.Ordinal);
            Assert.Contains(outputs, item => item.Truncated);
            var systemEvent = Assert.Single(await store.ListMessagesAsync(owner.PrincipalId, "conversation-1"));
            Assert.Equal("SYSTEM_EVENT", systemEvent.Role);
            Assert.Contains("output:run-1:log", Assert.Single(systemEvent.Parts).Text, StringComparison.Ordinal);

            executor.Result = new("UNKNOWN", [], "development_executor_outcome_unknown");
            var secondHash = DevelopmentCommandProfiles.CanonicalRequestHash("Reconcile", "workspace-1", profile.Id, []);
            var second = await store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-2", secondHash,
                "job-2", "run-2", "Reconcile", "workspace-1", profile, ExecutorOptions().ImageDigest, now.AddSeconds(1));
            Assert.NotNull(second.Creation);
            await scheduler.DispatchQueuedAsync(CancellationToken.None);
            await scheduler.WaitForDevelopmentDispatchesAsync();
            var unknownRun = await store.GetJobRunAsync(owner.PrincipalId, "run-2");
            Assert.Equal("RECONCILIATION_REQUIRED", unknownRun!.State);
            Assert.Equal("development_executor_outcome_unknown", unknownRun.ErrorCode);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public void Kubernetes_job_shape_is_direct_argv_non_privileged_and_server_owned()
    {
        var options = ExecutorOptions();
        using var client = new HttpClient { BaseAddress = new Uri(options.ApiServer) };
        var adapter = new KubernetesDevelopmentExecutor(client, options, _ => Task.FromResult("opaque"));
        Assert.True(DevelopmentCommandProfiles.TryResolve("repository.status", [], out var profile));
        var request = new DevelopmentExecutionRequest("owner", "conversation", "job", "run-123", "workspace",
            "snapshot/one", profile!, []);
        using var job = adapter.BuildJob(request, options.Snapshots[0]);
        var json = job.RootElement;
        var pod = json.GetProperty("spec").GetProperty("template").GetProperty("spec");
        Assert.False(pod.GetProperty("automountServiceAccountToken").GetBoolean());
        Assert.True(pod.GetProperty("securityContext").GetProperty("runAsNonRoot").GetBoolean());
        Assert.Equal("RuntimeDefault", pod.GetProperty("securityContext").GetProperty("seccompProfile").GetProperty("type").GetString());
        var command = pod.GetProperty("containers")[0];
        Assert.Equal("/usr/bin/git", command.GetProperty("command")[0].GetString());
        Assert.Equal(["status", "--short", "--branch"], command.GetProperty("args").EnumerateArray().Select(item => item.GetString()));
        var environment = command.GetProperty("env").EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, item => item.GetProperty("value").GetString());
        Assert.Equal("safe.directory", environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("/workspace", environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal(options.ImageDigest, command.GetProperty("image").GetString());
        Assert.False(command.GetProperty("securityContext").GetProperty("allowPrivilegeEscalation").GetBoolean());
        Assert.False(command.GetProperty("securityContext").GetProperty("privileged").GetBoolean());
        Assert.True(command.GetProperty("securityContext").GetProperty("readOnlyRootFilesystem").GetBoolean());
        Assert.Equal("ALL", command.GetProperty("securityContext").GetProperty("capabilities").GetProperty("drop")[0].GetString());
        var serialized = json.GetRawText();
        Assert.DoesNotContain("hostPath", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker.sock", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/bin/sh", serialized, StringComparison.Ordinal);
        Assert.Contains("\"tessera.dev/network\":\"deny-all\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kubernetes_adapter_reconciles_existing_job_and_reports_unknown_absence_without_duplicate_create()
    {
        var options = ExecutorOptions();
        Assert.True(DevelopmentCommandProfiles.TryResolve("repository.status", [], out var profile));
        var request = new DevelopmentExecutionRequest("owner", "conversation", "job", "run-123", "workspace",
            "snapshot/one", profile!, []);
        var reconciledHandler = new SequenceHandler(
            _ => new(HttpStatusCode.Conflict),
            _ => Json(HttpStatusCode.OK, "{\"metadata\":{\"name\":\"tessera-dev-run-123\"}}"),
            _ => Json(HttpStatusCode.OK, "{\"status\":{\"succeeded\":1}}"),
            _ => Json(HttpStatusCode.OK, "{\"items\":[{\"metadata\":{\"name\":\"pod-1\"}}]}"),
            _ => Json(HttpStatusCode.OK, "combined command log"));
        using (var client = new HttpClient(reconciledHandler) { BaseAddress = new Uri(options.ApiServer) })
        {
            var adapter = new KubernetesDevelopmentExecutor(client, options, _ => Task.FromResult(" token\n"));
            var result = await adapter.ExecuteAsync(request);
            Assert.Equal("SUCCEEDED", result.Outcome);
            Assert.Equal("combined command log", Encoding.UTF8.GetString(result.Log));
            Assert.Equal(1, reconciledHandler.Requests.Count(item => item.Method == HttpMethod.Post));
            Assert.Single(reconciledHandler.Requests, item => item.Path.Contains("/log?container=command", StringComparison.Ordinal));
            Assert.DoesNotContain(reconciledHandler.Requests, item => item.Path.Contains("stream=", StringComparison.Ordinal));
        }

        var emptyPodsHandler = new SequenceHandler(
            _ => Json(HttpStatusCode.Created, "{}"),
            _ => Json(HttpStatusCode.OK, "{\"status\":{\"succeeded\":1}}"),
            _ => Json(HttpStatusCode.OK, "{\"items\":[]}"));
        using (var client = new HttpClient(emptyPodsHandler) { BaseAddress = new Uri(options.ApiServer) })
        {
            var adapter = new KubernetesDevelopmentExecutor(client, options, _ => Task.FromResult("token"));
            var result = await adapter.ExecuteAsync(request);
            Assert.Equal("FAILED", result.Outcome);
            Assert.Equal("development_executor_output_unavailable", result.ErrorCode);
        }

        var unknownHandler = new SequenceHandler(
            _ => throw new HttpRequestException("ambiguous create"),
            _ => new(HttpStatusCode.NotFound));
        using (var client = new HttpClient(unknownHandler) { BaseAddress = new Uri(options.ApiServer) })
        {
            var adapter = new KubernetesDevelopmentExecutor(client, options, _ => Task.FromResult("token"));
            var result = await adapter.ExecuteAsync(request);
            Assert.Equal("UNKNOWN", result.Outcome);
            Assert.Equal("development_executor_outcome_unknown", result.ErrorCode);
            Assert.Equal(1, unknownHandler.Requests.Count(item => item.Method == HttpMethod.Post));
        }
    }

    private static DevelopmentExecutorOptions ExecutorOptions() => new(
        "https://kubernetes.default.svc/", "tessera-development", "executor@sha256:abcdef",
        65532, 65532, "500m", "512Mi", "1Gi",
        [new("snapshot/one", "reviewed-snapshots", "one"), new("snapshot/other", "reviewed-snapshots", "other")]);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string principal, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DevHeader, principal);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, string principal, string path, object body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(DevHeader, principal);
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingDevelopmentExecutor : IDevelopmentExecutor
    {
        public List<DevelopmentExecutionRequest> Requests { get; } = [];
        public DevelopmentExecutionResult Result { get; set; } = new("SUCCEEDED", []);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }

        public async Task<DevelopmentExecutionResult> ExecuteAsync(
            DevelopmentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Started.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            return Result;
        }
    }

    private sealed class NoopTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(string method, string url,
            IReadOnlyDictionary<string, string> headers, string? body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportResponse(503, new Dictionary<string, string>(), "{}"));
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri?.PathAndQuery ?? string.Empty));
            if (_index >= responses.Length) throw new InvalidOperationException("Unexpected Kubernetes API request.");
            return Task.FromResult(responses[_index++](request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}