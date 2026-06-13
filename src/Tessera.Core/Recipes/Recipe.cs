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
}

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
/// <param name="ExtraHeaders">Static non-secret headers every call needs (e.g. an API key header name→value mapping resolved from the bundle's <c>extra</c>).</param>
/// <param name="Description">A human-readable description.</param>
public sealed record Recipe(
    string Target,
    string Driver = "browser",
    EgressMode Egress = EgressMode.None,
    string? UpstreamBaseUrl = null,
    InjectionKind Injection = InjectionKind.None,
    IReadOnlyList<string>? Actions = null,
    IReadOnlyList<RecipeTool>? Tools = null,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null,
    string? Description = null)
{
    /// <summary>The action verbs this recipe exposes (never null).</summary>
    public IReadOnlyList<string> ExposedActions => Actions ?? [];

    /// <summary>The callable HTTP operations this recipe exposes (never null).</summary>
    public IReadOnlyList<RecipeTool> ExposedTools => Tools ?? [];

    /// <summary>Static non-secret headers to send on every call (never null).</summary>
    public IReadOnlyDictionary<string, string> StaticHeaders => ExtraHeaders ?? EmptyHeaders;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();
}
