using Tessera.Core.Identity;
using Tessera.Core.Model;

namespace Tessera.Core.Policy;

/// <summary>
/// One authorization rule: a <see cref="Caller"/>, optionally acting on behalf of
/// <see cref="OnBehalfOf"/>, may perform <see cref="Actions"/> on <see cref="Target"/>.
/// </summary>
/// <remarks>
/// Delegation must line up exactly: a grant naming <see cref="OnBehalfOf"/> matches
/// only a request carrying that exact end-user; a grant without it matches only
/// pure automation (no end-user). A human identity is never silently dropped or
/// added. Actions in <see cref="StepUpActions"/> are allowed only with a human
/// step-up confirmation (threat-model D — write/pay/book).
/// </remarks>
/// <param name="Caller">The caller id this grant applies to.</param>
/// <param name="Target">The target this grant applies to.</param>
/// <param name="Actions">Allowed action globs (e.g. <c>read:*</c>).</param>
/// <param name="OnBehalfOf">The exact delegated human, or <c>null</c> for automation.</param>
/// <param name="StepUpActions">Action globs that require human step-up before allowing.</param>
/// <param name="ManageStepUpExempt">
/// Control-plane (<c>manage:</c>) action globs this <em>specific</em> grant exempts
/// from the manage-plane default step-up (ADR 0019) — the fine-grained escape hatch
/// so a low-risk manage action (e.g. <c>manage:label.rename</c>) can be allowed
/// outright on one grant without loosening the whole plane (the global
/// <c>policy.manageRequiresStepUp</c>). An action also named in
/// <see cref="StepUpActions"/> still steps up (explicit always wins).
/// </param>
public sealed record Grant(
    string Caller,
    string Target,
    IReadOnlyList<string> Actions,
    string? OnBehalfOf = null,
    IReadOnlyList<string>? StepUpActions = null,
    IReadOnlyList<string>? ManageStepUpExempt = null)
{
    /// <summary>True when this grant applies to <paramref name="request"/>.</summary>
    public bool Matches(AccessRequest request)
    {
        if (!string.Equals(Caller, request.Caller.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(Target, request.Target, StringComparison.Ordinal))
        {
            return false;
        }

        // Delegation alignment: grant's on_behalf_of must match the request's
        // end-user (both null for automation). A delegated grant matches the
        // verified subject (oid) OR the verified preferred_username — both are
        // signed claims, so operators can author human-readable grants.
        if (!DelegationMatches(request.OnBehalfOf))
        {
            return false;
        }

        return MatchesAction(request.Action);
    }

    /// <summary>
    /// True when this grant's <see cref="Actions"/> authorize <paramref name="action"/>.
    /// The control plane is default-deny: a <c>manage:</c> action is authorized only
    /// by a grant pattern that is itself manage-scoped (ADR 0019), so a broad
    /// <c>*</c> or a <c>use:*</c> grant never silently reaches <c>manage:</c>. Read
    /// and use verbs keep plain least-privilege glob matching.
    /// </summary>
    private bool MatchesAction(string action)
    {
        if (ActionPlanes.Of(action) == ActionPlane.Manage)
        {
            foreach (var pattern in Actions)
            {
                if (ActionPlanes.IsManageScoped(pattern) && Glob.IsMatch(pattern, action))
                {
                    return true;
                }
            }

            return false;
        }

        return Glob.AnyMatch(Actions, action);
    }

    private bool DelegationMatches(EndUserAssertion? user)
    {
        if (OnBehalfOf is null)
        {
            return user is null;
        }

        if (user is null)
        {
            return false;
        }

        return string.Equals(OnBehalfOf, user.Subject, StringComparison.Ordinal)
            || string.Equals(OnBehalfOf, user.PreferredUsername, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the request's action requires step-up under this grant.</summary>
    public bool RequiresStepUp(AccessRequest request) =>
        StepUpActions is { Count: > 0 } && Glob.AnyMatch(StepUpActions, request.Action);

    /// <summary>
    /// True when this grant exempts <paramref name="action"/> from the manage-plane
    /// default step-up (it appears in <see cref="ManageStepUpExempt"/>) — the
    /// per-grant escape hatch for a low-risk control-plane action.
    /// </summary>
    public bool ExemptsManageStepUp(string action) =>
        ManageStepUpExempt is { Count: > 0 } && Glob.AnyMatch(ManageStepUpExempt, action);
}
