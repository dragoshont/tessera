using Tessera.Core.Recipes;
using Tessera.Core.Stores;

namespace Tessera.Providers;

/// <summary>Builds the request headers that inject a stored credential for a recipe.</summary>
internal static class ProviderHeaders
{
    /// <summary>
    /// Returns the headers for a call: the recipe's static headers, plus the
    /// injected credential (cookie or bearer) from the bundle. Returns an empty
    /// dictionary when the bundle lacks the material the injection kind needs (the
    /// egress then refuses the call rather than send an unauthenticated request).
    /// </summary>
    public static Dictionary<string, string>? Build(Recipe recipe, CredentialBundle bundle)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };

        foreach (var (name, value) in recipe.StaticHeaders)
        {
            headers[name] = value;
        }

        // Static-header values may reference a bundle `extra` field as {extra:key}
        // (per-account secret kept in the vault, not config) or a process env var
        // as {env:NAME} (a provider-wide key projected from a K8s secret).
        ResolvePlaceholders(headers, bundle);

        switch (recipe.Injection)
        {
            case InjectionKind.BearerToken when bundle.HasAccessToken:
                headers["Authorization"] = $"Bearer {bundle.AccessToken}";
                return headers;

            case InjectionKind.ApiKeyHeader when bundle.HasAccessToken:
                // The Servarr/Seerr class: the access token is the API key, injected
                // into a named header (X-Api-Key by default) rather than as a bearer.
                headers[recipe.EffectiveInjectionHeader] = bundle.AccessToken!;
                return headers;

            case InjectionKind.Cookies:
                var cookie = BuildCookieHeader(recipe, bundle);
                if (cookie is null)
                {
                    return null; // a required cookie source was missing
                }
                headers["Cookie"] = cookie;
                return headers;

            case InjectionKind.Basic
                when bundle.HasAccessToken && bundle.Extra is not null
                    && bundle.Extra.TryGetValue("username", out var basicUser)
                    && !string.IsNullOrEmpty(basicUser):
                var basicToken = System.Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{basicUser}:{bundle.AccessToken}"));
                headers["Authorization"] = $"Basic {basicToken}";
                return headers;

            case InjectionKind.None:
            case InjectionKind.BearerToken:
            case InjectionKind.ApiKeyHeader:
            case InjectionKind.Basic:
            default:
                return null; // missing the required credential material
        }
    }

    /// <summary>
    /// Builds the <c>Cookie</c> header. When the recipe declares a cookie map
    /// (cookie name → bundle source) the header is assembled from named bundle
    /// fields — so a portal that carries its session as <c>TokenSSO</c>/
    /// <c>RefreshTokenSSO</c> cookies can be fed from the bundle's access/refresh
    /// tokens. Otherwise the raw cookie dict is joined as-is. Returns null when a
    /// required source is absent so the egress refuses rather than send a half-auth
    /// request.
    /// </summary>
    private static string? BuildCookieHeader(Recipe recipe, CredentialBundle bundle)
    {
        if (recipe.CookieSources.Count > 0)
        {
            var parts = new List<string>(recipe.CookieSources.Count);
            foreach (var (cookieName, source) in recipe.CookieSources)
            {
                var value = ResolveCookieSource(source, bundle);
                if (string.IsNullOrEmpty(value))
                {
                    return null; // declared cookie has no backing value
                }
                parts.Add($"{cookieName}={value}");
            }
            return string.Join("; ", parts);
        }

        return bundle.HasCookies
            ? string.Join("; ", bundle.Cookies!.Select(kv => $"{kv.Key}={kv.Value}"))
            : null;
    }

    private static string? ResolveCookieSource(string source, CredentialBundle bundle) => source switch
    {
        "access_token" => bundle.AccessToken,
        "refresh_token" => bundle.RefreshToken,
        _ when source.StartsWith("cookie:", StringComparison.Ordinal) =>
            bundle.Cookies is not null && bundle.Cookies.TryGetValue(source[7..], out var c) ? c : null,
        _ => null,
    };

    private static void ResolvePlaceholders(Dictionary<string, string> headers, CredentialBundle bundle)
    {
        foreach (var key in headers.Keys.ToArray())
        {
            var value = headers[key];
            if (value.StartsWith("{extra:", StringComparison.Ordinal) && value.EndsWith('}'))
            {
                var field = value[7..^1];
                headers[key] = bundle.Extra is not null && bundle.Extra.TryGetValue(field, out var v) ? v : "";
            }
            else if (value.StartsWith("{env:", StringComparison.Ordinal) && value.EndsWith('}'))
            {
                var name = value[5..^1];
                headers[key] = Environment.GetEnvironmentVariable(name) ?? "";
            }
        }
    }
}
