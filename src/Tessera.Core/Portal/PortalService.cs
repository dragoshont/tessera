using Tessera.Core.Configuration;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Results;

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
    // The consent ledger (ADR 0020): a receipt is appended when a person seeds a
    // user/dependent-owned connection (the consent act). It is per-(principal,
    // target, data class) so calendar consent never satisfies mail. In-memory +
    // bounded like the audit tail — durable consent is the binding itself; this is
    // the "what/when did I consent" surface, lost on restart, never faked.
    private readonly List<ConsentReceipt> _consents = [];
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

    /// <summary>
    /// The current policy snapshot (the same one the read-model serves), exposed so a
    /// background consumer like the Mode U refresher reads the <em>live</em> policy
    /// after a portal add-connection — not a stale copy captured at startup.
    /// </summary>
    public LoadedPolicy CurrentPolicy => Policy;

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
    /// The dependents <paramref name="guardian"/> may act as (ADR 0020) — derived
    /// from the live policy's <c>owner: dependent</c> bindings whose <c>guardian</c>
    /// is this person. Answers "whose accounts do I manage?" Read-only, secret-free;
    /// it confers no authority on its own (the PDP grants still gate every action),
    /// it only surfaces the relationship the bindings already encode.
    /// </summary>
    public IReadOnlyList<string> ListDependents(string guardian) =>
        new GuardianRelationships(Policy.Bindings).DependentsOf(guardian);

    /// <summary>True when <paramref name="guardian"/> may act as <paramref name="dependent"/> (a seeded dependent binding exists).</summary>
    public bool MayActAs(string guardian, string dependent) =>
        new GuardianRelationships(Policy.Bindings).MayActAs(guardian, dependent);

    /// <summary>
    /// The consent receipts recorded for <paramref name="principal"/> (ADR 0020) —
    /// "what data classes did I consent to, and when?". Captured at the moment a
    /// person seeds a user/dependent-owned connection; newest-first. Bounded +
    /// in-memory (lost on restart, never faked); the durable consent is the binding.
    /// </summary>
    public IReadOnlyList<ConsentReceipt> ListConsents(string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        lock (_gate)
        {
            return _consents
                .Where(c => string.Equals(c.Principal, principal, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.GrantedAt)
                .ToArray();
        }
    }

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
                Planes: ActionPlanes.TokensOf(grant.Actions),
                IsAutomation: grant.OnBehalfOf is null,
                OnBehalfOf: grant.OnBehalfOf,
                Owner: OwnerForDelegation(policy, grant.Target, grant.OnBehalfOf)));
        }

        // Stable order: by target, then by caller — independent of policy-file order.
        return delegations
            .OrderBy(d => d.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Caller, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Lists the modules (connectors) loaded into the broker (ADR 0017) — a projection
    /// of the recipes plus the egress posture and a per-module connection count. It
    /// answers "what's loaded, and what can it do?" Secret-free: it surfaces the
    /// upstream <em>host</em> only (never a path or credential) and a count (never the
    /// owners). <paramref name="egressGloballyEnabled"/> is the broker's
    /// <c>egress.enabled</c> gate: a module's <see cref="ModuleView.EgressEnabled"/>
    /// is true only when the recipe is HTTP <b>and</b> that global gate is on — i.e.
    /// it can actually reach upstream right now (otherwise it is status-only).
    /// </summary>
    /// <param name="egressGloballyEnabled">The broker's global egress gate (config.Egress.Enabled).</param>
    public IReadOnlyList<ModuleView> ListModules(bool egressGloballyEnabled)
    {
        var policy = Policy;

        // Count connections (bindings) per target so each module shows its usage.
        var connectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var binding in policy.Bindings)
        {
            connectionCounts[binding.Target] = connectionCounts.GetValueOrDefault(binding.Target) + 1;
        }

        var modules = new List<ModuleView>(policy.Recipes.Count);
        foreach (var recipe in policy.Recipes)
        {
            var egressCapable = recipe.Egress is EgressMode.Http or EgressMode.Proxy;
            connectionCounts.TryGetValue(recipe.Target, out var count);
            modules.Add(new ModuleView(
                Target: recipe.Target,
                DisplayName: recipe.Description ?? recipe.Target,
                Driver: recipe.Driver,
                Egress: recipe.Egress switch { EgressMode.Http => "http", EgressMode.Proxy => "proxy", _ => "none" },
                EgressEnabled: egressCapable && egressGloballyEnabled,
                Actions: recipe.ExposedActions,
                // Planes span the declared action verbs and every tool's action, so a
                // module that exposes a manage: tool shows the manage chip (ADR 0019).
                Planes: ActionPlanes.TokensOf(recipe.ExposedActions.Concat(recipe.ExposedTools.Select(t => t.Action))),
                ToolCount: recipe.ExposedTools.Count,
                ConnectionCount: count,
                UpstreamHost: ExtractHost(recipe.UpstreamBaseUrl)));
        }

        return modules
            .OrderBy(m => m.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Extracts the host from an upstream base URL (host only, never a path/secret); null when absent/invalid.</summary>
    private static string? ExtractHost(string? upstreamBaseUrl) =>
        Uri.TryCreate(upstreamBaseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// Returns the rotation schedule of one connection (ADR 0017) — "is an automatic
    /// job keeping this session warm, and who owns it?" Honest about ownership:
    /// <c>none</c> (static, re-seed by hand), <c>external</c> (a domain component
    /// keeps it warm — today's Mode P), or <c>tessera</c> (Tessera's refresher owns
    /// it — Mode U). The last/next-run timestamps stay null until Tessera itself owns
    /// and tracks rotation (never faked). Returns null when no such connection exists
    /// (the endpoint maps that to 404).
    /// </summary>
    /// <param name="connectionId">The <c>{target}:{principal}</c> connection id.</param>
    public ScheduleView? GetSchedule(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var (target, principal) = SplitConnectionId(connectionId);
        if (target is null || principal is null)
        {
            return null;
        }

        var policy = Policy;
        var hasConnection = policy.Bindings.Any(b =>
            SameTarget(b.Target, target) && SamePrincipal(b.Principal, principal));
        if (!hasConnection)
        {
            return null;
        }

        var recipe = FindRecipe(policy, target);
        var owner = NormalizeRotationOwner(recipe?.Rotation?.Owner);
        var detail = !string.IsNullOrWhiteSpace(recipe?.Rotation?.Detail)
            ? recipe!.Rotation!.Detail!
            : DefaultRotationDetail(owner);

        return new ScheduleView(
            ConnectionId: connectionId,
            RotationOwner: owner,
            RefreshConfigured: owner != "none",
            Detail: detail,
            // Tessera tracks these only once its own refresher owns rotation (Mode U,
            // ADR 0015). Until then they are honestly unknown — never a fabricated date.
            LastRotatedAt: null,
            NextRotationAt: null);
    }

    /// <summary>Splits a <c>{target}:{principal}</c> id on the first colon (a principal may itself contain none).</summary>
    private static (string? Target, string? Principal) SplitConnectionId(string connectionId)
    {
        var idx = connectionId.IndexOf(':', StringComparison.Ordinal);
        if (idx <= 0 || idx >= connectionId.Length - 1)
        {
            return (null, null);
        }

        return (connectionId[..idx], connectionId[(idx + 1)..]);
    }

    /// <summary>Normalizes a declared rotation owner to the known vocabulary; anything else ⇒ <c>none</c>.</summary>
    private static string NormalizeRotationOwner(string? owner) => owner?.Trim().ToLowerInvariant() switch
    {
        "external" => "external",
        "tessera" => "tessera",
        _ => "none",
    };

    private static string DefaultRotationDetail(string owner) => owner switch
    {
        "external" => "Rotation is owned by an external component (e.g. a domain MCP keep-warm); Tessera does not schedule it.",
        "tessera" => "Tessera owns rotation; the last and next run appear once its refresher has executed.",
        _ => "No automatic rotation — this session is static and is re-seeded by hand.",
    };

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
                Detail: health.Detail,
                Owner: CredentialOwners.ToToken(binding.Owner),
                Guardian: binding.Guardian));
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
    /// <param name="owner">
    /// Whose credential this is (ADR 0020). Defaults to <see cref="CredentialOwner.User"/>:
    /// the connect wizard seeds a <em>person's own</em> login, so a portal-added
    /// connection is user-owned (not the service-owned fail-safe default a hand-
    /// authored shared key uses).
    /// </param>
    /// <param name="guardian">For an <see cref="CredentialOwner.Dependent"/> connection, the guardian who seeds it.</param>
    public async Task<PortalConnection> AddConnectionAsync(
        string provider,
        string principal,
        string credential,
        CredentialOwner owner = CredentialOwner.User,
        string? guardian = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        var binding = new TargetBinding(provider, credential, principal, owner, guardian);
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

        // Record the consent act: seeding a person's own (or a dependent's) login is
        // the consent. One receipt per (principal, target) — the data class is the
        // provider target, so two providers (e.g. calendar vs mail) get separate
        // receipts (calendar consent never satisfies mail), and Covers() can match
        // it. The provider's exposed action verbs ride along as Scopes (informational).
        if (owner is CredentialOwner.User or CredentialOwner.Dependent)
        {
            var recipe = FindRecipe(updated, provider);
            var scopes = recipe?.ExposedActions is { Count: > 0 } acts ? acts.ToArray() : null;
            var receipt = new ConsentReceipt(principal, provider, provider, owner, DateTimeOffset.UtcNow, guardian, scopes);
            lock (_gate)
            {
                // Replace any prior consent for the same (principal, target).
                _consents.RemoveAll(c => c.Covers(principal, provider, provider));
                _consents.Add(receipt);
            }
        }

        var resolvedRecipe = FindRecipe(updated, provider);
        var health = await _resolver.AssessBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        return new PortalConnection(
            ConnectionId: $"{provider}:{principal}",
            OwnerPrincipal: principal,
            Provider: provider,
            DisplayName: resolvedRecipe?.Description ?? provider,
            Status: MapStatus(health.Status),
            HasCookies: health.HasCookies,
            HasRefreshToken: health.HasRefreshToken,
            HasAccessToken: health.HasAccessToken,
            ExpiresAt: null,
            ExpiryIsEstimated: true,
            Detail: health.Detail,
            Owner: CredentialOwners.ToToken(binding.Owner),
            Guardian: binding.Guardian);
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

    /// <summary>
    /// The owner token of the credential that would back a delegation of
    /// <paramref name="target"/> to <paramref name="principal"/> — mirroring the
    /// resolver's exact-then-service-fallback (ADR 0020), so the "who/what may act as
    /// me" view is honest even when a <b>shared service key</b> stands in for the
    /// person (no per-person binding). Returns null for pure automation or when no
    /// binding backs it (the grant is then inert until a binding exists).
    /// </summary>
    private static string? OwnerForDelegation(LoadedPolicy policy, string target, string? principal)
    {
        if (principal is null)
        {
            return null; // automation — no per-person credential ownership to show
        }

        // Exact per-person binding wins.
        foreach (var binding in policy.Bindings)
        {
            if (SameTarget(binding.Target, target) && SamePrincipal(binding.Principal, principal))
            {
                return CredentialOwners.ToToken(binding.Owner);
            }
        }

        // Else a shared service-owned key for the same target backs it (the fallback).
        foreach (var binding in policy.Bindings)
        {
            if (binding.Principal is null
                && binding.Owner == CredentialOwner.Service
                && SameTarget(binding.Target, target))
            {
                return CredentialOwners.ToToken(CredentialOwner.Service);
            }
        }

        return null; // no binding backs this grant yet
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
