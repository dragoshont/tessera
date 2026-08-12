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
    public async Task<ModelGatewaySetupState> GetStateAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        var profiles = await store.ListModelProfilesAsync(owner, cancellationToken)
            .ConfigureAwait(false);
        var configured = profiles.FirstOrDefault(profile => profile.Enabled);
        if (configured is not null)
            return new(
                "CONNECTED",
                GatewayId(configured),
                configured.AdapterKind,
                configured.Model,
                configured.ProfileId,
                null);

        var gateway = AutoGateway();
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
                null);
    }

    public async Task<ModelGatewaySetupState> BootstrapAsync(
        string owner,
        CancellationToken cancellationToken)
    {
        var current = await GetStateAsync(owner, cancellationToken).ConfigureAwait(false);
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
}
