using Tessera.Core.Policy;
using Tessera.Core.Results;

namespace Tessera.Core.Recipes;

/// <summary>
/// One callable operation a recipe exposes — a generic HTTP request shape the
/// broker performs with the injected credential. Provider-specific endpoints live
/// here (operator config), never hardcoded in the broker, so the broker stays
/// provider-agnostic (ADR 0014).
/// </summary>
/// <param name="Name">Tool name (provider-prefixed by the MCP surface).</param>
/// <param name="Method">HTTP method (<c>GET</c>/<c>POST</c>/…).</param>
/// <param name="Path">Path appended to the recipe's base URL. May contain <c>{placeholder}</c> segments filled from the call args; a <c>{handle}</c> segment is filled from a target-scoped <see cref="Results.ResultHandle"/> (full body by handle only).</param>
/// <param name="Action">The policy action verb this tool maps to (e.g. <c>read:appointments</c>).</param>
/// <param name="StepUp">
/// True for a write/booking/pay tool: the broker returns a step-up decision and the
/// call proceeds only after an explicit human confirmation echoing the request
/// (ADR 0013 / 0014). The agent can never invoke it autonomously.
/// </param>
/// <param name="Description">Human/agent-facing description of what the tool does.</param>
/// <param name="OutputClass">
/// The output class this tool returns (service-access spec): <c>metadata</c> (search/
/// list — ids + handles, capped, no body), <c>preview</c>, <c>fullBody</c> /
/// <c>attachment</c> (full content — <b>must</b> read by a <c>{handle}</c>/placeholder
/// so it can't bulk-spill), or <c>receipt</c>. <c>null</c> ⇒ unclassified (no
/// result-class enforcement, the legacy behaviour).
/// </param>
/// <param name="Query">
/// The allow-list of query-string parameter names this tool may forward from the
/// call args (e.g. <c>pageSize</c>, <c>sortKey</c>, <c>start</c>). Only names listed
/// here are appended to the upstream URL, and only when present in the args — an
/// agent can't inject an arbitrary query parameter. Values are URL-encoded. <c>null</c>
/// ⇒ no query forwarding (the path is used verbatim). Path <c>{placeholders}</c> and
/// query params are independent: a name can be one or the other.
/// </param>
public sealed record RecipeTool(
    string Name,
    string Method,
    string Path,
    string Action,
    bool StepUp = false,
    string? Description = null,
    ResultClass? OutputClass = null,
    IReadOnlyList<string>? Query = null)
{
    /// <summary>True when this is a mutating tool requiring human confirmation.</summary>
    public bool RequiresConfirmation => StepUp;

    /// <summary>The query-parameter names this tool may forward (never null).</summary>
    public IReadOnlyList<string> AllowedQuery => Query ?? [];

    /// <summary>
    /// The plane this tool operates on (ADR 0019), always derived from the
    /// <see cref="Action"/> verb's namespace — the same value the PDP enforces, so
    /// the surfaced plane can never diverge from what is actually authorized.
    /// </summary>
    public ActionPlane EffectivePlane => ActionPlanes.Of(Action);

    /// <summary>
    /// True when this tool returns full content (<c>fullBody</c>/<c>attachment</c>)
    /// and must therefore be called by a handle/placeholder — never as a bare,
    /// bulk-readable path (service-access spec §"Output classes").
    /// </summary>
    public bool RequiresHandle => OutputClass is ResultClass.FullBody or ResultClass.Attachment;
}
