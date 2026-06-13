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
}

internal sealed class RecipeToolDto
{
    public string Name { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "";
    public string Action { get; init; } = "";
    public bool StepUp { get; init; }
    public string? Description { get; init; }
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
            .Select(b => new TargetBinding(b.Target, b.Credential, b.OnBehalfOf))
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
                    .Select(t => new RecipeTool(t.Name, t.Method, t.Path, t.Action, t.StepUp, t.Description))
                    .ToArray(),
                ExtraHeaders: r.ExtraHeaders,
                CookieMap: r.CookieMap,
                Description: r.Description))
            .ToArray();

        return new LoadedPolicy(grants, bindings, recipes);
    }

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
}
