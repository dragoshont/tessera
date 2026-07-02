using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tessera.Core.OAuthMcp;

/// <summary>RFC 9728 protected-resource metadata (the subset we consume).</summary>
public sealed record ProtectedResourceMetadata(
    [property: JsonPropertyName("resource")] string? Resource,
    [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string>? AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string>? ScopesSupported,
    [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string>? BearerMethodsSupported)
{
    /// <summary>Deserialize the protected-resource document; null on empty/invalid.</summary>
    public static ProtectedResourceMetadata? FromJson(string json) =>
        JsonSerializer.Deserialize<ProtectedResourceMetadata>(json, OAuthJson.Options);
}

/// <summary>RFC 8414 authorization-server metadata (the subset we use to acquire).</summary>
public sealed record AuthorizationServerMetadata(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string>? ScopesSupported,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string>? CodeChallengeMethodsSupported)
{
    /// <summary>Deserialize the authorization-server document; null on empty/invalid.</summary>
    public static AuthorizationServerMetadata? FromJson(string json) =>
        JsonSerializer.Deserialize<AuthorizationServerMetadata>(json, OAuthJson.Options);

    /// <summary>True when the AS advertises PKCE <c>S256</c> (required by acquisition).</summary>
    public bool SupportsPkceS256 =>
        CodeChallengeMethodsSupported?.Contains("S256", StringComparer.Ordinal) ?? false;
}

internal static class OAuthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
