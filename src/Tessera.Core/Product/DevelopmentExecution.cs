using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tessera.Core.Product;

public sealed record DevelopmentCommandProfile(
    string Id,
    string Effect,
    string Executable,
    IReadOnlyList<string> ArgumentPrefix,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutSeconds,
    int OutputLimitBytes);

public static class DevelopmentCommandProfiles
{
    private static readonly DevelopmentCommandProfile RepositoryStatus = new(
        "repository.status",
        "READ_ONLY",
        "/usr/bin/git",
        ["status", "--short", "--branch"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "safe.directory",
            ["GIT_CONFIG_VALUE_0"] = "/workspace",
            ["HOME"] = "/workspace",
        },
        300,
        32 * 1024);

    public static bool TryResolve(
        string? profileId,
        IReadOnlyList<string>? arguments,
        out DevelopmentCommandProfile? profile)
    {
        profile = null;
        if (!string.Equals(profileId, RepositoryStatus.Id, StringComparison.Ordinal)
            || arguments is null
            || arguments.Count != 0
            || arguments.Count > 8
            || arguments.Any(value => Encoding.UTF8.GetByteCount(value) > 256))
            return false;
        profile = RepositoryStatus;
        return true;
    }

    public static string CanonicalRequestHash(
        string name,
        string workspaceId,
        string profileId,
        IReadOnlyList<string> arguments)
    {
        var canonical = JsonSerializer.Serialize(new { name, workspaceId, commandProfile = profileId, arguments });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record DevelopmentTaskCreation(ProductJob Job, ProductJobRun Run, bool Replayed);

public sealed record DevelopmentTaskCreateResult(
    DevelopmentTaskCreation? Creation,
    string? ErrorCode,
    string? ResponseBodyJson,
    bool Replayed)
{
    public static DevelopmentTaskCreateResult Failed(string errorCode) => new(null, errorCode, null, false);
    public static DevelopmentTaskCreateResult Created(ProductJob job, ProductJobRun run, string responseBodyJson) =>
        new(new(job, run, false), null, responseBodyJson, false);
    public static DevelopmentTaskCreateResult Replay(string responseBodyJson) =>
        new(null, null, responseBodyJson, true);
}

public static class DevelopmentTaskResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(ProductJob job, ProductJobRun run) => JsonSerializer.Serialize(new
    {
        job = new
        {
            id = job.JobId,
            jobId = job.JobId,
            job.Name,
            job.Instruction,
            job.DesiredState,
            job.Health,
            job.ModelProfileId,
            job.Schedule,
            job.NextOccurrence,
            accountGrants = job.AccountGrants,
            capabilityGrants = job.CapabilityGrants.Select(value => $"{value.Id}@{value.Version}").ToArray(),
            sideEffectGrants = job.SideEffectGrants,
            contextPolicy = JsonDocument.Parse(job.ContextPolicyJson).RootElement.Clone(),
            job.Kind,
            job.ConversationId,
            developmentSpec = job.DevelopmentSpec is null ? null : new
            {
                job.DevelopmentSpec.WorkspaceId,
                job.DevelopmentSpec.CommandProfile,
                job.DevelopmentSpec.Arguments,
                job.DevelopmentSpec.Effect,
                job.DevelopmentSpec.TimeoutSeconds,
                job.DevelopmentSpec.OutputLimitBytes,
            },
            lastRun = (object?)null,
            job.Version,
        },
        run = new
        {
            id = run.RunId,
            runId = run.RunId,
            run.JobId,
            run.ScheduledFor,
            run.State,
            run.StartedAt,
            run.EndedAt,
            run.ModelProfileId,
            run.ContextSnapshotRef,
            capabilityCallIds = Array.Empty<string>(),
            accountIds = Array.Empty<string>(),
            actionIds = Array.Empty<string>(),
            outputRefs = Array.Empty<string>(),
            evidenceRefs = Array.Empty<string>(),
            run.ErrorCode,
            run.Version,
        },
    }, JsonOptions);
}

public sealed record DevelopmentExecutionRequest(
    string OwnerPrincipalId,
    string ConversationId,
    string JobId,
    string RunId,
    string WorkspaceId,
    string SnapshotRef,
    DevelopmentCommandProfile Profile,
    IReadOnlyList<string> Arguments);

public sealed record DevelopmentExecutionResult(
    string Outcome,
    byte[] Log,
    string? ErrorCode = null);

public interface IDevelopmentExecutor
{
    Task<DevelopmentExecutionResult> ExecuteAsync(
        DevelopmentExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record NormalizedDevelopmentOutput(string Text, bool Truncated);

public static partial class DevelopmentOutputNormalizer
{
    private static readonly UTF8Encoding LenientUtf8 = new(false, false);

    public static NormalizedDevelopmentOutput Normalize(ReadOnlySpan<byte> log, int limitBytes)
    {
        if (limitBytes is < 1 or > 32 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        return NormalizeOne(log, limitBytes);
    }

    private static NormalizedDevelopmentOutput NormalizeOne(ReadOnlySpan<byte> value, int limitBytes)
    {
        var decoded = LenientUtf8.GetString(value);
        var clean = new string(decoded.Where(character => character is '\n' or '\r' or '\t' || !char.IsControl(character)).ToArray());
        clean = SecretPattern().Replace(clean, match => $"{match.Groups[1].Value}[REDACTED]");
        var encoded = Encoding.UTF8.GetBytes(clean);
        var truncated = encoded.Length > limitBytes;
        var length = Math.Min(encoded.Length, limitBytes);
        while (length > 0 && length < encoded.Length && (encoded[length] & 0xC0) == 0x80) length--;
        var text = Encoding.UTF8.GetString(encoded, 0, length);
        return new(text, truncated);
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?|(?:api[_-]?key|token|password|secret)\\s*[:=]\\s*)[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}