using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;

namespace Tessera.Core.Configuration;

/// <summary>The wire form of the policy document (grants + bindings + recipes).</summary>
internal sealed class PolicyDocumentDto
{
    public List<GrantDto> Grants { get; init; } = [];
    public List<BindingDto> Bindings { get; init; } = [];
    public List<RecipeDto> Recipes { get; init; } = [];
}

internal sealed class GrantDto
{
    public string Caller { get; init; } = "";
    public string Target { get; init; } = "";
    public List<string> Actions { get; init; } = [];
    public string? OnBehalfOf { get; init; }
    public List<string>? StepUpActions { get; init; }
}

internal sealed class BindingDto
{
    public string Target { get; init; } = "";
    public string Credential { get; init; } = "";
    public string? OnBehalfOf { get; init; }
    public string? Owner { get; init; }
    public string? Guardian { get; init; }
}

internal sealed class RecipeDto
{
    public string Target { get; init; } = "";
    public string Driver { get; init; } = "browser";
    public string Egress { get; init; } = "none";
    public string? UpstreamBaseUrl { get; init; }
    public string? Injection { get; init; }
    public List<string>? Actions { get; init; }
    public List<RecipeToolDto>? Tools { get; init; }
    public Dictionary<string, string>? ExtraHeaders { get; init; }
    public Dictionary<string, string>? CookieMap { get; init; }
    public string? Description { get; init; }
    public RecipeRotationDto? Rotation { get; init; }
}

internal sealed class RecipeRotationDto
{
    public string Owner { get; init; } = "none";
    public string? Detail { get; init; }
}

internal sealed class RecipeToolDto
{
    public string Name { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "";
    public string Action { get; init; } = "";
    public bool StepUp { get; init; }
    public string? Description { get; init; }
    public string? Plane { get; init; }
}

/// <summary>
/// The loaded, validated policy: grants, bindings, and recipes in domain form.
/// A missing document file yields an empty policy — so every request is denied
/// (fail-closed).
/// </summary>
/// <param name="Grants">The authorization rules.</param>
/// <param name="Bindings">The target → store-secret bindings.</param>
/// <param name="Recipes">The provider recipes (drive the MCP tool surface).</param>
public sealed record LoadedPolicy(
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<TargetBinding> Bindings,
    IReadOnlyList<Recipe> Recipes)
{
    /// <summary>An empty policy (deny-all).</summary>
    public static readonly LoadedPolicy Empty = new([], [], []);

    internal static LoadedPolicy FromDto(PolicyDocumentDto dto)
    {
        var grants = dto.Grants
            .Select(g => new Grant(
                Caller: g.Caller,
                Target: g.Target,
                Actions: g.Actions,
                OnBehalfOf: g.OnBehalfOf,
                StepUpActions: g.StepUpActions))
            .ToArray();

        var bindings = dto.Bindings
            .Select(b => new TargetBinding(b.Target, b.Credential, b.OnBehalfOf, CredentialOwners.Parse(b.Owner), b.Guardian))
            .ToArray();

        var recipes = dto.Recipes
            .Select(r => new Recipe(
                Target: r.Target,
                Driver: r.Driver,
                Egress: ParseEgress(r.Egress),
                UpstreamBaseUrl: r.UpstreamBaseUrl,
                Injection: ParseInjection(r.Injection),
                Actions: r.Actions,
                Tools: r.Tools?
                    .Select(t => new RecipeTool(t.Name, t.Method, t.Path, t.Action, t.StepUp, t.Description, ParsePlane(t.Plane)))
                    .ToArray(),
                ExtraHeaders: r.ExtraHeaders,
                CookieMap: r.CookieMap,
                Description: r.Description,
                Rotation: r.Rotation is null ? null : new RecipeRotation(r.Rotation.Owner, r.Rotation.Detail)))
            .ToArray();

        return new LoadedPolicy(grants, bindings, recipes);    }

    private static EgressMode ParseEgress(string value) => value.ToLowerInvariant() switch
    {
        "http" => EgressMode.Http,
        _ => EgressMode.None,
    };

    private static InjectionKind ParseInjection(string? value) => value?.ToLowerInvariant() switch
    {
        "bearer" or "bearertoken" => InjectionKind.BearerToken,
        "cookie" or "cookies" => InjectionKind.Cookies,
        _ => InjectionKind.None,
    };

    // Only the three real planes round-trip; anything else (legacy/unknown) is null
    // ⇒ derive the plane from the action verb (RecipeTool.EffectivePlane).
    private static ActionPlane? ParsePlane(string? value) => value?.ToLowerInvariant() switch
    {
        "read" => ActionPlane.Read,
        "use" => ActionPlane.Use,
        "manage" => ActionPlane.Manage,
        _ => null,
    };

    /// <summary>
    /// Maps this policy back to its wire DTO (the reverse of <see cref="FromDto"/>)
    /// so it can be persisted by <c>ConfigLoader.SavePolicy</c>. Used by the admin
    /// portal's add-connection write — the document round-trips faithfully so a
    /// UI-added connection stays a reviewable change in the same file (ADR 0008).
    /// </summary>
    internal PolicyDocumentDto ToDocument() => new()
    {
        Grants = Grants
            .Select(g => new GrantDto
            {
                Caller = g.Caller,
                Target = g.Target,
                Actions = [.. g.Actions],
                OnBehalfOf = g.OnBehalfOf,
                StepUpActions = g.StepUpActions is { Count: > 0 } ? [.. g.StepUpActions] : null,
            })
            .ToList(),
        Bindings = Bindings
            .Select(b => new BindingDto
            {
                Target = b.Target,
                Credential = b.Credential,
                OnBehalfOf = b.Principal,
                // Persist only a non-default owner so service-owned bindings (the
                // default) round-trip back to null — a faithful document (ADR 0008).
                Owner = b.Owner == CredentialOwner.Service ? null : CredentialOwners.ToToken(b.Owner),
                Guardian = b.Guardian,
            })
            .ToList(),
        Recipes = Recipes
            .Select(r => new RecipeDto
            {
                Target = r.Target,
                Driver = r.Driver,
                Egress = r.Egress == EgressMode.Http ? "http" : "none",
                UpstreamBaseUrl = r.UpstreamBaseUrl,
                Injection = r.Injection switch
                {
                    InjectionKind.BearerToken => "bearer",
                    InjectionKind.Cookies => "cookies",
                    _ => null,
                },
                Actions = r.Actions is { Count: > 0 } ? [.. r.Actions] : null,
                Tools = r.Tools is { Count: > 0 }
                    ? r.Tools.Select(t => new RecipeToolDto
                    {
                        Name = t.Name,
                        Method = t.Method,
                        Path = t.Path,
                        Action = t.Action,
                        StepUp = t.StepUp,
                        Description = t.Description,
                        // Persist only an explicit plane override so a derived plane
                        // round-trips back to null (faithful document, ADR 0008).
                        Plane = t.Plane is { } p ? ActionPlanes.ToToken(p) : null,
                    }).ToList()
                    : null,
                ExtraHeaders = r.ExtraHeaders is { Count: > 0 } ? new Dictionary<string, string>(r.ExtraHeaders) : null,
                CookieMap = r.CookieMap is { Count: > 0 } ? new Dictionary<string, string>(r.CookieMap) : null,
                Description = r.Description,
                Rotation = r.Rotation is null ? null : new RecipeRotationDto { Owner = r.Rotation.Owner, Detail = r.Rotation.Detail },
            })
            .ToList(),
    };
}
