using Tessera.Core.Configuration;
using Tessera.Core.Recipes;
using Tessera.Core.Stores;

namespace Tessera.Providers;

/// <summary>A secret-free rollup of one refresh pass (counts only, never identifiers).</summary>
/// <param name="Considered">Bindings whose recipe is Tessera-owned + refresh-declaring.</param>
/// <param name="Rotated">Sessions rotated + written back.</param>
/// <param name="Dead">Refresh tokens reported dead (interactive re-login needed; never auto-logged-in).</param>
/// <param name="Errors">Transport/store failures.</param>
/// <param name="Skipped">Bindings with no current bundle to rotate (absent).</param>
public sealed record RefreshPassSummary(int Considered, int Rotated, int Dead, int Errors, int Skipped);

/// <summary>
/// The Mode U rotation owner (ADR 0015): one pass over the policy that rotates every
/// session whose recipe <b>both</b> declares <c>rotation.owner = tessera</c> <b>and</b>
/// carries a <see cref="RefreshSpec"/>. It is the *sole owner* path — it must run only
/// after Tessera has taken over rotation for that provider (the cutover, plan §2.4);
/// rotating a single-use session another component still owns would corrupt it, which
/// is why the gate is the recipe's own <c>rotation.owner</c> (an operator declaration),
/// not a blanket switch.
///
/// <para>It is pure and fully testable: read the current bundle, ask
/// <see cref="SessionRefresher"/> to rotate + write it back, tally the outcome. A dead
/// refresh token is <em>reported</em>, never auto-relogged-in (consent-gated). Every
/// result is secret-free (counts only). The host decides <em>whether</em> to run it
/// (gated by <c>refresh.enabled</c> + <c>egress.enabled</c>, both off by default).</para>
/// </summary>
public sealed class SessionRefreshOrchestrator
{
    private readonly Func<LoadedPolicy> _policy;
    private readonly ICredentialStore _store;
    private readonly SessionRefresher _refresher;

    /// <summary>Creates the orchestrator over a live policy source, the store (read), and the refresher (write).</summary>
    /// <param name="policy">
    /// A source of the <em>current</em> policy, read fresh on every pass — so a
    /// connection added through the portal after startup is picked up without a
    /// restart (no stale snapshot).
    /// </param>
    /// <param name="store">The credential store (read).</param>
    /// <param name="refresher">The session refresher (write-back).</param>
    public SessionRefreshOrchestrator(Func<LoadedPolicy> policy, ICredentialStore store, SessionRefresher refresher)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(refresher);
        _policy = policy;
        _store = store;
        _refresher = refresher;
    }

    /// <summary>Convenience overload over a fixed policy snapshot (tests / static policies).</summary>
    public SessionRefreshOrchestrator(LoadedPolicy policy, ICredentialStore store, SessionRefresher refresher)
        : this(() => policy, store, refresher)
    {
        ArgumentNullException.ThrowIfNull(policy);
    }

    /// <summary>True when at least one recipe is a Tessera-owned, refresh-declaring rotation target.</summary>
    public bool HasOwnedRotation => _policy().Recipes.Any(IsTesseraOwned);

    /// <summary>Runs a single rotation pass over every Tessera-owned, refresh-declaring binding.</summary>
    public async Task<RefreshPassSummary> RunPassAsync(CancellationToken cancellationToken = default)
    {
        var policy = _policy();
        var considered = 0;
        var rotated = 0;
        var dead = 0;
        var errors = 0;
        var skipped = 0;

        foreach (var recipe in policy.Recipes)
        {
            if (!IsTesseraOwned(recipe))
            {
                continue;
            }

            foreach (var binding in policy.Bindings)
            {
                if (!string.Equals(binding.Target, recipe.Target, StringComparison.Ordinal))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                considered++;

                CredentialBundle current;
                try
                {
                    current = await _store.GetBundleAsync(binding.Credential, cancellationToken).ConfigureAwait(false);
                }
                catch (StoreException)
                {
                    errors++;
                    continue;
                }

                if (current.IsEmpty)
                {
                    // Nothing seeded yet — there is no session to keep warm.
                    skipped++;
                    continue;
                }

                var result = await _refresher
                    .RefreshAsync(recipe, recipe.Refresh, binding.Credential, current, cancellationToken)
                    .ConfigureAwait(false);

                switch (result.Status)
                {
                    case RefreshStatus.Rotated: rotated++; break;
                    case RefreshStatus.Dead: dead++; break;
                    case RefreshStatus.NotConfigured: skipped++; break;
                    default: errors++; break;
                }
            }
        }

        return new RefreshPassSummary(considered, rotated, dead, errors, skipped);
    }

    /// <summary>A recipe Tessera rotates: it both declares ownership and carries a refresh spec.</summary>
    private static bool IsTesseraOwned(Recipe recipe) =>
        recipe.Refresh is not null
        && string.Equals(recipe.Rotation?.Owner, "tessera", StringComparison.OrdinalIgnoreCase);
}
