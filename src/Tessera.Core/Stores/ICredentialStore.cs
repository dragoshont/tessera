namespace Tessera.Core.Stores;

/// <summary>Raised when a credential store cannot be reached or read.</summary>
public sealed class StoreException : Exception
{
    /// <summary>Creates a store exception.</summary>
    public StoreException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Read-only access to credential bundles by name. The single, narrow store
/// abstraction (ADR 0003); concrete stores (Azure Key Vault, Vault, file) live
/// outside the core.
/// </summary>
public interface ICredentialStore
{
    /// <summary>A short, secret-free description of the backing store (for <c>/status</c>).</summary>
    string Kind { get; }

    /// <summary>
    /// Returns the bundle stored under <paramref name="name"/>, or
    /// <see cref="CredentialBundle.Empty"/> if missing/empty.
    /// </summary>
    /// <exception cref="StoreException">The store could not be read.</exception>
    Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default);
}
