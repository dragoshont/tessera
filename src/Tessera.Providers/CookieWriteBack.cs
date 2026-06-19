using Tessera.Core.Recipes;
using Tessera.Core.Stores;

namespace Tessera.Providers;

/// <summary>
/// Builds the write-back for a sliding session that the upstream rotated on a tool
/// call. When a recipe injects cookies and the provider answers a read with a fresh
/// session in <c>Set-Cookie</c>, the broker must persist the rotated material so the
/// next read uses the live session instead of a stale one — otherwise reads stop
/// extending the session and it dies between keep-warm passes (ADR 0014/0015).
/// </summary>
/// <remarks>
/// The rotated cookies are reverse-mapped through the recipe's <c>cookieMap</c>
/// (cookie name → bundle source): a <c>TokenSSO</c>/<c>RefreshTokenSSO</c> portal is
/// fed from — and writes back to — the bundle's access/refresh tokens. The result is
/// a <em>delta</em> bundle carrying only the fields that actually changed, so the
/// store's merge-then-write overlays just those and never clobbers the rest (the
/// external owner's other fields, or a concurrent re-login).
/// </remarks>
internal static class CookieWriteBack
{
    /// <summary>
    /// Returns the delta bundle to write back when <paramref name="responseHeaders"/>
    /// carry a rotated session for <paramref name="recipe"/>, or <c>null</c> when the
    /// recipe declares no cookie map, there is no <c>Set-Cookie</c>, or nothing
    /// changed (so a no-op never churns a store version).
    /// </summary>
    public static CredentialBundle? BuildRotation(
        Recipe recipe,
        CredentialBundle current,
        IReadOnlyDictionary<string, string> responseHeaders)
    {
        if (recipe.CookieSources.Count == 0 || !TryGetSetCookie(responseHeaders, out var raw))
        {
            return null;
        }

        var rotated = SetCookies.Parse(raw);
        if (rotated.Count == 0)
        {
            return null;
        }

        string? newAccess = null;
        string? newRefresh = null;
        Dictionary<string, string>? newCookies = null;
        var changed = false;

        foreach (var (cookieName, source) in recipe.CookieSources)
        {
            if (!rotated.TryGetValue(cookieName, out var value) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            switch (source)
            {
                case "access_token":
                    if (!string.Equals(value, current.AccessToken, StringComparison.Ordinal))
                    {
                        newAccess = value;
                        changed = true;
                    }

                    break;

                case "refresh_token":
                    if (!string.Equals(value, current.RefreshToken, StringComparison.Ordinal))
                    {
                        newRefresh = value;
                        changed = true;
                    }

                    break;

                default:
                    if (source.StartsWith("cookie:", StringComparison.Ordinal))
                    {
                        var key = source[7..];
                        var existing = current.Cookies is not null && current.Cookies.TryGetValue(key, out var c) ? c : null;
                        if (!string.Equals(value, existing, StringComparison.Ordinal))
                        {
                            // The store overwrites the whole `cookies` object on merge,
                            // so carry every existing cookie forward plus the rotated one.
                            newCookies ??= current.Cookies is null
                                ? new Dictionary<string, string>(StringComparer.Ordinal)
                                : new Dictionary<string, string>(current.Cookies, StringComparer.Ordinal);
                            newCookies[key] = value;
                            changed = true;
                        }
                    }

                    break;
            }
        }

        return changed
            ? new CredentialBundle(AccessToken: newAccess, RefreshToken: newRefresh, Cookies: newCookies)
            : null;
    }

    /// <summary>
    /// Reads the <c>Set-Cookie</c> header case-insensitively. The real transport keeps
    /// response headers in a case-insensitive map, but a test fake (or a future
    /// transport) may not — so both casings are tried.
    /// </summary>
    private static bool TryGetSetCookie(IReadOnlyDictionary<string, string> headers, out string value) =>
        headers.TryGetValue("Set-Cookie", out value!) || headers.TryGetValue("set-cookie", out value!);
}
