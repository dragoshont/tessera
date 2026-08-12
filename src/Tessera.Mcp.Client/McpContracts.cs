using System.Text.Json;

namespace Tessera.Mcp.Client;

public sealed record McpServerEndpoint(
    string ServerId,
    Uri Endpoint,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool AllowPrivateNetwork = false);

public sealed record McpCallPolicy(
    TimeSpan Timeout,
    int MaximumResultBytes,
    bool MutationDispatched = false)
{
    public static McpCallPolicy ReadOnly { get; } = new(TimeSpan.FromSeconds(30), 512 * 1024);
}

public sealed record McpToolContract(
    string Name,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record McpServerContract(
    string ServerId,
    string? ServerName,
    string? ServerVersion,
    IReadOnlyList<McpToolContract> Tools);

public enum McpInvocationOutcome
{
    Succeeded,
    Failed,
    UnknownOutcome,
}

public sealed record McpInvocationResult(
    McpInvocationOutcome Outcome,
    JsonElement? StructuredOutput,
    string? ErrorCode);

public interface IMcpClientRuntime
{
    Task<McpServerContract> DiscoverAsync(
        McpServerEndpoint endpoint,
        McpCallPolicy policy,
        CancellationToken cancellationToken = default);

    Task<McpInvocationResult> CallAsync(
        McpServerEndpoint endpoint,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        McpCallPolicy policy,
        CancellationToken cancellationToken = default);
}