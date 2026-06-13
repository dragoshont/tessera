using Tessera.Core.Identity;
using Tessera.Core.Model;

namespace Tessera.Core.Resolution;

/// <summary>
/// Maps a <c>(target, principal)</c> to the store secret holding its bundle. A
/// binding with <see cref="Principal"/> = <c>null</c> backs pure-automation access.
/// </summary>
/// <param name="Target">The target this binding backs.</param>
/// <param name="Credential">The store secret name holding the bundle.</param>
/// <param name="Principal">The exact delegated human, or <c>null</c> for automation.</param>
public sealed record TargetBinding(
    string Target,
    string Credential,
    string? Principal = null)
{
    /// <summary>True when this binding backs <paramref name="request"/>.</summary>
    public bool Matches(AccessRequest request)
    {
        if (!string.Equals(Target, request.Target, StringComparison.Ordinal))
        {
            return false;
        }

        return PrincipalMatches(request.OnBehalfOf);
    }

    private bool PrincipalMatches(EndUserAssertion? user)
    {
        if (Principal is null)
        {
            return user is null;
        }

        if (user is null)
        {
            return false;
        }

        // Match the binding's principal against the verified subject (oid) OR the
        // verified preferred_username — both are signed claims.
        return string.Equals(Principal, user.Subject, StringComparison.Ordinal)
            || string.Equals(Principal, user.PreferredUsername, StringComparison.OrdinalIgnoreCase);
    }
}
