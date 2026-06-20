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

            // HTTP Basic: username from the bundle's extra.username, password from
            // the access token (the iCloud CalDAV/CardDAV class — Apple ID +
            // app-specific password). The bytes are used here and never returned.
            case InjectionKind.Basic when bundle.HasAccessToken
                && bundle.Extra is not null
                && bundle.Extra.TryGetValue("username", out var basicUser)
                && !string.IsNullOrEmpty(basicUser):
                var basic = System.Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{basicUser}:{bundle.AccessToken}"));
                return [("Authorization", $"Basic {basic}")];

            case InjectionKind.None:
            case InjectionKind.BearerToken:
            case InjectionKind.Cookies:
            case InjectionKind.Basic:
            default:
                return [];
        }
    }
}
