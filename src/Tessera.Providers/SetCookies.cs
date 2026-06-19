namespace Tessera.Providers;

/// <summary>
/// Parses a <c>Set-Cookie</c> response header into its <c>name → value</c> pairs.
/// Shared by the refresh path (<see cref="SessionRefresher"/>) and the per-read
/// write-back path (<see cref="CookieWriteBack"/>) so the two read a rotated
/// session identically.
/// </summary>
internal static class SetCookies
{
    /// <summary>
    /// Splits a (possibly comma-joined) <c>Set-Cookie</c> header value into cookie
    /// <c>name → value</c> pairs, dropping the attributes (<c>Path</c>, <c>Domain</c>,
    /// <c>Expires</c>, <c>HttpOnly</c>, …). Best-effort and total: a malformed or
    /// attribute-only segment is skipped, never thrown. A segment whose name carries
    /// a space (e.g. the date inside a comma-split <c>Expires=Wed, 09 Jun …</c>) is
    /// rejected, so a split date can't masquerade as a cookie.
    /// </summary>
    public static Dictionary<string, string> Parse(string? setCookie)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(setCookie))
        {
            return map;
        }

        foreach (var part in setCookie.Split(','))
        {
            var seg = part.Trim();
            var eq = seg.IndexOf('=', StringComparison.Ordinal);
            var semi = seg.IndexOf(';', StringComparison.Ordinal);
            if (eq > 0)
            {
                var name = seg[..eq].Trim();
                var value = (semi > eq ? seg[(eq + 1)..semi] : seg[(eq + 1)..]).Trim();
                if (name.Length > 0 && !name.Contains(' ', StringComparison.Ordinal))
                {
                    map[name] = value;
                }
            }
        }

        return map;
    }
}
