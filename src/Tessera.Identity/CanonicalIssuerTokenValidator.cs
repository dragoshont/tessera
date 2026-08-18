namespace Tessera.Identity;

/// <summary>
/// Validates through one explicit trust lane, then maps its signed subject onto
/// Tessera's primary issuer namespace so the same Authentik user owns one durable
/// product state across application-specific issuers.
/// </summary>
public sealed class CanonicalIssuerTokenValidator(
    ITokenValidator validator,
    string canonicalIssuer,
    string canonicalSubjectClaim) : ITokenValidator
{
    private readonly string _canonicalIssuer = !string.IsNullOrWhiteSpace(canonicalIssuer)
        ? canonicalIssuer
        : throw new ArgumentException("Canonical issuer is required.", nameof(canonicalIssuer));
    private readonly string _canonicalSubjectClaim = !string.IsNullOrWhiteSpace(canonicalSubjectClaim)
        ? canonicalSubjectClaim
        : throw new ArgumentException("Canonical subject claim is required.", nameof(canonicalSubjectClaim));

    public bool DelegationEnabled => validator.DelegationEnabled;

    public async Task<TesseraTokenResult> ValidateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var result = await validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        return result.WithCanonicalIdentity(_canonicalIssuer, _canonicalSubjectClaim);
    }
}