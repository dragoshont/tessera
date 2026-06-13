using Tessera.Core.Identity;

namespace Tessera.Identity;

/// <summary>
/// The outcome of validating a forwarded OIDC access token. On success it exposes
/// only the claims Tessera needs; on failure, a secret-free reason.
/// </summary>
public sealed class TesseraTokenResult
{
    private TesseraTokenResult(bool succeeded, string? failureReason, IReadOnlyDictionary<string, string>? claims)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
        _claims = claims ?? Empty;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    private readonly IReadOnlyDictionary<string, string> _claims;

    /// <summary>True when the token validated against the configured trust + audience.</summary>
    public bool Succeeded { get; }

    /// <summary>A secret-free failure reason (never the token).</summary>
    public string? FailureReason { get; }

    /// <summary>Stable end-user id (<c>oid</c>), if this is a user token.</summary>
    public string? Oid => Get("oid");

    /// <summary>Human-readable username (<c>preferred_username</c>), if present.</summary>
    public string? PreferredUsername => Get("preferred_username") ?? Get("upn");

    /// <summary>The calling application id (<c>azp</c>/<c>appid</c>), if present.</summary>
    public string? AppId => Get("azp") ?? Get("appid");

    /// <summary>Tenant id (<c>tid</c>).</summary>
    public string? TenantId => Get("tid");

    /// <summary>Audience (<c>aud</c>).</summary>
    public string? Audience => Get("aud");

    /// <summary>Issuer (<c>iss</c>).</summary>
    public string? Issuer => Get("iss");

    /// <summary>Token id (<c>jti</c>/<c>uti</c>) — for audit and future replay defence.</summary>
    public string? TokenId => Get("jti") ?? Get("uti");

    /// <summary>
    /// True when the token represents an app-only (automation) caller — no human.
    /// Detected via <c>idtyp=app</c>, or the absence of a user identifier.
    /// </summary>
    public bool IsAppOnly =>
        string.Equals(Get("idtyp"), "app", StringComparison.OrdinalIgnoreCase) ||
        (string.IsNullOrEmpty(Oid) && string.IsNullOrEmpty(PreferredUsername) && !string.IsNullOrEmpty(AppId));

    /// <summary>Builds a failed result with a reason.</summary>
    public static TesseraTokenResult Fail(string reason) => new(false, reason, null);

    /// <summary>Builds a successful result from the validated claims.</summary>
    public static TesseraTokenResult Success(IReadOnlyDictionary<string, string> claims) => new(true, null, claims);

    /// <summary>
    /// Converts a successful user token to the verified end-user assertion (FOR WHOM).
    /// Returns <c>null</c> for an app-only token or a failed validation.
    /// </summary>
    public EndUserAssertion? ToEndUserAssertion()
    {
        if (!Succeeded || IsAppOnly)
        {
            return null;
        }

        var subject = Oid ?? PreferredUsername;
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(Issuer))
        {
            return null;
        }

        return new EndUserAssertion(subject, Issuer, VerificationMethod.OidcJwt, PreferredUsername);
    }

    /// <summary>
    /// Converts a successful app-only token to the verified caller identity (WHO).
    /// Returns <c>null</c> for a user token or a failed validation.
    /// </summary>
    public CallerIdentity? ToCallerIdentity()
    {
        if (!Succeeded || !IsAppOnly || string.IsNullOrEmpty(AppId))
        {
            return null;
        }

        return new CallerIdentity(AppId, VerificationMethod.OidcJwt, TenantId);
    }

    private string? Get(string key) => _claims.TryGetValue(key, out var value) ? value : null;
}
