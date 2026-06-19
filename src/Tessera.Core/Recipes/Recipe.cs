namespace Tessera.Core.Recipes;

/// <summary>How the broker performs the upstream call for a target.</summary>
public enum EgressMode
{
    /// <summary>
    /// No upstream egress. The broker only authorizes and reports credential
    /// status (the safe, read-only surface — iteration 1 default).
    /// </summary>
    None = 0,

    /// <summary>
    /// HTTP-injectable: the broker injects the stored credential and forwards the
    /// request to an allow-listed upstream (YARP). Gated behind an SSRF allow-list.
    /// </summary>
    Http,
}

/// <summary>How a stored credential is injected into an HTTP upstream call.</summary>
public enum InjectionKind
{
    /// <summary>No injection.</summary>
    None = 0,

    /// <summary>Inject the access token as an <c>Authorization: Bearer</c> header.</summary>
    BearerToken,

    /// <summary>Inject the stored cookies as a <c>Cookie</c> header.</summary>
    Cookies,

    /// <summary>
    /// Inject the access token into a named API-key header (default <c>X-Api-Key</c>)
    /// — the Servarr / Seerr / *arr provider class, which authenticates with a key
    /// header rather than an OAuth bearer. The header name is the recipe's
    /// <see cref="Recipe.InjectionHeader"/>.
    /// </summary>
    ApiKeyHeader,
}

/// <summary>
/// Who owns rotating (keeping warm) a provider's session — operator-declared
/// provider knowledge surfaced by the awareness dashboard (ADR 0017). It is a
/// <em>declaration</em>, not a behaviour: Tessera only actually rotates when its
/// own refresher is wired (Mode U, ADR 0015). Until then a module that is kept warm
/// elsewhere declares <c>external</c> so the portal can say so honestly.
/// </summary>
/// <param name="Owner"><c>none</c> (static, re-seed by hand), <c>external</c> (a domain component keeps it warm — today's Mode P), or <c>tessera</c> (Tessera's refresher owns it — Mode U).</param>
/// <param name="Detail">An optional secret-free explanation shown to the user.</param>
public sealed record RecipeRotation(string Owner, string? Detail = null);

/// <summary>
/// A provider recipe — the easy-setup unit that names a target, the harvest driver
/// that keeps it warm, and how the broker reaches it (ADR 0006 / 0002). A recipe
/// changes neither the broker nor the policy model; adding a provider is additive.
/// </summary>
/// <param name="Target">The target name (matches grants + bindings).</param>
/// <param name="Driver">The harvest driver: <c>browser</c> (now), <c>android</c>/<c>desktop</c> (future).</param>
/// <param name="Egress">How the broker performs the upstream call.</param>
/// <param name="UpstreamBaseUrl">The allow-listed upstream base URL for HTTP egress.</param>
/// <param name="Injection">How the credential is injected for HTTP egress.</param>
/// <param name="Actions">The action verbs this recipe exposes (drives the MCP tool surface).</param>
/// <param name="Tools">The callable HTTP operations this recipe exposes (ADR 0014).</param>
/// <param name="ExtraHeaders">Static non-secret headers every call needs (values may use <c>{extra:key}</c> from the bundle or <c>{env:NAME}</c> from the process env).</param>
/// <param name="CookieMap">For cookie injection: cookie name → bundle source (<c>access_token</c> / <c>refresh_token</c> / <c>cookie:&lt;name&gt;</c>). When set, the <c>Cookie</c> header is built from this map instead of the raw cookie dict.</param>
/// <param name="InjectionHeader">For <see cref="InjectionKind.ApiKeyHeader"/>: the header name the access token is injected into (default <c>X-Api-Key</c>).</param>
/// <param name="Description">A human-readable description.</param>
/// <param name="Rotation">Who owns rotating this provider's session (awareness dashboard, ADR 0017); null ⇒ no rotation declared (static).</param>
/// <param name="Refresh">How Tessera rotates this session when it is the owner (Mode U, ADR 0015); null ⇒ no Tessera-owned refresh (static or external).</param>
/// <param name="AbsorbSetCookie">For cookie injection: when true, a session the upstream rotates on a tool call (a <c>Set-Cookie</c> on a 2xx response) is captured and written back to the store, so the next read uses the live session instead of a stale one (ADR 0014/0015). The rotated cookies are reverse-mapped through <see cref="CookieMap"/>; requires a writable store. Default false (the broker only reads).</param>
public sealed record Recipe(
    string Target,
    string Driver = "browser",
    EgressMode Egress = EgressMode.None,
    string? UpstreamBaseUrl = null,
    InjectionKind Injection = InjectionKind.None,
    IReadOnlyList<string>? Actions = null,
    IReadOnlyList<RecipeTool>? Tools = null,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null,
    IReadOnlyDictionary<string, string>? CookieMap = null,
    string? InjectionHeader = null,
    string? Description = null,
    RecipeRotation? Rotation = null,
    RefreshSpec? Refresh = null,
    bool AbsorbSetCookie = false)
{
    /// <summary>The action verbs this recipe exposes (never null).</summary>
    public IReadOnlyList<string> ExposedActions => Actions ?? [];

    /// <summary>The callable HTTP operations this recipe exposes (never null).</summary>
    public IReadOnlyList<RecipeTool> ExposedTools => Tools ?? [];

    /// <summary>Static non-secret headers to send on every call (never null).</summary>
    public IReadOnlyDictionary<string, string> StaticHeaders => ExtraHeaders ?? EmptyHeaders;

    /// <summary>Cookie-name → bundle-source mapping for cookie injection (never null).</summary>
    public IReadOnlyDictionary<string, string> CookieSources => CookieMap ?? EmptyHeaders;

    /// <summary>The API-key header name for <see cref="InjectionKind.ApiKeyHeader"/> (default <c>X-Api-Key</c>).</summary>
    public string EffectiveInjectionHeader =>
        string.IsNullOrWhiteSpace(InjectionHeader) ? "X-Api-Key" : InjectionHeader;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();
}
