using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Providers;
using Tessera.Providers.R2;

namespace Tessera.Broker;

internal sealed record ModelGatewaySetupState(
    string State,
    string? GatewayId,
    string? DisplayName,
    string? Model,
    string? ProfileId,
    string? DetailCode);

internal sealed class ModelGatewayBootstrapService(
    TesseraConfig config,
    SqliteKernelStore store,
    ICredentialStore custody,
    IHttpTransport transport,
    Func<string, string?> environment)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new(StringComparer.Ordinal);

    public async Task<ModelGatewaySetupState> GetStateAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        var ownerGate = _ownerGates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await ownerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetStateCoreAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ownerGate.Release();
        }
    }

    private async Task<ModelGatewaySetupState> GetStateCoreAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        var profiles = await store.ListModelProfilesAsync(owner, cancellationToken)
            .ConfigureAwait(false);
        var configured = profiles.FirstOrDefault(profile => profile.Enabled);
        var gateway = AutoGateway();
        if (configured is not null)
        {
            if (gateway is not null
                && configured.ProfileId == StableId(owner, "model-profile", $"{gateway.Id}\n{gateway.DefaultModel}")
                && !Matches(configured, gateway, StableId(owner, "model-account", gateway.Id)))
                return new(
                    "DEGRADED",
                    gateway.Id,
                    gateway.DisplayName,
                    gateway.DefaultModel,
                    configured.ProfileId,
                    "gateway_binding_conflict");
            var account = await store.GetConnectedAccountAsync(owner, configured.AccountId, cancellationToken)
                .ConfigureAwait(false);
            if (account is not null
                && account.Lifecycle == AccountLifecycle.Connected
                && account.Health == AccountHealth.Healthy)
            {
                try
                {
                    var credentialRef = R2ConnectedAccountService.ValidateCredentialRef(account);
                    var bundle = await custody.GetBundleAsync(credentialRef, cancellationToken).ConfigureAwait(false);
                    if (!bundle.IsEmpty)
                        return new(
                            "CONNECTED",
                            GatewayId(configured),
                            configured.AdapterKind,
                            configured.Model,
                            configured.ProfileId,
                            null);
                }
                catch (InvalidDataException)
                {
                    return new(
                        "DEGRADED",
                        GatewayId(configured),
                        configured.AdapterKind,
                        configured.Model,
                        configured.ProfileId,
                        "model_gateway_credential_ref_invalid");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return new(
                        "DEGRADED",
                        GatewayId(configured),
                        configured.AdapterKind,
                        configured.Model,
                        configured.ProfileId,
                        "model_gateway_custody_unavailable");
                }
            }
        }

        if (gateway is null)
            return new("CONFIGURATION_REQUIRED", null, null, null, null, "model_gateway_not_configured");
        var secret = environment(gateway.CredentialEnvironmentVariable);
        return string.IsNullOrWhiteSpace(secret)
            ? new(
                "CONFIGURATION_REQUIRED",
                gateway.Id,
                gateway.DisplayName,
                gateway.DefaultModel,
                null,
                "model_gateway_credential_unavailable")
            : new(
                "READY_TO_CONNECT",
                gateway.Id,
                gateway.DisplayName,
                gateway.DefaultModel,
                null,
                configured is null ? null : "model_profile_repair_required");
    }

    public async Task<ModelGatewaySetupState> BootstrapAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        var ownerGate = _ownerGates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await ownerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await BootstrapCoreAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ownerGate.Release();
        }
    }

    private async Task<ModelGatewaySetupState> BootstrapCoreAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        var current = await GetStateCoreAsync(owner, cancellationToken).ConfigureAwait(false);
        if (current.State == "CONNECTED") return current;
        var gateway = AutoGateway()
            ?? throw new InvalidOperationException("model_gateway_not_configured");
        if (custody is not ICredentialWriter writer)
            throw new InvalidOperationException("storage_unavailable");
        var secret = environment(gateway.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("model_gateway_credential_unavailable");

        var probe = await new OpenAiCompatibleAdapter(transport)
            .ProbeTrustedInternalAsync(gateway.Endpoint, secret, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Available)
            throw new InvalidOperationException(probe.ErrorCode ?? "provider_unavailable");
        if (!probe.Models.Contains(gateway.DefaultModel, StringComparer.Ordinal))
            throw new InvalidOperationException("default_model_unavailable");

        var accountId = StableId(owner, "model-account", gateway.Id);
        var profileId = StableId(owner, "model-profile", $"{gateway.Id}\n{gateway.DefaultModel}");
        var credentialRef = ConnectedAccountCredentialRef.Create(owner, accountId);
        var configuration = JsonSerializer.Serialize(new
        {
            endpoint = gateway.Endpoint,
            gatewayId = gateway.Id,
            pluginVersion = "1.0.0",
        });
        var binding = new AccountCapabilityBinding(
            "model-provider",
            "1.0.0",
            "model.chat.complete",
            "1");
        var account = await store.GetConnectedAccountAsync(owner, accountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            account = await new R2ConnectedAccountService(store, writer)
                .ConnectAsync(
                    owner,
                    accountId,
                    "openai-compatible",
                    "model-provider",
                    "1.0.0",
                    gateway.DisplayName,
                    configuration,
                    new CredentialBundle(AccessToken: secret),
                    [],
                    [binding],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (!string.Equals(account.NonSecretConfigJson, configuration, StringComparison.Ordinal))
                throw new InvalidOperationException("gateway_binding_conflict");
            if (account.Lifecycle == AccountLifecycle.Revoked)
                throw new InvalidOperationException("gateway_binding_conflict");
            _ = R2ConnectedAccountService.ValidateCredentialRef(account);
            await writer.PutBundleAsync(
                    credentialRef,
                    new CredentialBundle(AccessToken: secret),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (account.Lifecycle != AccountLifecycle.Connected
            || account.Health != AccountHealth.Healthy)
        {
            account = await store.SetConnectedAccountStateAsync(
                    owner,
                    accountId,
                    account.Version,
                    AccountLifecycle.Connected,
                    AccountHealth.Healthy,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ProductConcurrencyException("Model account changed during bootstrap.");
        }

        var profile = await store.GetModelProfileAsync(owner, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            var now = DateTimeOffset.UtcNow;
            await store.AddModelProfileAsync(
                    new(
                        owner,
                        profileId,
                        accountId,
                        "openai-compatible-local",
                        gateway.Endpoint,
                        gateway.DefaultModel,
                        gateway.DefaultContextLimit,
                        true,
                        true,
                        true,
                        now,
                        now,
                        1),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!Matches(profile, gateway, accountId))
        {
            throw new InvalidOperationException("gateway_binding_conflict");
        }
        else if (!profile.Enabled)
        {
            throw new InvalidOperationException("model_profile_disabled");
        }

        var settings = await store.GetSettingsAsync(owner, cancellationToken).ConfigureAwait(false);
        if (settings.DefaultChatModelProfileId is null
            || settings.DefaultLightweightModelProfileId is null)
        {
            _ = await store.UpdateSettingsAsync(
                    owner,
                    settings.DefaultChatModelProfileId ?? profileId,
                    settings.DefaultLightweightModelProfileId ?? profileId,
                    null,
                    null,
                    null,
                    settings.Version,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ProductConcurrencyException("Settings changed during model bootstrap.");
        }
        await store.RecomputeJobsHealthAsync(owner, cancellationToken).ConfigureAwait(false);
        return new(
            "CONNECTED",
            gateway.Id,
            gateway.DisplayName,
            gateway.DefaultModel,
            profileId,
            null);
    }

    private ModelGatewayEndpointOptions? AutoGateway()
        => config.ModelGateways.Enabled
            ? config.ModelGateways.Endpoints.FirstOrDefault(endpoint => endpoint.AutoConnect)
            : null;

    private static string? GatewayId(ModelProfile profile)
    {
        try
        {
            return new Uri(profile.Endpoint).Host;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string StableId(string owner, string kind, string value)
        => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{kind}\n{value}")));

    private static bool Matches(
        ModelProfile profile,
        ModelGatewayEndpointOptions gateway,
        string accountId)
        => profile.AccountId == accountId
            && profile.AdapterKind == "openai-compatible-local"
            && profile.Endpoint == gateway.Endpoint
            && profile.Model == gateway.DefaultModel
            && profile.ContextLimit == gateway.DefaultContextLimit;
}
