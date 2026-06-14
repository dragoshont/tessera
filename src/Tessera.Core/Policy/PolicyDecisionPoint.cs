using Tessera.Core.Model;

namespace Tessera.Core.Policy;

/// <summary>
/// The Policy Decision Point: fail-closed authorization. Given an
/// <see cref="AccessRequest"/>, returns a <see cref="Decision"/>. Deliberately
/// boring and auditable — this is the security boundary (ADR 0005 / 0008).
/// </summary>
/// <remarks>
/// Invariants: verified callers only (unless <c>allowUnverified</c> for loopback
/// dev); a present end-user must also be verified; default deny; explicit
/// delegation; least-privilege glob actions; step-up for flagged high-impact verbs.
/// </remarks>
public sealed class PolicyDecisionPoint
{
    private readonly IReadOnlyList<Grant> _grants;
    private readonly bool _allowUnverified;
    private readonly bool _manageRequiresStepUp;

    /// <summary>Creates a PDP over a set of grants.</summary>
    /// <param name="grants">The authorization rules (empty = deny everything).</param>
    /// <param name="allowUnverified">
    /// When true, unverified (dev) callers are tolerated — local loopback only.
    /// </param>
    /// <param name="manageRequiresStepUp">
    /// When true (the default), an authorized control-plane (<c>manage:</c>) action
    /// always requires a human step-up, even when the grant didn't list it in
    /// <see cref="Grant.StepUpActions"/> (ADR 0019). Reshaping an integration is
    /// high-impact by default; an operator must deliberately set this false to
    /// loosen the whole manage plane.
    /// </param>
    public PolicyDecisionPoint(
        IEnumerable<Grant>? grants = null,
        bool allowUnverified = false,
        bool manageRequiresStepUp = true)
    {
        _grants = grants?.ToArray() ?? [];
        _allowUnverified = allowUnverified;
        _manageRequiresStepUp = manageRequiresStepUp;
    }

    /// <summary>Evaluates a request, fail-closed.</summary>
    public Decision Evaluate(AccessRequest request)
    {
        // 1) Identity gate — never authorize an unproven caller on the network.
        if (!request.Caller.IsVerified && !_allowUnverified)
        {
            return Decision.Deny(
                $"caller '{request.Caller.Id}' is not verified (via {request.Caller.VerifiedVia})");
        }

        // An end-user, if present, must also be verified (no plaintext delegation).
        if (request.OnBehalfOf is { IsVerified: false } unverified)
        {
            return Decision.Deny(
                $"end-user '{unverified.Subject}' assertion is not verified");
        }

        // 2) Authorization — explicit grant required (default deny).
        foreach (var grant in _grants)
        {
            if (!grant.Matches(request))
            {
                continue;
            }

            var who = request.OnBehalfOf is { } user
                ? $"{request.Caller.Id} on behalf of {user.Subject}"
                : request.Caller.Id;

            if (grant.RequiresStepUp(request))
            {
                return Decision.StepUp(
                    $"step-up required: {who} may {request.Action} on {request.Target} only after human approval",
                    obligation: request.Action);
            }

            // The control plane reshapes an integration — default to step-up unless
            // an operator has deliberately loosened the whole manage plane (ADR 0019)
            // or exempted this specific action on this grant (the fine-grained hatch).
            if (_manageRequiresStepUp
                && ActionPlanes.Of(request.Action) == ActionPlane.Manage
                && !grant.ExemptsManageStepUp(request.Action))
            {
                return Decision.StepUp(
                    $"step-up required: {who} may manage {request.Target} ({request.Action}) only after human approval",
                    obligation: request.Action);
            }

            return Decision.Allow($"granted: {who} may {request.Action} on {request.Target}");
        }

        return Decision.Deny(
            $"no grant allows {request.Caller.Id} to {request.Action} on {request.Target}");
    }
}
