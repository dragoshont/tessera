using Microsoft.AspNetCore.Http;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Identity;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;

namespace Tessera.Broker;

internal sealed class BrokerPluginRequestIdentity(
    ITokenValidator validator,
    TesseraConfig config,
    SqliteKernelStore store) : IPluginRequestIdentity
{
    public async ValueTask<string?> ResolveOwnerAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config).ConfigureAwait(false);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId)) return null;
        await PrincipalRegistration.RegisterForMutationAsync(
            context,
            store,
            PrincipalRef.Create(
                user.Issuer,
                user.TenantId,
                user.Subject,
                user.PreferredUsername,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return user.CanonicalPrincipalId;
    }
}

internal sealed class BrokerPluginAccountRuntime(
    SqliteKernelStore store,
    ICredentialStore custody,
    TesseraPluginRegistry plugins) : IPluginAccountRuntime
{
    public async ValueTask<ConnectedAccount?> GetAccountAsync(
        string ownerPrincipalId,
        string accountId,
        CancellationToken cancellationToken = default)
        => await store.GetConnectedAccountAsync(ownerPrincipalId, accountId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ConnectedAccount>> ListAccountsAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => await store.ListConnectedAccountsByPluginAsync(pluginId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ConnectedAccount> ConnectAsync(
        string ownerPrincipalId,
        string accountId,
        string providerId,
        string pluginId,
        string pluginVersion,
        string displayName,
        string nonSecretConfigurationJson,
        CredentialBundle credential,
        IReadOnlyList<string> permissions,
        IReadOnlyList<AccountCapabilityBinding> capabilities,
        CancellationToken cancellationToken = default)
    {
        if (custody is not ICredentialWriter writer)
            throw new InvalidOperationException("storage_unavailable");
        return await new R2ConnectedAccountService(store, writer).ConnectAsync(
            ownerPrincipalId,
            accountId,
            providerId,
            pluginId,
            pluginVersion,
            displayName,
            nonSecretConfigurationJson,
            credential,
            permissions,
            capabilities,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConnectedAccount> SetStateAsync(
        ConnectedAccount account,
        AccountLifecycle lifecycle,
        AccountHealth health,
        CancellationToken cancellationToken = default)
        => await store.SetConnectedAccountStateAsync(
            account.OwnerPrincipalId,
            account.AccountId,
            account.Version,
            lifecycle,
            health,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ConnectedAccount> SetValidationAsync(
        ConnectedAccount account,
        PluginAccountValidation validation,
        CancellationToken cancellationToken = default)
    {
        if (validation.ProviderAccountId is null || validation.IdentityHint is null)
            return await SetStateAsync(account, validation.Lifecycle, validation.Health, cancellationToken).ConfigureAwait(false);
        return await store.SetConnectedAccountValidationAsync(
            account.OwnerPrincipalId,
            account.AccountId,
            account.Version,
            validation.Lifecycle,
            validation.Health,
            validation.ProviderAccountId,
            validation.IdentityHint,
            validation.Permissions,
            validation.ProviderScopes,
            validation.CapabilityBindings,
            validation.LastSuccessfulUse ?? DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecomputeJobsHealthAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
        => await store.RecomputeJobsHealthAsync(
            ownerPrincipalId,
            plugins.ListManifests()
                .Select(manifest => (manifest.PluginId, manifest.Version))
                .ToHashSet(),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<PluginCursorState?> GetCursorAsync(
        string ownerPrincipalId,
        string accountId,
        string pluginId,
        string stateKey,
        CancellationToken cancellationToken = default)
    {
        var row = await store.GetPluginCursorAsync(
            ownerPrincipalId,
            accountId,
            pluginId,
            stateKey,
            cancellationToken).ConfigureAwait(false);
        return row is null
            ? null
            : new(
                row.OwnerPrincipalId,
                row.AccountId,
                row.PluginId,
                row.StateKey,
                row.Cursor,
                row.MetadataJson,
                row.UpdatedAt,
                row.Version);
    }

    public async ValueTask CommitCursorAsync(
        PluginCursorState state,
        IReadOnlyList<EvidenceRecord> evidence,
        IReadOnlyList<ObservationEvent> events,
        CancellationToken cancellationToken = default)
        => await store.CommitPluginCursorAsync(
            new(
                state.OwnerPrincipalId,
                state.AccountId,
                state.PluginId,
                state.StateKey,
                state.Cursor,
                state.MetadataJson,
                state.UpdatedAt,
                state.Version),
            evidence,
            events,
            cancellationToken).ConfigureAwait(false);
}