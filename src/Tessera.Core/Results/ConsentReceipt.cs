using Tessera.Core.Resolution;

namespace Tessera.Core.Results;

/// <summary>
/// A secret-free record that a person consented to Tessera holding/using a
/// credential for a specific data class (ADR 0020 cross-cutting, service-access
/// spec §"separate consent receipts per data class"). It is a <em>declaration of
/// scope</em>, not a credential: a person consenting to calendar must not silently
/// grant mail, so consent is per <c>(principal, target, data class)</c>. It never
/// carries a secret value — only who consented, to what, when, and under which
/// ownership mode.
/// </summary>
/// <param name="Principal">The verified person who consented.</param>
/// <param name="Target">The provider/target the consent is scoped to.</param>
/// <param name="DataClass">The data class consented to (e.g. <c>calendar</c>, <c>mail.metadata</c>, <c>mail.body</c>) — keeps calendar consent from leaking into mail.</param>
/// <param name="Owner">The ownership mode the credential is held under (ADR 0020).</param>
/// <param name="GrantedAt">When consent was given.</param>
/// <param name="Guardian">For <see cref="CredentialOwner.Dependent"/>: the guardian who consented on the dependent's behalf; otherwise null.</param>
/// <param name="Scopes">The provider scopes this consent covers (e.g. delegated OAuth scopes), for the audit trail; never a secret.</param>
public sealed record ConsentReceipt(
    string Principal,
    string Target,
    string DataClass,
    CredentialOwner Owner,
    DateTimeOffset GrantedAt,
    string? Guardian = null,
    IReadOnlyList<string>? Scopes = null)
{
    /// <summary>The scopes this consent covers (never null).</summary>
    public IReadOnlyList<string> CoveredScopes => Scopes ?? [];

    /// <summary>
    /// True when this receipt covers <paramref name="dataClass"/> on
    /// <paramref name="target"/> for <paramref name="principal"/> — an exact,
    /// case-insensitive match on all three. Consent for one data class never
    /// satisfies a different one (calendar ≠ mail), the whole point of the receipt.
    /// </summary>
    public bool Covers(string principal, string target, string dataClass) =>
        string.Equals(Principal, principal, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Target, target, StringComparison.Ordinal)
        && string.Equals(DataClass, dataClass, StringComparison.Ordinal);
}
