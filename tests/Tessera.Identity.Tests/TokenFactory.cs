using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Tessera.Identity.Tests;

/// <summary>
/// Mints signed JWTs with a throwaway RSA key and exposes that key as a static
/// JWKS, so the validator can be exercised fully offline (no Entra, no network).
/// </summary>
internal sealed class TokenFactory
{
    public const string Issuer = "https://login.microsoftonline.com/test-tenant/v2.0";
    public const string Audience = "11111111-2222-3333-4444-555555555555";
    public const string TenantId = "test-tenant";

    private readonly RsaSecurityKey _key;
    private readonly SigningCredentials _credentials;

    public TokenFactory()
    {
        _key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key-1" };
        _credentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>A static configuration manager that trusts this factory's signing key.</summary>
    public IConfigurationManager<OpenIdConnectConfiguration> ConfigurationManager()
    {
        var config = new OpenIdConnectConfiguration { Issuer = Issuer };
        config.SigningKeys.Add(_key);
        return new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
    }

    /// <summary>A second factory whose key is NOT trusted (for tamper tests).</summary>
    public static TokenFactory Untrusted() => new();

    public string UserToken(
        string oid = "oid-alice",
        string preferredUsername = "alice@example.com",
        string? audience = null,
        string? issuer = null,
        string? tenantId = null,
        DateTime? expires = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = oid,
            ["preferred_username"] = preferredUsername,
            ["tid"] = tenantId ?? TenantId,
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        return Create(claims, audience, issuer, expires);
    }

    public string AppOnlyToken(string appId = "app-crawler-9999", string? audience = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["azp"] = appId,
            ["appid"] = appId,
            ["idtyp"] = "app",
            ["tid"] = TenantId,
            ["roles"] = "Tessera.Call",
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        return Create(claims, audience, issuer: null, expires: null);
    }

    private string Create(Dictionary<string, object> claims, string? audience, string? issuer, DateTime? expires)
    {
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Claims = claims,
            SigningCredentials = _credentials,
        };
        return handler.CreateToken(descriptor);
    }
}
