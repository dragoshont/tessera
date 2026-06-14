using Tessera.Core.Policy;

namespace Tessera.Core.Recipes;

/// <summary>
/// One callable operation a recipe exposes — a generic HTTP request shape the
/// broker performs with the injected credential. Provider-specific endpoints live
/// here (operator config), never hardcoded in the broker, so the broker stays
/// provider-agnostic (ADR 0014).
/// </summary>
/// <param name="Name">Tool name (provider-prefixed by the MCP surface).</param>
/// <param name="Method">HTTP method (<c>GET</c>/<c>POST</c>/…).</param>
/// <param name="Path">Path appended to the recipe's base URL.</param>
/// <param name="Action">The policy action verb this tool maps to (e.g. <c>read:appointments</c>).</param>
/// <param name="StepUp">
/// True for a write/booking/pay tool: the broker returns a step-up decision and the
/// call proceeds only after an explicit human confirmation echoing the request
/// (ADR 0013 / 0014). The agent can never invoke it autonomously.
/// </param>
/// <param name="Description">Human/agent-facing description of what the tool does.</param>
/// <param name="Plane">
/// An explicit action plane (ADR 0019), or <c>null</c> to derive it from
/// <see cref="Action"/>. Set this only for a legacy verb whose namespace doesn't
/// already say the plane (e.g. a <c>pay:</c> tool declaring <c>use</c>); namespaced
/// <c>read:</c>/<c>use:</c>/<c>manage:</c> verbs classify themselves.
/// </param>
public sealed record RecipeTool(
    string Name,
    string Method,
    string Path,
    string Action,
    bool StepUp = false,
    string? Description = null,
    ActionPlane? Plane = null)
{
    /// <summary>True when this is a mutating tool requiring human confirmation.</summary>
    public bool RequiresConfirmation => StepUp;

    /// <summary>
    /// The plane this tool operates on: the explicit <see cref="Plane"/> when set,
    /// otherwise derived from the <see cref="Action"/> verb's namespace.
    /// </summary>
    public ActionPlane EffectivePlane => Plane ?? ActionPlanes.Of(Action);
}
