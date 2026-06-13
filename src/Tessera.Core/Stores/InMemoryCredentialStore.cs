namespace Tessera.Core.Stores;

/// <summary>A dictionary-backed store for tests and offline dev. No network.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, CredentialBundle> _bundles;

    /// <summary>Creates an empty in-memory store.</summary>
    public InMemoryCredentialStore() => _bundles = new(StringComparer.Ordinal);

    /// <summary>Creates an in-memory store seeded with bundles.</summary>
    public InMemoryCredentialStore(IDictionary<string, CredentialBundle> bundles) =>
        _bundles = new(bundles, StringComparer.Ordinal);

    /// <inheritdoc/>
    public string Kind => "in-memory (no Azure env; resolution will be 'absent')";

    /// <summary>Adds or replaces a bundle.</summary>
    public void Put(string name, CredentialBundle bundle) => _bundles[name] = bundle;

    /// <inheritdoc/>
    public Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bundles.TryGetValue(name, out var bundle) ? bundle : CredentialBundle.Empty);
}
