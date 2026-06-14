using System.Text.Json.Serialization;

namespace Tessera.Core.Configuration;

/// <summary>HTTP listener settings.</summary>
public sealed class ServerOptions
{
    /// <summary>Bind address (default loopback).</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Bind port.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>True when bound to a loopback address.</summary>
    [JsonIgnore]
    public bool IsLoopback => Host is "127.0.0.1" or "::1" or "localhost";
}

/// <summary>OIDC end-user/automation token validation settings (Entra — ADR 0011).</summary>
public sealed class OidcOptions
{
    /// <summary>The expected token issuer, e.g. <c>https://login.microsoftonline.com/&lt;tid&gt;/v2.0</c>.</summary>
    public string Issuer { get; init; } = "";

    /// <summary>
    /// The expected token <c>aud</c> (the shared system app — Flow B). EMPTY means
    /// OIDC delegation is <em>fail-closed</em>: until a real forwarded token's
    /// audience is confirmed (gate G2/C3), Tessera denies token-authenticated calls.
    /// </summary>
    public string Audience { get; init; } = "";

    /// <summary>The expected tenant id (<c>tid</c> claim).</summary>
    public string TenantId { get; init; } = "";

    /// <summary>
    /// For a multi-tenant authority (<c>/common</c> etc.), the tenant IDs allowed
    /// to sign in. Empty = any tenant whose issuer matches the Entra template.
    /// </summary>
    public IReadOnlyList<string> AllowedTenants { get; init; } = [];

    /// <summary>
    /// The OAuth scope the admin-portal SPA requests at sign-in (ADR 0016). It must
    /// yield an access token whose <c>aud</c> equals <see cref="Audience"/> so the
    /// broker validates it. Default (empty) ⇒ the portal config endpoint derives
    /// <c>openid profile email &lt;audience&gt;/.default</c>. Override it (e.g. to
    /// <c>api://&lt;audience&gt;/access</c>) when the app exposes a named scope via
    /// an Application ID URI. Only consumed by the portal sign-in, never the broker.
    /// </summary>
    public string SpaScope { get; init; } = "";

    /// <summary>True once an audience is configured (delegation can be enforced).</summary>
    [JsonIgnore]
    public bool DelegationEnabled => !string.IsNullOrWhiteSpace(Audience);
}

/// <summary>Caller/end-user identity settings (ADR 0005).</summary>
public sealed class IdentityOptions
{
    /// <summary>Identity mode: <c>mtls</c> | <c>oidc</c> | <c>dev</c>.</summary>
    public string Mode { get; init; } = "mtls";

    /// <summary>The workload trust domain (for the self-test caller id).</summary>
    public string TrustDomain { get; init; } = "tessera.local";

    /// <summary>OIDC validation settings (used when end-user tokens are forwarded).</summary>
    public OidcOptions Oidc { get; init; } = new();
}

/// <summary>Policy settings (ADR 0008).</summary>
public sealed class PolicyOptions
{
    /// <summary>Default effect — must be <c>deny</c> (an <c>allow</c> default is fail-open).</summary>
    public string Default { get; init; } = "deny";

    /// <summary>Path to the policy document (grants + bindings + recipes).</summary>
    public string Document { get; init; } = "grants.json";
}

/// <summary>Audit settings (ADR 0008).</summary>
public sealed class AuditOptions
{
    /// <summary>Whether audit is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Audit path; <c>-</c> means stdout (right for containers).</summary>
    public string Path { get; init; } = "-";

    /// <summary>
    /// How many recent entries the in-memory activity tail retains for the portal's
    /// feed (ADR 0017). Newest-wins, bounded, O(capacity) memory; a restart drops it
    /// (the JSONL sink → stdout/Loki stays the durable record). <c>0</c> disables the
    /// tail entirely (the portal activity feed is then empty), independent of
    /// <see cref="Enabled"/> — so an operator can keep durable audit while opting out
    /// of the in-memory mirror, or vice versa.
    /// </summary>
    public int TailCapacity { get; init; } = 1000;
}

