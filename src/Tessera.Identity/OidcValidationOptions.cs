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

    /// <summary>Allowed clock skew when checking <c>exp</c>/<c>nbf</c>.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>True once an audience is configured and delegation can be enforced.</summary>
    public bool DelegationEnabled => !string.IsNullOrWhiteSpace(Audience);
}
