using Tessera.Core.Configuration;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;

namespace Tessera.Core.Portal;

/// <summary>
/// The admin portal's read-model: it projects <em>people</em> and their
/// <em>connections</em> from what already exists — the loaded policy (grants +
/// bindings + recipes), the admins allow-list, and the credential store's
/// secret-free status — with <b>no database</b> (ADR 0016 / spec §7b).
///
/// <para>It returns only metadata + presence flags, never a secret value. The
/// store touch is read-only (a status assessment), so listing connections never
/// makes an upstream call and never exposes bytes.</para>
/// </summary>
public sealed class PortalService
{
    private readonly LoadedPolicy _policy;
    private readonly CredentialResolver _resolver;
    private readonly IReadOnlyList<string> _admins;

    /// <summary>Creates the portal read-model over the policy, resolver, and admins allow-list.</summary>
    /// <param name="policy">The loaded grants + bindings + recipes (the source of truth).</param>
    /// <param name="resolver">The credential resolver (read-only status, no bytes).</param>
    /// <param name="admins">The portal admins allow-list (config, not a DB).</param>
    public PortalService(LoadedPolicy policy, CredentialResolver resolver, IReadOnlyCollection<string> admins)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(admins);
        _policy = policy;
        _resolver = resolver;
        _admins = admins.ToArray();
    }

    /// <summary>True when <paramref name="principal"/> is in the admins allow-list (case-insensitive).</summary>
    public bool IsAdmin(string? principal) =>
        !string.IsNullOrWhiteSpace(principal)
        && _admins.Contains(principal, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lists every person the portal knows about with an attention rollup — the
    /// Users view (admin surface). Resolves each connection's status once.
    /// </summary>
    public async Task<IReadOnlyList<PersonView>> ListPeopleAsync(CancellationToken cancellationToken = default)
    {
        var people = PortalPeople.Project(_policy, _admins);
        var views = new List<PersonView>(people.Count);
        foreach (var person in people)
        {
            var connections = await ListConnectionsAsync(person.Principal, cancellationToken).ConfigureAwait(false);
            var needsAttention = connections.Count(c => c.Status is not "live");
            views.Add(new PersonView(person.Principal, person.Role, connections.Count, needsAttention));
        }

        return views;
    }

    /// <summary>
    /// Lists the connections that act on behalf of <paramref name="principal"/>,
    /// each with a health badge derived from the store's secret-free status.
    /// </summary>
    public async Task<IReadOnlyList<PortalConnection>> ListConnectionsAsync(string principal, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);

        var connections = new List<PortalConnection>();
        foreach (var binding in _policy.Bindings)
        {
            if (binding.Principal is null
                || !string.Equals(binding.Principal, principal, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var recipe = FindRecipe(binding.Target);
            var health = await _resolver.AssessBindingAsync(binding, cancellationToken).ConfigureAwait(false);
            connections.Add(new PortalConnection(
                ConnectionId: $"{binding.Target}:{binding.Principal}",
                OwnerPrincipal: binding.Principal,
                Provider: binding.Target,
                DisplayName: recipe?.Description ?? binding.Target,
                Status: MapStatus(health.Status),
                HasCookies: health.HasCookies,
                HasRefreshToken: health.HasRefreshToken,
                HasAccessToken: health.HasAccessToken,
                // A harvested session bundle carries no readable expiry today, so we
                // are honest: expiry is unknown/estimated rather than a fake date.
                ExpiresAt: null,
                ExpiryIsEstimated: true,
                Detail: health.Detail));
        }

        // Stable order: needs-attention first, then by provider name.
        return connections
            .OrderByDescending(c => c.Status is not "live")
            .ThenBy(c => c.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Recipe? FindRecipe(string target)
    {
        foreach (var recipe in _policy.Recipes)
        {
            if (string.Equals(recipe.Target, target, StringComparison.Ordinal))
            {
                return recipe;
            }
        }

        return null;
    }

    // The store status maps to the UI health vocabulary. expiring_soon / seeding /
    // needs_human are live-flow states not derivable from a static bundle yet, so
    // they are intentionally not emitted here (honest — see spec R2).
    private static string MapStatus(CredentialStatus status) => status switch
    {
        CredentialStatus.Present => "live",
        CredentialStatus.Absent => "absent",
        CredentialStatus.Incomplete => "error",
        _ => "error",
    };
}
