using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Tessera.Core.Stores;

namespace Tessera.Stores.AzureKeyVault;

/// <summary>
/// The default credential store: read credential bundles from Azure Key Vault,
/// read-only, authenticated with <see cref="DefaultAzureCredential"/> so the broker
/// uses a **Managed Identity / Workload Identity Federation** with no client secret
/// (ADR 0003). The same code also picks up the SP env (<c>AZURE_TENANT_ID</c> /
/// <c>AZURE_CLIENT_ID</c> / <c>AZURE_CLIENT_SECRET</c>) via <c>EnvironmentCredential</c>,
/// so it works against the existing harvester service principal and upgrades to WIF
/// with no code change.
/// </summary>
public sealed class AzureKeyVaultCredentialStore : ICredentialStore
{
    private readonly SecretClient _client;

    /// <summary>Creates a store over a configured <see cref="SecretClient"/>.</summary>
    public AzureKeyVaultCredentialStore(SecretClient client) => _client = client;

    /// <summary>
    /// Builds a store from <c>TESSERA_VAULT_URL</c>, or returns <c>null</c> if it
    /// is unset (so the runtime falls back to an empty in-memory store).
    /// </summary>
    public static AzureKeyVaultCredentialStore? FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var vaultUrl = environment is not null
            ? (environment.TryGetValue("TESSERA_VAULT_URL", out var v) ? v : null)
            : Environment.GetEnvironmentVariable("TESSERA_VAULT_URL");

        if (string.IsNullOrWhiteSpace(vaultUrl))
        {
            return null;
        }

        // Server posture: no interactive or local-dev-tool credentials — only
        // Environment (SP), Workload Identity Federation, and Managed Identity.
        var options = new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeAzureCliCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
        };

        var client = new SecretClient(new Uri(vaultUrl), new DefaultAzureCredential(options));
        return new AzureKeyVaultCredentialStore(client);
    }

    /// <inheritdoc/>
    public string Kind => "azure-key-vault";

    /// <inheritdoc/>
    public async Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            Response<KeyVaultSecret> response =
                await _client.GetSecretAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
            return BundleParser.Parse(response.Value.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No such secret = nothing stored for this binding (fail-closed, not an error).
            return CredentialBundle.Empty;
        }
        catch (RequestFailedException ex)
        {
            throw new StoreException($"Key Vault GET '{name}' failed: HTTP {ex.Status}", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new StoreException($"Key Vault auth failed reading '{name}': {ex.Message}", ex);
        }
    }
}
