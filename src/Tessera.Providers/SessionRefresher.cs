using System.Text.Json;
using Tessera.Core.Egress;
using Tessera.Core.Recipes;
using Tessera.Core.Stores;

namespace Tessera.Providers;

/// <summary>The outcome of a refresh attempt.</summary>
public enum RefreshStatus
{
    /// <summary>Refresh is not configured for this provider.</summary>
    NotConfigured = 0,

    /// <summary>The session was rotated and written back.</summary>
    Rotated,

    /// <summary>The refresh token is dead — an interactive re-login is required (report, don't auto-relogin).</summary>
    Dead,

    /// <summary>The transport or store failed.</summary>
    Error,
}

/// <summary>The result of a refresh (secret-free).</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">A secret-free explanation.</param>
public sealed record RefreshResult(RefreshStatus Status, string Detail = "");

/// <summary>
/// Rotates a provider session and writes the rotated bundle back to the store — the
/// <em>sole session owner</em> path (ADR 0014). This is used ONLY in Phase B, after
/// Tessera has taken over rotation from any prior owner; running it while another
/// component owns the same single-use session would corrupt it. A dead refresh
/// token is <em>reported</em>, never auto-relogged-in (consent-gated, review H3/G3).
/// </summary>
public sealed class SessionRefresher
{
    private readonly IHttpTransport _transport;
    private readonly ICredentialWriter _writer;
    private readonly SsrfGuard _guard;

    /// <summary>Creates a refresher over the transport + a store writer.</summary>
    /// <param name="transport">The HTTP transport that performs the refresh call.</param>
    /// <param name="writer">The store writer for the rotated bundle.</param>
    /// <param name="guard">
    /// The SSRF allow-list the refresh URL must pass (the same list the data egress
    /// uses). <b>Required</b> — the refresher egresses, so it can never reach a host
    /// the data egress couldn't; an OAuth token endpoint on a different host must be
    /// allow-listed too. There is deliberately no unguarded constructor.
    /// </param>
    public SessionRefresher(IHttpTransport transport, ICredentialWriter writer, SsrfGuard guard)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(guard);
        _transport = transport;
        _writer = writer;
        _guard = guard;
    }

    /// <summary>Refreshes <paramref name="secretName"/>'s session per <paramref name="spec"/> and writes it back.</summary>
    public async Task<RefreshResult> RefreshAsync(
        Recipe recipe,
        RefreshSpec? spec,
        string secretName,
        CredentialBundle current,
        CancellationToken cancellationToken = default)
    {
        if (spec is null || recipe.UpstreamBaseUrl is null)
        {
            return new RefreshResult(RefreshStatus.NotConfigured, "no refresh spec");
        }

        var headers = ProviderHeaders.Build(recipe, current);
        if (headers is null)
        {
            return new RefreshResult(RefreshStatus.Dead, "no current credential to refresh");
        }

        // Use the absolute OAuth token endpoint when declared (a different host than
        // the data API, e.g. login.microsoftonline.com vs graph.microsoft.com),
        // otherwise the data base URL + path. Either way it must pass the SSRF
        // allow-list — the refresher never reaches a host the data egress couldn't.
        var url = !string.IsNullOrWhiteSpace(spec.TokenUrl)
            ? spec.TokenUrl!
            : recipe.UpstreamBaseUrl.TrimEnd('/') + "/" + spec.Path.TrimStart('/');

        if (!_guard.IsAllowed(url))
        {
            return new RefreshResult(RefreshStatus.Error, "refresh URL host is not on the SSRF allow-list");
        }

        TransportResponse response;
        try
        {
            response = await _transport.SendAsync(spec.Method, url, headers, "", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RefreshResult(RefreshStatus.Error, ex.Message);
        }

        if (response.Status is 401 or 403)
        {
            // The refresh token is dead — a real interactive login is needed. We
            // REPORT this; we never drive a login (consent-gated).
            return new RefreshResult(RefreshStatus.Dead, $"refresh rejected (HTTP {response.Status}) — interactive login needed");
        }

        if (response.Status is < 200 or >= 300)
        {
            return new RefreshResult(RefreshStatus.Error, $"refresh HTTP {response.Status}");
        }

        var rotated = MergeRotated(current, spec, response);

        try
        {
            await _writer.PutBundleAsync(secretName, rotated, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException ex)
        {
            return new RefreshResult(RefreshStatus.Error, $"write-back failed: {ex.Message}");
        }

        return new RefreshResult(RefreshStatus.Rotated, "session rotated + written back");
    }

    private static CredentialBundle MergeRotated(CredentialBundle current, RefreshSpec spec, TransportResponse response)
    {
        var access = current.AccessToken;
        var refresh = current.RefreshToken;

        if (!string.IsNullOrEmpty(response.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty(spec.AccessTokenField, out var a) && a.ValueKind == JsonValueKind.String)
                    {
                        access = a.GetString();
                    }

                    if (root.TryGetProperty(spec.RefreshTokenField, out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        refresh = r.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // No JSON body — rely on Set-Cookie (below) if enabled.
            }
        }

        var cookies = current.Cookies;
        if (spec.AbsorbSetCookie
            && (response.Headers.TryGetValue("Set-Cookie", out var sc) || response.Headers.TryGetValue("set-cookie", out sc))
            && !string.IsNullOrEmpty(sc))
        {
            cookies = AbsorbCookies(current.Cookies, sc);
        }

        return current with { AccessToken = access, RefreshToken = refresh, Cookies = cookies };
    }

    private static Dictionary<string, string> AbsorbCookies(IReadOnlyDictionary<string, string>? existing, string setCookie)
    {
        var map = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing, StringComparer.Ordinal);

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
