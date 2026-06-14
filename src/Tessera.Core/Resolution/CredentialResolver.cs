using Tessera.Core.Model;
using Tessera.Core.Stores;

namespace Tessera.Core.Resolution;

/// <summary>
/// Resolves a request to a <see cref="ResolvedCredential"/> using bindings + a
/// store. The secret bytes stay inside the resolver — never returned, logged, or
/// audited (the secretless contract, ADR 0003).
/// </summary>
public sealed class CredentialResolver
{
    private readonly IReadOnlyList<TargetBinding> _bindings;
    private readonly ICredentialStore _store;

    /// <summary>Creates a resolver over a set of bindings and a store.</summary>
    public CredentialResolver(IEnumerable<TargetBinding> bindings, ICredentialStore store)
    {
        _bindings = bindings.ToArray();
        _store = store;
    }

    /// <summary>Finds the binding that backs <paramref name="request"/>, if any.</summary>
    /// <remarks>
    /// Exact match wins: a person's own per-principal binding (including an
    /// <c>owner: user</c>/<c>dependent</c> one) backs their delegated request, and an
    /// automation binding backs an automation (no-human) request. Only when no exact
    /// binding matches a <em>delegated</em> request does it fall back to a shared
    /// <b>service-owned</b> key (ADR 0020): a <c>principal = null, owner = service</c>
    /// binding for the same target — the brokered authority nobody personally holds.
    /// The grant has already authorized this exact user (the PDP runs first); the
    /// binding only supplies the key. A non-service principal-null binding is never a
    /// fallback, and a request with no human never reaches the fallback.
    /// </remarks>
    public TargetBinding? BindingFor(AccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) Exact match — per-principal (the person's own key) or automation.
        foreach (var binding in _bindings)
        {
            if (binding.Matches(request))
            {
                return binding;
            }
        }

        // 2) Service-owned fallback for a delegated request with no per-person key.
        if (request.OnBehalfOf is not null)
        {
            foreach (var binding in _bindings)
            {
                if (binding.Principal is null
                    && binding.Owner == CredentialOwner.Service
                    && string.Equals(binding.Target, request.Target, StringComparison.Ordinal))
                {
                    return binding;
                }
            }
        }

        return null;
    }

    /// <summary>Resolves a request to a status — never the secret bytes.</summary>
    public async Task<ResolvedCredential> ResolveAsync(AccessRequest request, CancellationToken cancellationToken = default)
    {
        var binding = BindingFor(request);
        if (binding is null)
        {
            return new ResolvedCredential(request.Target, CredentialStatus.Absent, "no target binding");
        }

        CredentialBundle bundle;
        try
        {
            bundle = await _store.GetBundleAsync(binding.Credential, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException exc)
        {
            return new ResolvedCredential(request.Target, CredentialStatus.Error, exc.Message);
        }

        var (status, detail) = Assess(bundle);
        return new ResolvedCredential(request.Target, status, detail);
    }

    /// <summary>
    /// Resolves a request to the actual bundle for INTERNAL egress use — the bytes
    /// are injected into the upstream call and never returned to a caller, logged,
    /// or audited. Returns <c>null</c> when there is no binding or the bundle is
    /// empty/unusable.
    /// </summary>
    public async Task<CredentialBundle?> ResolveBundleAsync(AccessRequest request, CancellationToken cancellationToken = default)
    {
        var binding = BindingFor(request);
        if (binding is null)
        {
            return null;
        }

        CredentialBundle bundle;
        try
        {
            bundle = await _store.GetBundleAsync(binding.Credential, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException)
        {
            return null;
        }

        return bundle.IsEmpty ? null : bundle;
    }

    /// <summary>
    /// Assesses a binding's stored bundle for the admin portal: its
    /// <see cref="CredentialStatus"/> plus the non-secret <em>presence</em> flags
    /// (which kinds of material exist — never the values). This is the data behind
    /// a connection's health badge and the "has cookies ✓ / has refresh token ✓"
    /// drawer line; it returns no secret bytes (ADR 0016 / the secretless contract).
    /// </summary>
    public async Task<BindingHealth> AssessBindingAsync(TargetBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        CredentialBundle bundle;
        try
        {
            bundle = await _store.GetBundleAsync(binding.Credential, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException exc)
        {
            return new BindingHealth(CredentialStatus.Error, false, false, false, exc.Message);
        }

        var (status, detail) = Assess(bundle);
        return new BindingHealth(
            status,
            bundle.HasAccessToken,
            bundle.HasRefreshToken,
            bundle.HasCookies,
            detail);
    }

    /// <summary>Classifies a bundle into a status + secret-free detail.</summary>
    internal static (CredentialStatus Status, string Detail) Assess(CredentialBundle bundle)
    {
        if (bundle.IsEmpty)
        {
            return (CredentialStatus.Absent, "no bundle in store");
        }

        // Report *that* material is present, never *what* it is.
        var kinds = new List<string>(3);
        if (bundle.HasAccessToken)
        {
            kinds.Add("access_token");
        }

        if (bundle.HasRefreshToken)
        {
            kinds.Add("refresh_token");
        }

        if (bundle.HasCookies)
        {
            kinds.Add("cookies");
        }

        return kinds.Count > 0
            ? (CredentialStatus.Present, "has " + string.Join(", ", kinds))
            : (CredentialStatus.Incomplete, "bundle present but no tokens/cookies");
    }
}
