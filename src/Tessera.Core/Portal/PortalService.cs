using Tessera.Core.Configuration;
using Tessera.Core.Policy;
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
///
/// <para>Adding a connection (the connect wizard) appends a binding to the policy
/// in memory and persists it back to the policy document (the files stay the
/// source of truth — ADR 0008 / spec R3). On a read-only mount (the GitOps case)
/// persistence is skipped and the add applies for the running process only.</para>
/// </summary>
public sealed class PortalService
{
    private readonly object _gate = new();
    private readonly CredentialResolver _resolver;
    private readonly IReadOnlyList<string> _admins;
    private readonly Action<LoadedPolicy>? _persist;
    // The caller id recorded on a portal-authored grant. A binding alone makes a
    // person + connection appear (the read-model keys on bindings); a grant is what
    // authorizes a *consumer* to use it, so portal-added grants name this principal
    // and stay deny-by-default for everything else.
    private const string PortalCaller = "portal://tessera";
    private LoadedPolicy _policy;

    /// <summary>Creates the portal read-model over the policy, resolver, and admins allow-list.</summary>
    /// <param name="policy">The loaded grants + bindings + recipes (the source of truth).</param>
    /// <param name="resolver">The credential resolver (read-only status, no bytes).</param>
    /// <param name="admins">The portal admins allow-list (config, not a DB).</param>
    /// <param name="persist">Optional writer to persist the policy after a mutation (null = in-memory only).</param>
    public PortalService(
        LoadedPolicy policy,
        CredentialResolver resolver,
        IReadOnlyCollection<string> admins,
        Action<LoadedPolicy>? persist = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(admins);
        _policy = policy;
        _resolver = resolver;
        _admins = admins.ToArray();
        _persist = persist;
    }

    /// <summary>The current policy snapshot (swapped atomically on a mutation).</summary>
    private LoadedPolicy Policy
    {
        get { lock (_gate) { return _policy; } }
    }

    /// <summary>True when <paramref name="principal"/> is in the admins allow-list (case-insensitive).</summary>
    public bool IsAdmin(string? principal) =>
        !string.IsNullOrWhiteSpace(principal)
        && _admins.Contains(principal, StringComparer.OrdinalIgnoreCase);

    /// <summary>The recipe targets a connection can be created against (the wizard's provider picker).</summary>
    public IReadOnlyList<RecipeSummary> ListRecipes() =>
        Policy.Recipes
            .Select(r => new RecipeSummary(r.Target, r.Description ?? r.Target))
            .OrderBy(r => r.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Lists the delegations the awareness dashboard shows (ADR 0017) — a projection
    /// of the <em>enforced</em> grants (the same in-memory policy the PDP decides on,
    /// so it can never diverge from what is actually enforced). When
    /// <paramref name="principal"/> is non-null, only grants that delegate to that
    /// exact person are returned ("who/what may act as me"); when null, every grant is
    /// returned including pure automation (the operator's "who is using auth" view).
    /// Pure projection, no I/O, secret-free.
    /// </summary>
    /// <param name="principal">The delegated person to scope to, or null for all grants (operator).</param>
    public IReadOnlyList<DelegationView> ListDelegations(string? principal)
    {
        var policy = Policy;
        var delegations = new List<DelegationView>();
        foreach (var grant in policy.Grants)
        {
            if (principal is not null
                && (grant.OnBehalfOf is null
                    || !string.Equals(grant.OnBehalfOf, principal, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var recipe = FindRecipe(policy, grant.Target);
            delegations.Add(new DelegationView(
                Caller: grant.Caller,
                Target: grant.Target,
                DisplayName: recipe?.Description ?? grant.Target,
                Actions: grant.Actions,
                StepUpActions: grant.StepUpActions ?? [],
                IsAutomation: grant.OnBehalfOf is null,
                OnBehalfOf: grant.OnBehalfOf));
        }

        // Stable order: by target, then by caller — independent of policy-file order.
        return delegations
            .OrderBy(d => d.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Caller, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Lists every person the portal knows about with an attention rollup — the
    /// Users view (admin surface). Resolves each connection's status once.
    /// </summary>
    public async Task<IReadOnlyList<PersonView>> ListPeopleAsync(CancellationToken cancellationToken = default)
    {
        var policy = Policy;
        var people = PortalPeople.Project(policy, _admins);
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

        var policy = Policy;
        var connections = new List<PortalConnection>();
        foreach (var binding in policy.Bindings)
        {
            if (binding.Principal is null
                || !string.Equals(binding.Principal, principal, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var recipe = FindRecipe(policy, binding.Target);
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

    /// <summary>
    /// Adds (or re-points) a connection: writes a binding <c>(provider, principal) →
    /// credential</c> into the policy so the person + connection appear, then
    /// persists the document (best-effort — skipped on a read-only mount). This is
    /// the connect wizard's write. It records a <em>binding</em> only; authorizing a
    /// consumer to use it is a separate grant step, so a new connection is visible
    /// but stays deny-by-default until explicitly granted (spec R3 / ADR 0008).
    /// Returns the new connection row (with its freshly-assessed health).
    /// </summary>
    /// <param name="provider">The recipe target (e.g. <c>health-portal</c>).</param>
    /// <param name="principal">The person the connection acts for.</param>
    /// <param name="credential">The store secret name holding the session bundle.</param>
    public async Task<PortalConnection> AddConnectionAsync(
        string provider,
        string principal,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        var binding = new TargetBinding(provider, credential, principal);
        LoadedPolicy updated;
        lock (_gate)
        {
            // Replace any existing binding for this (provider, principal) pair so a
            // re-add re-points the credential instead of duplicating the row.
            var bindings = _policy.Bindings
                .Where(b => !(SameTarget(b.Target, provider) && SamePrincipal(b.Principal, principal)))
                .Append(binding)
                .ToArray();
            updated = new LoadedPolicy(_policy.Grants, bindings, _policy.Recipes);
            _policy = updated;
        }

        // Persist outside the lock; a read-only mount (GitOps) is not an error —
        // the in-memory add still applies for this process.
        try
        {
            _persist?.Invoke(updated);
        }
        catch (IOException)
        {
            // read-only policy document (ConfigMap / GitOps) — in-memory only.
        }
        catch (UnauthorizedAccessException)
        {
            // read-only policy document — in-memory only.
        }

        var recipe = FindRecipe(updated, provider);
        var health = await _resolver.AssessBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        return new PortalConnection(
            ConnectionId: $"{provider}:{principal}",
            OwnerPrincipal: principal,
            Provider: provider,
            DisplayName: recipe?.Description ?? provider,
            Status: MapStatus(health.Status),
            HasCookies: health.HasCookies,
            HasRefreshToken: health.HasRefreshToken,
            HasAccessToken: health.HasAccessToken,
            ExpiresAt: null,
            ExpiryIsEstimated: true,
            Detail: health.Detail);
    }

    private static bool SameTarget(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static bool SamePrincipal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Recipe? FindRecipe(LoadedPolicy policy, string target)
    {
        foreach (var recipe in policy.Recipes)
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
