using System.Text.Json;

namespace Tessera.Core.Configuration;

/// <summary>
/// Loads the broker config and the policy document from JSON, applying a handful
/// of environment overrides for container deploys. A missing file is never an
/// error — config falls back to safe defaults, and a missing policy document means
/// deny-all (fail-closed).
/// </summary>
public static class ConfigLoader
{
    /// <summary>The shared JSON options: case-insensitive, comments + trailing commas allowed.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Environment overrides for the most common container settings.</summary>
    private const string EnvPrefix = "TESSERA_";

    /// <summary>Loads the broker config from <paramref name="path"/> (or defaults), then applies env overrides.</summary>
    public static TesseraConfig LoadConfig(string? path, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var config = new TesseraConfig();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<TesseraConfig>(json, JsonOptions) ?? new TesseraConfig();
        }

        return ApplyEnvironmentOverrides(config, environment ?? ReadEnvironment());
    }

    /// <summary>Loads the policy document (grants + bindings + recipes). Missing file = empty (deny-all).</summary>
    public static LoadedPolicy LoadPolicy(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return LoadedPolicy.Empty;
        }

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<PolicyDocumentDto>(json, JsonOptions);
        return dto is null ? LoadedPolicy.Empty : LoadedPolicy.FromDto(dto);
    }

    /// <summary>The write options for a policy document: camelCase to match the
    /// hand-authored files, indented, and nulls/empties omitted so a round-trip
    /// stays clean and reviewable as a diff.</summary>
    private static readonly JsonSerializerOptions PolicyWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Persists a policy document back to <paramref name="path"/> as JSON (the
    /// reverse of <see cref="LoadPolicy"/>). Used by the admin portal's
    /// add-connection write — the files stay the source of truth (ADR 0008), so a
    /// connection added in the UI lands as a reviewable change in the same document.
    /// Throws <see cref="IOException"/> on a read-only mount (the GitOps case), which
    /// the caller treats as "in-memory only this session" rather than a hard failure.
    /// </summary>
    public static void SavePolicy(string path, LoadedPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(policy);
        var json = JsonSerializer.Serialize(policy.ToDocument(), PolicyWriteOptions);
        File.WriteAllText(path, json);
    }

    private static TesseraConfig ApplyEnvironmentOverrides(TesseraConfig config, IReadOnlyDictionary<string, string?> env)
    {
        var server = config.Server;
        var host = Get(env, "SERVER_HOST");
        var portRaw = Get(env, "SERVER_PORT");
        if (host is not null || portRaw is not null)
        {
            server = new ServerOptions
            {
                Host = host ?? server.Host,
                Port = int.TryParse(portRaw, out var p) ? p : server.Port,
            };
        }

        var identity = config.Identity;
        var mode = Get(env, "IDENTITY_MODE");
        var issuer = Get(env, "OIDC_ISSUER");
        var audience = Get(env, "OIDC_AUDIENCE");
        var tenantId = Get(env, "OIDC_TENANT_ID");
        var allowedTenants = Get(env, "OIDC_ALLOWED_TENANTS");
        var spaScope = Get(env, "OIDC_SPA_SCOPE");
        var trustDomain = Get(env, "TRUST_DOMAIN");
        if (mode is not null || issuer is not null || audience is not null || tenantId is not null || allowedTenants is not null || spaScope is not null || trustDomain is not null)
        {
            identity = new IdentityOptions
            {
                Mode = mode ?? identity.Mode,
                TrustDomain = trustDomain ?? identity.TrustDomain,
                Oidc = new OidcOptions
                {
                    Issuer = issuer ?? identity.Oidc.Issuer,
                    Audience = audience ?? identity.Oidc.Audience,
                    TenantId = tenantId ?? identity.Oidc.TenantId,
                    AllowedTenants = allowedTenants is not null
                        ? allowedTenants.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : identity.Oidc.AllowedTenants,
                    SpaScope = spaScope ?? identity.Oidc.SpaScope,
                },
            };
        }

        var policy = config.Policy;
        var policyDefault = Get(env, "POLICY_DEFAULT");
        var policyDocument = Get(env, "POLICY_DOCUMENT");
        if (policyDefault is not null || policyDocument is not null)
        {
            policy = new PolicyOptions
            {
                Default = policyDefault ?? policy.Default,
                Document = policyDocument ?? policy.Document,
            };
        }

        var egress = config.Egress;
        var egressEnabled = Get(env, "EGRESS_ENABLED");
        if (egressEnabled is not null)
        {
            egress = new EgressOptions
            {
                Enabled = egressEnabled is "1" or "true" or "TRUE",
                AllowedHosts = egress.AllowedHosts,
            };
        }

        var portal = config.Portal;
        var portalAdmins = Get(env, "PORTAL_ADMINS");
        var portalWebRoot = Get(env, "WEB_ROOT");
        if (portalAdmins is not null || portalWebRoot is not null)
        {
            portal = new PortalOptions
            {
                Admins = portalAdmins is not null
                    ? portalAdmins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : portal.Admins,
                WebRoot = portalWebRoot ?? portal.WebRoot,
            };
        }

        return new TesseraConfig
        {
            Server = server,
            Identity = identity,
            Policy = policy,
            Audit = config.Audit,
            Egress = egress,
            Portal = portal,
        };
    }

    private static string? Get(IReadOnlyDictionary<string, string?> env, string key) =>
        env.TryGetValue(EnvPrefix + key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static Dictionary<string, string?> ReadEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string;
        }

        return result;
    }
}
