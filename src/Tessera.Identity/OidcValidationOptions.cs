namespace Tessera.Identity;

/// <summary>
/// What Tessera validates a forwarded Entra OIDC <em>access</em> token against
/// (ADR 0011). An empty <see cref="Audience"/> means delegation is fail-closed.
/// </summary>
public sealed class OidcValidationOptions
{
    /// <summary>Expected issuer, e.g. <c>https://login.microsoftonline.com/&lt;tid&gt;/v2.0</c>.</summary>
    public required string Issuer { get; init; }

    /// <summary>
    /// Expected <c>aud</c> — the shared system app (Flow B). Empty ⇒ fail-closed:
    /// no token is accepted until a real forwarded token's audience is confirmed
    /// (gate G2/C3).
    /// </summary>
    public string Audience { get; init; } = "";

    /// <summary>Expected tenant id (<c>tid</c> claim). Empty disables the tid check.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>
    /// For a multi-tenant authority (<c>/common</c>, <c>/organizations</c>,
    /// <c>/consumers</c>), the tenant IDs allowed to sign in. Empty = accept any
    /// tenant whose issuer matches the Entra template
    /// (<c>https://login.microsoftonline.com/&lt;tid&gt;/v2.0</c>). Ignored for a
    /// single-tenant issuer. Personal Microsoft accounts come from the consumer
    /// tenant <c>9188040d-6c67-4c5b-b112-36a304b66dad</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedTenants { get; init; } = [];

    /// <summary>Allowed clock skew when checking <c>exp</c>/<c>nbf</c>.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>True once an audience is configured and delegation can be enforced.</summary>
    public bool DelegationEnabled => !string.IsNullOrWhiteSpace(Audience);

    /// <summary>
    /// True when the issuer is a multi-tenant authority (<c>/common</c>,
    /// <c>/organizations</c>, or <c>/consumers</c>) — the token's real <c>iss</c>
    /// is then the per-tenant URL, not this authority, so issuer validation must be
    /// template-based.
    /// </summary>
    public bool IsMultiTenantAuthority
    {
        get
        {
            var i = Issuer.TrimEnd('/');
            return i.EndsWith("/common/v2.0", StringComparison.OrdinalIgnoreCase)
                || i.EndsWith("/organizations/v2.0", StringComparison.OrdinalIgnoreCase)
                || i.EndsWith("/consumers/v2.0", StringComparison.OrdinalIgnoreCase)
                || i.EndsWith("/common", StringComparison.OrdinalIgnoreCase)
                || i.EndsWith("/organizations", StringComparison.OrdinalIgnoreCase)
                || i.EndsWith("/consumers", StringComparison.OrdinalIgnoreCase);
        }
    }
}
