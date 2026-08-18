namespace Tessera.Identity;

/// <summary>
/// Validates through one explicit trust lane, then maps its signed subject onto
/// Tessera's primary issuer namespace so the same Authentik user owns one durable
/// product state across application-specific issuers.
/// </summary>
public sealed class CanonicalIssuerTokenValidator(
    ITokenValidator validator,
    string canonicalIssuer) : ITokenValidator
{
    private readonly string _canonicalIssuer = !string.IsNullOrWhiteSpace(canonicalIssuer)
        ? canonicalIssuer
        : throw new ArgumentException("Canonical issuer is required.", nameof(canonicalIssuer));

    public bool DelegationEnabled => validator.DelegationEnabled;

    public async Task<TesseraTokenResult> ValidateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var result = await validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        return result.WithCanonicalIssuer(_canonicalIssuer);
    }
}