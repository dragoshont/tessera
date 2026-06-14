namespace Tessera.Mcp;

/// <summary>A provider tool the current identity may call (from a recipe).</summary>
/// <param name="Target">The provider/target.</param>
/// <param name="Tool">The tool name.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Write">True if this is a write/booking tool (needs confirmation).</param>
/// <param name="Description">What the tool does.</param>
/// <param name="Plane">The action plane (ADR 0019): <c>read</c> (observe) / <c>use</c> (operate) / <c>manage</c> (reshape), or null for an unclassified legacy verb.</param>
/// <param name="OutputClass">The output class the tool returns (<c>metadata</c>/<c>preview</c>/<c>fullBody</c>/<c>attachment</c>/<c>receipt</c>), or null when unclassified.</param>
/// <param name="RequiresHandle">True when the tool returns full content and must be called with a <c>handle</c> arg from a prior search (no bulk read).</param>
public sealed record ProviderToolInfo(
    string Target,
    string Tool,
    string Method,
    bool Write,
    string? Description,
    string? Plane = null,
    string? OutputClass = null,
    bool RequiresHandle = false);

/// <summary>The provider tools visible to the current identity.</summary>
/// <param name="Authenticated">Whether a verified identity was established.</param>
/// <param name="Tools">The callable provider tools.</param>
/// <param name="Detail">A secret-free explanation.</param>
public sealed record ListProviderToolsResult(bool Authenticated, IReadOnlyList<ProviderToolInfo> Tools, string Detail);

/// <summary>The result of a provider call via the MCP surface.</summary>
/// <param name="Status">Outcome: <c>completed</c> / <c>denied</c> / <c>stepup</c> / <c>nocredential</c> / <c>notallowed</c> / <c>badrequest</c> / <c>error</c>.</param>
/// <param name="HttpStatus">Upstream HTTP status, when a call was made.</param>
/// <param name="Body">Upstream response body, when a call was made.</param>
/// <param name="Detail">A secret-free explanation (e.g. the confirmation prompt for a write).</param>
/// <param name="OutputClass">The output class of the body (<c>metadata</c>/<c>fullBody</c>/…): the retention rule + a reminder the body is untrusted provider content.</param>
public sealed record ProviderCallToolResult(string Status, int? HttpStatus, string? Body, string Detail, string? OutputClass = null);
