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

        // Static-header values may reference a bundle `extra` field as {extra:key},
        // so an operator can put a per-account API key in the vault, not in config.
        ResolveExtraPlaceholders(headers, bundle);

        switch (recipe.Injection)
        {
            case InjectionKind.BearerToken when bundle.HasAccessToken:
                headers["Authorization"] = $"Bearer {bundle.AccessToken}";
                return headers;

            case InjectionKind.Cookies when bundle.HasCookies:
                headers["Cookie"] = string.Join("; ", bundle.Cookies!.Select(kv => $"{kv.Key}={kv.Value}"));
                return headers;

            case InjectionKind.None:
            case InjectionKind.BearerToken:
            case InjectionKind.Cookies:
            default:
                return null; // missing the required credential material
        }
    }

    private static void ResolveExtraPlaceholders(Dictionary<string, string> headers, CredentialBundle bundle)
    {
        if (bundle.Extra is null || bundle.Extra.Count == 0)
        {
            return;
        }

        foreach (var key in headers.Keys.ToArray())
        {
            var value = headers[key];
            if (value.StartsWith("{extra:", StringComparison.Ordinal) && value.EndsWith('}'))
            {
                var field = value[7..^1];
                headers[key] = bundle.Extra.TryGetValue(field, out var v) ? v : "";
            }
        }
    }
}
