using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Tessera.Mcp;

/// <summary>
/// The MCP tools LibreChat invokes. Each reads the forwarded end-user OIDC token
/// from the request and delegates to <see cref="TesseraMcpService"/>. The tools are
/// deliberately read-only in iteration 1 — they prove per-user delegation and
/// report credential status, but make no upstream call (the injection egress is
/// gated in the broker host; review H2/H3).
/// </summary>
[McpServerToolType]
public sealed class TesseraMcpTools
{
    private readonly TesseraMcpService _service;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TesseraMcpOptions _options;

    /// <summary>Creates the tool set.</summary>
    public TesseraMcpTools(TesseraMcpService service, IHttpContextAccessor httpContextAccessor, TesseraMcpOptions options)
    {
        _service = service;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    /// <summary>Reports who Tessera believes is calling — the verified caller and end-user.</summary>
    [McpServerTool(Name = "tessera_whoami")]
    [Description("Report the verified identity Tessera resolved for this call: the calling workload and, if delegated, the signed-in user. Use this to confirm per-user delegation is working.")]
    public Task<WhoAmIResult> WhoAmIAsync(CancellationToken cancellationToken) =>
        _service.WhoAmIAsync(ForwardedToken(), cancellationToken);

    /// <summary>Lists the targets the current user may ask about.</summary>
    [McpServerTool(Name = "tessera_list_targets")]
    [Description("List the providers/targets configured in Tessera and whether the signed-in user is granted access to each. Read-only; makes no upstream call.")]
    public Task<ListTargetsResult> ListTargetsAsync(CancellationToken cancellationToken) =>
        _service.ListTargetsAsync(ForwardedToken(), cancellationToken);

    /// <summary>Checks whether the current user may perform an action, and whether a credential is ready.</summary>
    [McpServerTool(Name = "tessera_check_access")]
    [Description("Authorize a (target, action) for the signed-in user and report the policy decision plus whether a usable credential is present in the vault. Read-only: it does NOT call the upstream service.")]
    public Task<CheckAccessResult> CheckAccessAsync(
        [Description("The provider/target, e.g. health-portal or marketplace.")] string target,
        [Description("The action verb, e.g. read:results or write:events.create.")] string action,
        CancellationToken cancellationToken) =>
        _service.CheckAccessAsync(ForwardedToken(), target, action, cancellationToken);

    /// <summary>Lists the provider operations the signed-in user may call.</summary>
    [McpServerTool(Name = "tessera_list_provider_tools")]
    [Description("List the provider operations (per target) the signed-in user may call through Tessera — each with its method and whether it is a write that needs confirmation. Read-only.")]
    public Task<ListProviderToolsResult> ListProviderToolsAsync(CancellationToken cancellationToken) =>
        _service.ListProviderToolsAsync(ForwardedToken(), cancellationToken);

    /// <summary>Calls a provider operation on behalf of the signed-in user (Tessera injects their credential).</summary>
    [McpServerTool(Name = "tessera_call")]
    [Description("Call a provider operation as the signed-in user — Tessera injects that user's credential and returns only the result (the caller never sees the secret). For a WRITE/booking operation you MUST first read back the exact details to the user, get a spoken/typed yes, then call again with confirm=true; a write never runs with confirm=false.")]
    public Task<ProviderCallToolResult> CallAsync(
        [Description("The provider/target, e.g. the health portal.")] string target,
        [Description("The operation name, from tessera_list_provider_tools.")] string tool,
        [Description("Optional JSON arguments/body for the operation.")] string? args,
        [Description("Set true ONLY for a write/booking after the user has explicitly confirmed the exact details.")] bool confirm,
        CancellationToken cancellationToken) =>
        _service.CallProviderAsync(ForwardedToken(), target, tool, args, confirm, cancellationToken);

    private string? ForwardedToken()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(_options.AlternateTokenHeader)
            && context.Request.Headers.TryGetValue(_options.AlternateTokenHeader, out var alternate))
        {
            return StripBearer(alternate.ToString());
        }

        return StripBearer(context.Request.Headers.Authorization.ToString());
    }

    private static string? StripBearer(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : value.Trim();
    }
}
