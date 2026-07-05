using Tessera.Core.OAuthMcp;

namespace Tessera.Providers.OAuthMcp;

/// <summary>
/// A pending authorization-code exchange — everything the callback needs to redeem a
/// <c>code</c> for a given <c>state</c>, minus the code itself. It holds the PKCE
/// verifier (kept server-side, never on the front channel) and the exact
/// <c>redirect_uri</c>/<c>client_id</c>/token endpoint/resource the exchange must echo.
/// </summary>
/// <param name="Principal">The person the acquired credential is for (the per-principal owner).</param>
/// <param name="Target">The recipe target the connection is against.</param>
/// <param name="SecretName">The per-principal store key the issued bundle is written to.</param>
/// <param name="TokenEndpoint">The RFC 8414 token endpoint the code is redeemed at.</param>
/// <param name="RedirectUri">The exact redirect URI the authorize request used (echoed on redemption).</param>
/// <param name="ClientId">The OAuth client id the authorize request used.</param>
/// <param name="Resource">The RFC 8707 resource the token is bound to.</param>
/// <param name="Verifier">The PKCE <c>code_verifier</c> (sent only on the back-channel token call).</param>
/// <param name="ExpiresAt">When this pending exchange is no longer redeemable.</param>
public sealed record PendingAuthorization(
    string Principal,
    string Target,
    string SecretName,
    Uri TokenEndpoint,
    Uri RedirectUri,
    string ClientId,
    string Resource,
    string Verifier,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Holds the in-flight authorization-code exchanges keyed by their opaque <c>state</c>.
/// An entry is redeemable exactly once (single-use) and only before it expires — the
/// state is the CSRF/anti-forgery binding between the authorize redirect and the callback.
/// </summary>
public interface IPendingAuthorizationStore
{
    /// <summary>Stash a pending exchange under its <paramref name="state"/>.</summary>
    void Put(string state, PendingAuthorization pending);

    /// <summary>
    /// Remove and return the pending exchange for <paramref name="state"/> — single-use:
    /// a second call for the same state returns null. Returns null when the state is
    /// unknown or has expired at <paramref name="now"/>.
    /// </summary>
    PendingAuthorization? Take(string state, DateTimeOffset now);
}

/// <summary>
/// A bounded, single-use, in-memory <see cref="IPendingAuthorizationStore"/>. State
/// entries are short-lived (an authorize round-trip is seconds to minutes), so a bound
/// with soonest-to-expire eviction is safe: a dropped pending merely forces the user to
/// restart the connect. Volatile by design — a restart drops in-flight authorizations,
/// never an acquired credential (those are in the store).
/// </summary>
public sealed class InMemoryPendingAuthorizationStore : IPendingAuthorizationStore
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingAuthorization> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates the store bounded to <paramref name="capacity"/> concurrent pending exchanges.</summary>
    public InMemoryPendingAuthorizationStore(int capacity = 256) => _capacity = Math.Max(1, capacity);

    /// <inheritdoc/>
    public void Put(string state, PendingAuthorization pending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(pending);

        lock (_gate)
        {
            if (_entries.Count >= _capacity && !_entries.ContainsKey(state))
            {
                // Evict the soonest-to-expire — the least valuable in-flight authorization.
                var evict = _entries.MinBy(kv => kv.Value.ExpiresAt).Key;
                _entries.Remove(evict);
            }

            _entries[state] = pending;
        }
    }

    /// <inheritdoc/>
    public PendingAuthorization? Take(string state, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_entries.Remove(state, out var pending))
            {
                return null; // unknown or already consumed (single-use)
            }

            return pending.ExpiresAt < now ? null : pending; // expired ⇒ gone
        }
    }
}
