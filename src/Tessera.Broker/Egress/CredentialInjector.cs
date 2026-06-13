using Tessera.Core.Recipes;
using Tessera.Core.Stores;

namespace Tessera.Broker.Egress;

/// <summary>
/// Builds the HTTP header(s) that inject a stored credential into an upstream call.
/// The bytes are used here and never returned to the caller or logged ("inject,
/// never hand over" — MCP spec forbids token passthrough).
/// </summary>
public static class CredentialInjector
{
    /// <summary>
    /// Returns the headers to inject for <paramref name="injection"/>, or an empty
    /// list when the bundle lacks the required material (the egress then refuses).
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> BuildHeaders(CredentialBundle bundle, InjectionKind injection)
    {
        switch (injection)
        {
            case InjectionKind.BearerToken when bundle.HasAccessToken:
                return [("Authorization", $"Bearer {bundle.AccessToken}")];

            case InjectionKind.Cookies when bundle.HasCookies:
                var cookie = string.Join("; ", bundle.Cookies!.Select(kv => $"{kv.Key}={kv.Value}"));
                return [("Cookie", cookie)];

            case InjectionKind.None:
            case InjectionKind.BearerToken:
            case InjectionKind.Cookies:
            default:
                return [];
        }
    }
}
