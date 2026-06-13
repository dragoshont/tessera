using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Tessera.Identity;

/// <summary>Validates a forwarded OIDC access token, fail-closed.</summary>
public interface ITokenValidator
{
    /// <summary>True once delegation can be enforced (an audience is configured).</summary>
    bool DelegationEnabled { get; }

    /// <summary>Validates a bearer access token and returns the result (never throws on a bad token).</summary>
    Task<TesseraTokenResult> ValidateAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates Microsoft Entra OIDC <em>access</em> tokens (ADR 0011). It checks the
/// signature against Entra's JWKS, the issuer, the audience (the shared system app
/// — Flow B), the lifetime, and the tenant. It deliberately validates the
/// <em>access</em> token: an <c>id_token</c>'s <c>aud</c> is always the login
/// client (the classic ID-token-as-access-token trap), so feeding an id_token here
/// would be rejected by the audience check.
/// </summary>
public sealed class EntraTokenValidator : ITokenValidator
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configManager;
    private readonly OidcValidationOptions _options;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Creates a validator over an (injectable) OIDC configuration source.</summary>
    public EntraTokenValidator(IConfigurationManager<OpenIdConnectConfiguration> configManager, OidcValidationOptions options)
    {
        _configManager = configManager;
        _options = options;
    }

    /// <summary>Builds a validator that fetches Entra's JWKS from the issuer's discovery document.</summary>
    public static EntraTokenValidator Create(OidcValidationOptions options)
    {
        var metadataAddress = options.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
        return new EntraTokenValidator(configManager, options);
    }

    /// <inheritdoc/>
    public bool DelegationEnabled => _options.DelegationEnabled;

    /// <inheritdoc/>
    public async Task<TesseraTokenResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        // Fail-closed: until a real forwarded token's audience is confirmed (G2/C3),
        // delegation is off and every token is denied.
        if (!_options.DelegationEnabled)
        {
            return TesseraTokenResult.Fail("delegation fail-closed: no OIDC audience configured (gate G2/C3)");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return TesseraTokenResult.Fail("no token presented");
        }

        OpenIdConnectConfiguration config;
        try
        {
            config = await _configManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return TesseraTokenResult.Fail($"could not fetch OIDC metadata: {ex.Message}");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // Flow B: aud is the shared system app id. Accept both the bare id (v2
            // access tokens) and the api:// form (v1) for tolerance.
            ValidAudiences = [_options.Audience, $"api://{_options.Audience}"],
            ValidateAudience = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = _options.ClockSkew,
        };

        if (_options.IsMultiTenantAuthority)
        {
            // A /common (or /organizations, /consumers) token's real `iss` is the
            // PER-TENANT URL, never the authority. Validate it matches the Entra
            // template for the token's own `tid`, optionally restricted to an
            // allow-list of tenants (Microsoft's documented multi-tenant pattern).
            parameters.IssuerValidator = ValidateTemplateIssuer;
        }
        else
        {
            parameters.ValidIssuer = _options.Issuer;
        }

        var result = await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return TesseraTokenResult.Fail($"token rejected: {result.Exception?.Message ?? "invalid"}");
        }

        var claims = Flatten(result.Claims);

        if (!string.IsNullOrEmpty(_options.TenantId)
            && claims.TryGetValue("tid", out var tid)
            && !string.Equals(tid, _options.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return TesseraTokenResult.Fail("token tenant (tid) does not match the configured tenant");
        }

        return TesseraTokenResult.Success(claims);
    }

    private static Dictionary<string, string> Flatten(IDictionary<string, object> claims)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in claims)
        {
            flat[key] = value switch
            {
                string s => s,
                null => "",
                _ => value.ToString() ?? "",
            };
        }

        return flat;
    }

    /// <summary>
    /// Validates a multi-tenant issuer: the token's <c>iss</c> must equal
    /// <c>https://login.microsoftonline.com/&lt;tid&gt;/v2.0</c> for the token's own
    /// <c>tid</c>, and (when an allow-list is set) that <c>tid</c> must be allowed.
    /// Returns the issuer when valid; throws otherwise (fail-closed).
    /// </summary>
    private string ValidateTemplateIssuer(string issuer, SecurityToken token, TokenValidationParameters parameters)
    {
        string? tid = null;
        if (token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var value))
        {
            tid = value;
        }

        if (string.IsNullOrEmpty(tid))
        {
            throw new SecurityTokenInvalidIssuerException("token has no tid claim for multi-tenant issuer validation")
            {
                InvalidIssuer = issuer,
            };
        }

        var expected = $"https://login.microsoftonline.com/{tid}/v2.0";
        if (!string.Equals(issuer, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityTokenInvalidIssuerException($"issuer '{issuer}' does not match the Entra template for tid '{tid}'")
            {
                InvalidIssuer = issuer,
            };
        }

        if (_options.AllowedTenants.Count > 0
            && !_options.AllowedTenants.Contains(tid, StringComparer.OrdinalIgnoreCase))
        {
            throw new SecurityTokenInvalidIssuerException($"tenant '{tid}' is not in the allow-list")
            {
                InvalidIssuer = issuer,
            };
        }

        return issuer;
    }
}
