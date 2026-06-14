namespace Tessera.Core.Resolution;

/// <summary>
/// Who a stored credential belongs to (ADR 0020) — an axis orthogonal to the
/// acting identity, the action plane, and step-up risk. It answers "whose secret is
/// this?", which shapes consent, isolation, and the (off-by-default) reveal path.
/// </summary>
/// <remarks>
/// The invariants hold the same across all three: the raw secret never reaches an
/// agent, every use is policy-gated and audited, and one person's credential is
/// never visible to another. Ownership only changes <em>who, if anyone,</em> may
/// ever be told the secret.
/// </remarks>
public enum CredentialOwner
{
    /// <summary>
    /// Brokered authority nobody personally holds — a household/service key (media
    /// stack API keys, a Home Assistant token). The default (fail-safe): never reveal
    /// to anyone, including the acting user.
    /// </summary>
    Service = 0,

    /// <summary>
    /// The person's own login that Tessera holds for them (their medical portal,
    /// Gmail, Apple). The owner knows it; "never reveal" protects it from agents and
    /// from other users — not from the owner.
    /// </summary>
    User,

    /// <summary>
    /// A dependent's credential a guardian seeded (a child's account). The guardian
    /// owns the seeding; the dependent owns the data. Named via
    /// <see cref="TargetBinding.Guardian"/>.
    /// </summary>
    Dependent,
}

/// <summary>Parsing and wire-token helpers for <see cref="CredentialOwner"/>.</summary>
public static class CredentialOwners
{
    /// <summary>
    /// Parses a wire token to an owner; an empty/unknown value is
    /// <see cref="CredentialOwner.Service"/> — the fail-safe (never-reveal) default.
    /// </summary>
    public static CredentialOwner Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "user" => CredentialOwner.User,
        "dependent" => CredentialOwner.Dependent,
        _ => CredentialOwner.Service,
    };

    /// <summary>The lowercase wire token for an owner.</summary>
    public static string ToToken(CredentialOwner owner) => owner switch
    {
        CredentialOwner.User => "user",
        CredentialOwner.Dependent => "dependent",
        _ => "service",
    };
}
