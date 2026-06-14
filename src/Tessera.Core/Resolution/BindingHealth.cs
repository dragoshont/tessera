namespace Tessera.Core.Resolution;

/// <summary>
/// A binding's stored-bundle health for the admin portal: its
/// <see cref="Status"/> plus the non-secret <em>presence</em> flags. It carries no
/// secret bytes — only <em>that</em> material exists, never <em>what</em> it is —
/// so it is safe to surface in the UI (ADR 0016 / the secretless contract).
/// </summary>
/// <param name="Status">The credential status (present / absent / incomplete / error).</param>
/// <param name="HasAccessToken">Whether an access token is present.</param>
/// <param name="HasRefreshToken">Whether a refresh token is present.</param>
/// <param name="HasCookies">Whether one or more session cookies are present.</param>
/// <param name="Detail">A secret-free explanation (e.g. "has refresh_token, cookies").</param>
public sealed record BindingHealth(
    CredentialStatus Status,
    bool HasAccessToken,
    bool HasRefreshToken,
    bool HasCookies,
    string Detail = "");
