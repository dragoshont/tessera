using Tessera.Core.Identity;

namespace Tessera.Mcp;

/// <summary>
/// The seam the MCP surface uses to perform a provider call, implemented by the
/// broker host (which owns the egress + transport). Keeps Tessera.Mcp free of the
/// web/egress dependencies and avoids a project cycle.
/// </summary>
public interface IProviderGateway
{
    /// <summary>Lists the provider tools the given identity may call (dry — no upstream call).</summary>
    IReadOnlyList<ProviderToolInfo> ListTools(CallerIdentity caller, EndUserAssertion? onBehalfOf);

    /// <summary>
    /// Resolves the tool <em>name</em> for an exact <c>(method, path)</c> on a target
    /// — the structural address a domain MCP uses to invoke a tool by the HTTP shape
    /// it already knows (ADR 0015), without duplicating the recipe's name map. Only
    /// matches a tool whose path is a fixed template (no <c>{placeholder}</c>): a
    /// parameterized tool must be invoked by name. Returns <c>null</c> when nothing
    /// matches (or egress is disabled). This is structural only — authorization still
    /// happens in <see cref="CallAsync"/>.
    /// </summary>
    string? ResolveToolByHttp(string target, string method, string path);

    /// <summary>
    /// Performs a provider call for the identity. <paramref name="confirmed"/> must
    /// be true to run a write/booking tool.
    /// </summary>
    Task<ProviderCallToolResult> CallAsync(
        CallerIdentity caller,
        EndUserAssertion? onBehalfOf,
        string target,
        string tool,
        string? argsJson,
        bool confirmed,
        CancellationToken cancellationToken = default);
}

/// <summary>A gateway that does nothing (egress disabled) — every call is refused.</summary>
public sealed class DisabledProviderGateway : IProviderGateway
{
    /// <summary>The shared disabled instance.</summary>
    public static readonly DisabledProviderGateway Instance = new();

    /// <inheritdoc/>
    public IReadOnlyList<ProviderToolInfo> ListTools(CallerIdentity caller, EndUserAssertion? onBehalfOf) => [];

    /// <inheritdoc/>
    public string? ResolveToolByHttp(string target, string method, string path) => null;

    /// <inheritdoc/>
    public Task<ProviderCallToolResult> CallAsync(
        CallerIdentity caller, EndUserAssertion? onBehalfOf, string target, string tool, string? argsJson, bool confirmed, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderCallToolResult("notallowed", null, null, "provider egress is disabled"));
}
