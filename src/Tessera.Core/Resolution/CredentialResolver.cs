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
    public TargetBinding? BindingFor(AccessRequest request)
    {
        foreach (var binding in _bindings)
        {
            if (binding.Matches(request))
            {
                return binding;
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
