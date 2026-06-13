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
public sealed record Grant(
    string Caller,
    string Target,
    IReadOnlyList<string> Actions,
    string? OnBehalfOf = null,
    IReadOnlyList<string>? StepUpActions = null)
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

        // Delegation alignment: grant's on_behalf_of must equal the request's
        // end-user subject (both null for automation).
        var requestUser = request.OnBehalfOf?.Subject;
        if (!string.Equals(OnBehalfOf, requestUser, StringComparison.Ordinal))
        {
            return false;
        }

        return Glob.AnyMatch(Actions, request.Action);
    }

    /// <summary>True when the request's action requires step-up under this grant.</summary>
    public bool RequiresStepUp(AccessRequest request) =>
        StepUpActions is { Count: > 0 } && Glob.AnyMatch(StepUpActions, request.Action);
}
