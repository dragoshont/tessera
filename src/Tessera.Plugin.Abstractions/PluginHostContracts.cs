using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;

namespace Tessera.Plugin.Abstractions;

public sealed record PluginHostConfiguration(
    string? ConfigPath,
    Func<string, string?> GetEnvironmentVariable);

public interface ITesseraHostPlugin
{
    void ConfigureServices(IServiceCollection services, PluginHostConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

public interface IPluginRequestIdentity
{
    ValueTask<string?> ResolveOwnerAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);
}

public sealed record PluginCursorState(
    string OwnerPrincipalId,
    string AccountId,
    string PluginId,
    string StateKey,
    string Cursor,
    string MetadataJson,
    DateTimeOffset UpdatedAt,
    long Version);

public interface IPluginAccountRuntime
{
    ValueTask<ConnectedAccount?> GetAccountAsync(
        string ownerPrincipalId,
        string accountId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConnectedAccount>> ListAccountsAsync(
        string pluginId,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectedAccount> ConnectAsync(
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
        CancellationToken cancellationToken = default);

    ValueTask<ConnectedAccount> SetStateAsync(
        ConnectedAccount account,
        AccountLifecycle lifecycle,
        AccountHealth health,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectedAccount> SetValidationAsync(
        ConnectedAccount account,
        PluginAccountValidation validation,
        CancellationToken cancellationToken = default);

    ValueTask RecomputeJobsHealthAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default);

    ValueTask<PluginCursorState?> GetCursorAsync(
        string ownerPrincipalId,
        string accountId,
        string pluginId,
        string stateKey,
        CancellationToken cancellationToken = default);

    ValueTask CommitCursorAsync(
        PluginCursorState state,
        IReadOnlyList<EvidenceRecord> evidence,
        IReadOnlyList<ObservationEvent> events,
        CancellationToken cancellationToken = default);
}