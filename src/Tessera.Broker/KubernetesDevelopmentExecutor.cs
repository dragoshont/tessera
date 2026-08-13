using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Tessera.Core.Product;

namespace Tessera.Broker;

public sealed record DevelopmentSnapshotOptions(string SnapshotRef, string PvcClaimName, string PvcSubPath);

public sealed record DevelopmentExecutorOptions(
    string ApiServer,
    string Namespace,
    string ImageDigest,
    int RunAsUser,
    int RunAsGroup,
    string CpuLimit,
    string MemoryLimit,
    string EphemeralStorageLimit,
    IReadOnlyList<DevelopmentSnapshotOptions> Snapshots,
    string ServiceAccountTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token",
    string ServiceAccountCaPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt")
{
    public bool IsComplete => Uri.TryCreate(ApiServer, UriKind.Absolute, out var apiServer)
        && apiServer.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(Namespace)
        && Namespace.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.')
        && ImageDigest.Contains("@sha256:", StringComparison.Ordinal)
        && RunAsUser > 0
        && RunAsGroup > 0
        && !string.IsNullOrWhiteSpace(CpuLimit)
        && !string.IsNullOrWhiteSpace(MemoryLimit)
        && !string.IsNullOrWhiteSpace(EphemeralStorageLimit)
        && Path.IsPathRooted(ServiceAccountTokenPath)
        && Path.IsPathRooted(ServiceAccountCaPath)
        && Snapshots.Count > 0
        && Snapshots.All(snapshot =>
            !string.IsNullOrWhiteSpace(snapshot.SnapshotRef)
            && !string.IsNullOrWhiteSpace(snapshot.PvcClaimName)
            && !string.IsNullOrWhiteSpace(snapshot.PvcSubPath)
            && !Path.IsPathRooted(snapshot.PvcSubPath)
            && !snapshot.PvcSubPath.Split('/').Contains("..", StringComparer.Ordinal));
}

internal sealed class KubernetesDevelopmentExecutor(
    HttpClient client,
    DevelopmentExecutorOptions options,
    Func<CancellationToken, Task<string>> tokenReader) : IDevelopmentExecutor
{
    private static readonly string[] CopyCommand = ["/bin/cp"];
    private static readonly string[] CopyArguments = ["-a", "/snapshot/.", "/workspace/"];
    private static readonly string[] AllCapabilities = ["ALL"];

    internal static HttpClient CreateHttpClient(DevelopmentExecutorOptions options)
    {
        var root = X509Certificate2.CreateFromPemFile(options.ServiceAccountCaPath);
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null
                || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                return false;
            using var server = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(server);
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(options.ApiServer.TrimEnd('/') + "/"),
        };
    }

    public async Task<DevelopmentExecutionResult> ExecuteAsync(
        DevelopmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsComplete) return Unavailable();
        var snapshot = options.Snapshots.SingleOrDefault(item => item.SnapshotRef == request.SnapshotRef);
        if (snapshot is null) return Unavailable();
        using var job = BuildJob(request, snapshot);
        var name = JobName(request.RunId);
        var jobsPath = $"apis/batch/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/jobs";
        await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage? create = null;
        try
        {
            create = await client.PostAsJsonAsync(jobsPath, job.RootElement, cancellationToken).ConfigureAwait(false);
            if (create.StatusCode == HttpStatusCode.Conflict)
            {
                using var existing = await client.GetAsync($"{jobsPath}/{name}", cancellationToken).ConfigureAwait(false);
                if (!existing.IsSuccessStatusCode) return Unknown();
            }
            else if (!create.IsSuccessStatusCode)
            {
                return new("FAILED", [], "development_executor_failed");
            }
        }
        catch (HttpRequestException)
        {
            try
            {
                using var existing = await client.GetAsync($"{jobsPath}/{name}", cancellationToken).ConfigureAwait(false);
                if (!existing.IsSuccessStatusCode) return Unknown();
            }
            catch (HttpRequestException)
            {
                return Unknown();
            }
        }
        finally
        {
            create?.Dispose();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Profile.TimeoutSeconds));
        try
        {
            while (true)
            {
                using var statusResponse = await client.GetAsync($"{jobsPath}/{name}", timeout.Token).ConfigureAwait(false);
                if (!statusResponse.IsSuccessStatusCode) return Unknown();
                using var status = await JsonDocument.ParseAsync(
                    await statusResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                var state = status.RootElement.TryGetProperty("status", out var statusValue) ? statusValue : default;
                if (state.ValueKind == JsonValueKind.Object
                    && state.TryGetProperty("succeeded", out var succeeded)
                    && succeeded.GetInt32() > 0)
                    return await ReadLogsAsync(request, succeeded: true, timeout.Token).ConfigureAwait(false);
                if (state.ValueKind == JsonValueKind.Object
                    && state.TryGetProperty("failed", out var failed)
                    && failed.GetInt32() > 0)
                    return await ReadLogsAsync(request, succeeded: false, timeout.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("FAILED", [], "development_executor_timeout");
        }
    }

    internal JsonDocument BuildJob(DevelopmentExecutionRequest request, DevelopmentSnapshotOptions snapshot)
    {
        var name = JobName(request.RunId);
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "tessera-development-executor",
            ["tessera.dev/run-id"] = SafeLabel(request.RunId),
            ["tessera.dev/network"] = "deny-all",
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            apiVersion = "batch/v1",
            kind = "Job",
            metadata = new { name, @namespace = options.Namespace, labels },
            spec = new
            {
                backoffLimit = 0,
                activeDeadlineSeconds = request.Profile.TimeoutSeconds,
                ttlSecondsAfterFinished = 300,
                template = new
                {
                    metadata = new { labels },
                    spec = new
                    {
                        automountServiceAccountToken = false,
                        restartPolicy = "Never",
                        securityContext = new
                        {
                            runAsNonRoot = true,
                            runAsUser = options.RunAsUser,
                            runAsGroup = options.RunAsGroup,
                            fsGroup = options.RunAsGroup,
                            seccompProfile = new { type = "RuntimeDefault" },
                        },
                        initContainers = new[]
                        {
                            new
                            {
                                name = "copy-snapshot",
                                image = options.ImageDigest,
                                command = CopyCommand,
                                args = CopyArguments,
                                securityContext = ContainerSecurityContext(),
                                resources = Resources(),
                                volumeMounts = new object[]
                                {
                                    new { name = "snapshot", mountPath = "/snapshot", readOnly = true, subPath = snapshot.PvcSubPath },
                                    new { name = "workspace", mountPath = "/workspace" },
                                },
                            },
                        },
                        containers = new[]
                        {
                            new
                            {
                                name = "command",
                                image = options.ImageDigest,
                                workingDir = "/workspace",
                                command = new[] { request.Profile.Executable },
                                args = request.Profile.ArgumentPrefix.Concat(request.Arguments).ToArray(),
                                env = request.Profile.Environment.Select(item => new { name = item.Key, value = item.Value }).ToArray(),
                                securityContext = ContainerSecurityContext(),
                                resources = Resources(),
                                volumeMounts = new object[] { new { name = "workspace", mountPath = "/workspace" } },
                            },
                        },
                        volumes = new object[]
                        {
                            new { name = "snapshot", persistentVolumeClaim = new { claimName = snapshot.PvcClaimName, readOnly = true } },
                            new { name = "workspace", emptyDir = new { sizeLimit = options.EphemeralStorageLimit } },
                        },
                    },
                },
            },
        }));
    }

    private async Task<DevelopmentExecutionResult> ReadLogsAsync(
        DevelopmentExecutionRequest request,
        bool succeeded,
        CancellationToken token)
    {
        var podsPath = $"api/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/pods?labelSelector="
            + Uri.EscapeDataString($"tessera.dev/run-id={SafeLabel(request.RunId)}");
        using var podsResponse = await client.GetAsync(podsPath, token).ConfigureAwait(false);
        if (!podsResponse.IsSuccessStatusCode) return Unknown();
        using var pods = await JsonDocument.ParseAsync(
            await podsResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
        if (!pods.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0)
            return new("FAILED", [], "development_executor_output_unavailable");
        var podName = items[0].GetProperty("metadata").GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(podName))
            return new("FAILED", [], "development_executor_output_unavailable");
        var logsPath = $"api/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/pods/{Uri.EscapeDataString(podName)}/log?container=command&limitBytes={request.Profile.OutputLimitBytes}";
        using var logResponse = await client.GetAsync(logsPath, token).ConfigureAwait(false);
        if (!logResponse.IsSuccessStatusCode)
            return new("FAILED", [], "development_executor_output_unavailable");
        var log = await logResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        return new(succeeded ? "SUCCEEDED" : "FAILED", log,
            succeeded ? null : "development_command_failed");
    }

    private async Task AuthorizeAsync(CancellationToken token)
    {
        var credential = (await tokenReader(token).ConfigureAwait(false)).Trim();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    }

    private static object ContainerSecurityContext() => new
    {
        allowPrivilegeEscalation = false,
        privileged = false,
        readOnlyRootFilesystem = true,
        capabilities = new { drop = AllCapabilities },
    };

    private object Resources() => new
    {
        requests = new Dictionary<string, string>
        {
            ["cpu"] = options.CpuLimit,
            ["memory"] = options.MemoryLimit,
            ["ephemeral-storage"] = options.EphemeralStorageLimit,
        },
        limits = new Dictionary<string, string>
        {
            ["cpu"] = options.CpuLimit,
            ["memory"] = options.MemoryLimit,
            ["ephemeral-storage"] = options.EphemeralStorageLimit,
        },
    };

    private static string JobName(string runId) => $"tessera-dev-{SafeLabel(runId)[..Math.Min(40, SafeLabel(runId).Length)]}";
    private static string SafeLabel(string value) => new(value.ToLowerInvariant().Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.').ToArray());
    private static DevelopmentExecutionResult Unknown() => new("UNKNOWN", [], "development_executor_outcome_unknown");
    private static DevelopmentExecutionResult Unavailable() => new("FAILED", [], "development_executor_unavailable");
}