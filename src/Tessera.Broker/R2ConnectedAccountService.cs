using System.Security.Cryptography;
using System.Text;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;

namespace Tessera.Broker;

public sealed class R2AccountStorageException(string message,Exception inner) : Exception(message,inner);

public sealed class R2ConnectedAccountService(SqliteKernelStore productStore, ICredentialWriter credentialWriter)
{
    public static async Task<CredentialBundle> GetValidatedBundleAsync(
        ICredentialStore credentialStore,
        ConnectedAccount account,
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(account);
        ConnectedAccountCredentialRef.Validate(account, ownerPrincipalId);
        var expected = ConnectedAccountCredentialRef.Create(ownerPrincipalId, account.AccountId);
        return await credentialStore.GetBundleAsync(expected, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectedAccount> ConnectAsync(
        string ownerPrincipalId, string accountId, string providerId, string pluginId, string pluginVersion,
        string displayName, string nonSecretConfigJson, CredentialBundle secretBundle,
        IReadOnlyList<string> permissions, IReadOnlyList<AccountCapabilityBinding> capabilities,
        CancellationToken cancellationToken = default)
    {
        if (secretBundle.IsEmpty) throw new ArgumentException("A non-empty credential bundle is required.", nameof(secretBundle));
        displayName=ProductContentValidation.Text(displayName,nameof(displayName),256);
        using(var configuration=System.Text.Json.JsonDocument.Parse(nonSecretConfigJson))
            ProductContentValidation.Json(configuration.RootElement,nameof(nonSecretConfigJson),16*1024);
        var credentialRef = ConnectedAccountCredentialRef.Create(ownerPrincipalId, accountId);
        var now = DateTimeOffset.UtcNow;
        var account = new ConnectedAccount(ownerPrincipalId,accountId,providerId,pluginId,pluginVersion,displayName,null,
            AccountLifecycle.Connecting,credentialRef,AccountHealth.Unknown,null,nonSecretConfigJson,permissions,capabilities,now,now,1);
        var cleanupReceiptId = Guid.NewGuid().ToString("N");
        await productStore.AddOrphanCredentialCleanupReceiptAsync(
            ownerPrincipalId, cleanupReceiptId, accountId, credentialRef, now, cancellationToken).ConfigureAwait(false);
        await credentialWriter.PutBundleAsync(credentialRef, secretBundle, cancellationToken).ConfigureAwait(false);
        try
        {
            await productStore.AddConnectedAccountAsync(account, cancellationToken).ConfigureAwait(false);
            await productStore.CompleteOrphanCleanupAsync(ownerPrincipalId, cleanupReceiptId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return account;
        }
        catch
        {
            try { await credentialWriter.PutBundleAsync(credentialRef, CredentialBundle.Empty, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new R2AccountStorageException("Account metadata failed and credential cleanup remains pending.",exception);
            }
            throw;
        }
    }

    public async Task<ConnectedAccount> RevokeAsync(
        string ownerPrincipalId, string accountId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var current = await productStore.GetConnectedAccountAsync(ownerPrincipalId,accountId,cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Account not found.");
        ValidateCredentialRef(current);
        var expectedCredentialRef = ConnectedAccountCredentialRef.Create(ownerPrincipalId, accountId);
        var cleanupReceiptId = Guid.NewGuid().ToString("N");
        var account=await productStore.RevokeConnectedAccountWithCleanupAsync(ownerPrincipalId,accountId,expectedVersion,cleanupReceiptId,expectedCredentialRef,DateTimeOffset.UtcNow,cancellationToken).ConfigureAwait(false);
        await productStore.RecomputeJobsHealthAsync(ownerPrincipalId,cancellationToken).ConfigureAwait(false);
        try
        {
            await credentialWriter.PutBundleAsync(expectedCredentialRef, CredentialBundle.Empty, cancellationToken).ConfigureAwait(false);
            await productStore.CompleteOrphanCleanupAsync(ownerPrincipalId,cleanupReceiptId,DateTimeOffset.UtcNow,cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
        return account;
    }

    public static string CredentialRef(string ownerPrincipalId, string accountId)
        => ConnectedAccountCredentialRef.Create(ownerPrincipalId, accountId);

    public static string ValidateCredentialRef(ConnectedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var expected = CredentialRef(account.OwnerPrincipalId, account.AccountId);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(account.CredentialRef)))
        {
            throw new InvalidDataException("credential_ref_mismatch");
        }
        return expected;
    }
}