using System.Net.Http;
using Azure;
using Azure.Core.Pipeline;
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
///
/// <para>For the LOCAL DEV LOOP (no Azure), setting <c>TESSERA_KEYVAULT_EMULATOR=lowkey</c>
/// points the same Azure SDK client at a local <a href="https://github.com/nagyesta/lowkey-vault">Lowkey
/// Vault</a> emulator (it speaks the Key Vault REST API). That path relaxes only
/// what the emulator needs — a dummy token, the challenge-resource bypass, a pinned
/// service version, and (for Lowkey's self-signed TLS) a dev cert bypass — and is
/// reflected in <see cref="Kind"/> as <c>azure-key-vault(lowkey)</c>. The production
/// path never sets the flag, so its behaviour is unchanged. See
/// docs/specs/local-azure-devloop.md.</para>
/// </summary>
public sealed class AzureKeyVaultCredentialStore : ICredentialStore, ICredentialWriter
{
    private readonly SecretClient _client;

    /// <summary>Creates a store over a configured <see cref="SecretClient"/>.</summary>
    public AzureKeyVaultCredentialStore(SecretClient client) : this(client, "azure-key-vault")
    {
    }

    private AzureKeyVaultCredentialStore(SecretClient client, string kind)
    {
        _client = client;
        Kind = kind;
    }

    /// <summary>
    /// Builds a store from <c>TESSERA_VAULT_URL</c>, or returns <c>null</c> if it
    /// is unset (so the runtime falls back to an empty in-memory store). When
    /// <c>TESSERA_KEYVAULT_EMULATOR=lowkey</c> is set, the client is wired for the
    /// local Lowkey emulator instead of a real vault (dev loop only).
    /// </summary>
    public static AzureKeyVaultCredentialStore? FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Get(string key) => environment is not null
            ? (environment.TryGetValue(key, out var v) ? v : null)
            : Environment.GetEnvironmentVariable(key);

        var vaultUrl = Get("TESSERA_VAULT_URL");
        if (string.IsNullOrWhiteSpace(vaultUrl))
        {
            return null;
        }

        var emulator = Get("TESSERA_KEYVAULT_EMULATOR");
        if (string.Equals(emulator, "lowkey", StringComparison.OrdinalIgnoreCase))
        {
            return BuildLowkey(vaultUrl);
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

    /// <summary>
    /// Wire the Azure SDK <see cref="SecretClient"/> for the local Lowkey emulator
    /// (dev loop only — never reached in production, which doesn't set the flag):
    /// a dummy token (Lowkey ignores it), the AAD challenge-resource verification
    /// disabled (no real AAD), the service version pinned to 7.4 (Lowkey's), and a
    /// transport that accepts Lowkey's self-signed TLS cert.
    /// </summary>
    private static AzureKeyVaultCredentialStore BuildLowkey(string vaultUrl)
    {
        // Accept Lowkey's self-signed cert — DEV ONLY, and only on this client.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        // Pin the Key Vault service version: the Azure SDK defaults to the newest
        // API version (7.6+), which Lowkey 2.7.1 rejects with HTTP 400. 7.5 is the
        // highest Lowkey speaks, and the broker only uses plain secret get/set, so
        // 7.5 is fully sufficient for the dev loop.
        var options = new SecretClientOptions(SecretClientOptions.ServiceVersion.V7_5)
        {
            DisableChallengeResourceVerification = true,
            Transport = new HttpClientTransport(new HttpClient(handler)),
        };

        var client = new SecretClient(new Uri(vaultUrl), new StaticDevTokenCredential(), options);
        return new AzureKeyVaultCredentialStore(client, "azure-key-vault(lowkey)");
    }

    /// <inheritdoc/>
    public string Kind { get; }

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

    /// <inheritdoc/>
    public async Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
    {
        // Merge-then-write: re-read the current secret and overlay the rotated
        // fields, so a concurrent harvester write isn't clobbered (the bundle may
        // carry fields we don't model). Only the sole session owner calls this.
        string merged;
        try
        {
            Response<KeyVaultSecret> current = await _client.GetSecretAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
            merged = BundleParser.Merge(current.Value.Value, bundle);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            merged = BundleParser.Serialize(bundle);
        }

        try
        {
            await _client.SetSecretAsync(name, merged, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new StoreException($"Key Vault SET '{name}' failed: HTTP {ex.Status}", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new StoreException($"Key Vault auth failed writing '{name}': {ex.Message}", ex);
        }
    }
}
