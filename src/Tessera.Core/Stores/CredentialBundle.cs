namespace Tessera.Core.Stores;

/// <summary>
/// A credential bundle as written by the harvester and read by Tessera: an access
/// token, a refresh token, cookies, and any provider-specific extra material.
/// </summary>
/// <remarks>
/// Tessera only ever <em>reads</em> a bundle, never logs it, and never returns its
/// bytes across the broker boundary — callers learn only a
/// <see cref="Resolution.CredentialStatus"/>. This is the secretless contract:
/// "applications cannot leak what they don't have."
/// </remarks>
/// <param name="AccessToken">The OAuth/session access token, if any.</param>
/// <param name="RefreshToken">The refresh token, if any.</param>
/// <param name="Cookies">Session cookies, if any.</param>
/// <param name="Extra">Provider-specific extra fields, if any.</param>
public sealed record CredentialBundle(
    string? AccessToken = null,
    string? RefreshToken = null,
    IReadOnlyDictionary<string, string>? Cookies = null,
    IReadOnlyDictionary<string, string>? Extra = null)
{
    /// <summary>An absent bundle (nothing stored).</summary>
    public static readonly CredentialBundle Empty = new();

    /// <summary>True when nothing at all is stored (no token, cookie, or extra field).</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(AccessToken) &&
        string.IsNullOrEmpty(RefreshToken) &&
        (Cookies is null || Cookies.Count == 0) &&
        (Extra is null || Extra.Count == 0);

    /// <summary>True when an access token is present.</summary>
    public bool HasAccessToken => !string.IsNullOrEmpty(AccessToken);

    /// <summary>True when a refresh token is present.</summary>
    public bool HasRefreshToken => !string.IsNullOrEmpty(RefreshToken);

    /// <summary>True when one or more cookies are present.</summary>
    public bool HasCookies => Cookies is { Count: > 0 };
}