/// <summary>Injection-egress settings (ADR 0001 / architecture §6).</summary>
public sealed class EgressOptions
{
    /// <summary>
    /// Whether the broker may make injected upstream calls. OFF by default —
    /// deploying the broker never opens an egress path until explicitly enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The SSRF allow-list: upstream hosts the broker may reach. Required (non-empty)
    /// when <see cref="Enabled"/> is true.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
}

/// <summary>Admin-portal settings (ADR 0016). The portal is a thin convenience
/// layer; the only decision it owns is "who is an operator" — a small allow-list,
/// never a database (spec §7b).</summary>
public sealed class PortalOptions
{
    /// <summary>
    /// The admins allow-list: verified principals (<c>oid</c> / <c>preferred_username</c>,
    /// e.g. an email) who may enter the operator surface. Everyone else is a Member
    /// who sees only their own connections. Empty = no operator (the portal is then
    /// self-service-only). This is the <em>only</em> portal authorization datum, and
    /// it lives in config so it is a reviewable diff like every other rule (ADR 0008).
    /// </summary>
    public IReadOnlyList<string> Admins { get; init; } = [];

    /// <summary>
    /// Optional path to the built SPA (the <c>web/dist</c> output) the host should
    /// serve. When set and the directory exists, the broker serves the admin portal
    /// at <c>/</c> alongside its API (same origin → no CORS). Unset = API only.
    /// </summary>
    public string? WebRoot { get; init; }
}

/// <summary>The full broker configuration, with fail-closed validation.</summary>
public sealed class TesseraConfig
{
    /// <summary>HTTP listener settings.</summary>
    public ServerOptions Server { get; init; } = new();

    /// <summary>Identity settings.</summary>
    public IdentityOptions Identity { get; init; } = new();

    /// <summary>Policy settings.</summary>
    public PolicyOptions Policy { get; init; } = new();

    /// <summary>Audit settings.</summary>
    public AuditOptions Audit { get; init; } = new();

    /// <summary>Injection-egress settings.</summary>
    public EgressOptions Egress { get; init; } = new();

    /// <summary>Admin-portal settings (the admins allow-list).</summary>
    public PortalOptions Portal { get; init; } = new();

    /// <summary>
    /// Returns a list of problems. Empty list == valid. These checks encode the
    /// security invariants; the most important: a <c>dev</c> (unverified) identity
    /// mode is tolerated only on loopback, and <c>policy.default = allow</c> is
    /// rejected as fail-open.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Server.Port is <= 0 or > 65535)
        {
            problems.Add($"server.port {Server.Port} is out of range (1-65535)");
        }

        if (Identity.Mode is not ("mtls" or "oidc" or "dev"))
        {
            problems.Add($"identity.mode \"{Identity.Mode}\" is invalid (expected \"mtls\", \"oidc\", or \"dev\")");
        }

        if (Identity.Mode == "dev" && !Server.IsLoopback)
        {
            problems.Add(
                "identity.mode \"dev\" disables caller verification and is only allowed on loopback, " +
                $"but server.host is \"{Server.Host}\". Use mtls/oidc, or bind to 127.0.0.1.");
        }

        if (Identity.Mode == "oidc" && string.IsNullOrWhiteSpace(Identity.Oidc.Issuer))
        {
            problems.Add("identity.mode \"oidc\" requires identity.oidc.issuer");
        }

        if (Policy.Default is not ("deny" or "allow"))
        {
            problems.Add($"policy.default \"{Policy.Default}\" is invalid (expected \"deny\" or \"allow\")");
        }

        if (Policy.Default == "allow")
        {
            problems.Add("policy.default \"allow\" is unsafe (fail-open). Set it to \"deny\" and grant explicitly.");
        }

        if (Egress is { Enabled: true, AllowedHosts.Count: 0 })
        {
            problems.Add("egress.enabled is true but egress.allowedHosts is empty (an SSRF allow-list is required).");
        }

        return problems;
    }
}
