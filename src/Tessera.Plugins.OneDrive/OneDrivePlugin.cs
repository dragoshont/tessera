using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Plugin.Abstractions;

namespace Tessera.Plugins.OneDrive;

public sealed class OneDrivePlugin : ITesseraCapabilityPlugin, ITesseraAccountPlugin, ITesseraHostPlugin, ITesseraSetupPlugin
{
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
    private static readonly IReadOnlyList<PluginCapabilityManifest> Capabilities =
    [
        Capability("onedrive.account.identity", "Verify OneDrive account identity"),
        Capability("onedrive.items.list", "List one bounded page of OneDrive item metadata"),
        Capability("onedrive.items.get", "Get metadata for one exact OneDrive item"),
    ];

    public TesseraPluginManifest Manifest { get; } = new("onedrive", "1.0.0", "OneDrive", "onedrive", Capabilities);

    public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services, PluginHostConfiguration configuration)
        => OneDrivePluginHost.ConfigureServices(services, configuration);
    public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
        => OneDrivePluginHost.MapEndpoints(endpoints);
    public PluginSetupDescriptor DescribeSetup(PluginHostConfiguration configuration)
        => OneDrivePluginHost.DescribeSetup(configuration);

    public PluginAccountDefinition DefineAccount(string pluginVersion, JsonElement nonSecretConfiguration)
        => new("onedrive", [], Capabilities.Select(item => new AccountCapabilityBinding("onedrive", pluginVersion, item.CapabilityId, item.Version)).ToArray());

    public async ValueTask<PluginAccountValidation> ValidateAccountAsync(ConnectedAccount account, Tessera.Core.Stores.CredentialBundle credential, PluginCapabilityContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.AccessToken)) return FailedValidation(true);
        var result = await new OneDriveRestAdapter(context.Transport).ValidateAsync(credential.AccessToken, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Identity is null) return FailedValidation(result.ErrorCode == "provider_auth_required");
        if (!string.IsNullOrWhiteSpace(account.ProviderAccountId) && account.ProviderAccountId != result.Identity.DriveId) return FailedValidation(false);
        return new(AccountLifecycle.Connected, AccountHealth.Healthy, result.Identity.DriveId, result.Identity.OwnerDisplayName ?? result.Identity.DriveId,
            ["onedrive.read"], account.ProviderScopes, account.CapabilityBindings, DateTimeOffset.UtcNow);
    }

    public ValueTask DisconnectAccountAsync(ConnectedAccount account, Tessera.Core.Stores.CredentialBundle credential, PluginCapabilityContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<ICapability> CreateCapabilityAsync(string capabilityId, string capabilityVersion, PluginCapabilityContext context, CancellationToken cancellationToken = default)
    {
        if (capabilityVersion != "1" || context.Account is null || context.Account.ProviderId != "onedrive" || context.Account.PluginId != "onedrive")
            throw new InvalidOperationException("capability_unavailable");
        var credential = context.AccountCredential;
        return ValueTask.FromResult<ICapability>(new DeferredPluginCapability(
            Capabilities.Single(item => item.CapabilityId == capabilityId).ToDescriptor(),
            async token =>
            {
                var resolved = !string.IsNullOrWhiteSpace(credential.AccessToken)
                    ? credential
                    : await context.ResolveCredentialAsync(context.Account.CredentialRef, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(resolved.AccessToken)) throw new InvalidOperationException("account_credential_unavailable");
                return capabilityId switch
                {
                    "onedrive.account.identity" => new IdentityCapability(context.Transport, resolved.AccessToken, context.Account.OwnerPrincipalId),
                    "onedrive.items.list" => new ListCapability(context.Transport, resolved.AccessToken, context.Account.OwnerPrincipalId),
                    "onedrive.items.get" => new ItemCapability(context.Transport, resolved.AccessToken, context.Account.OwnerPrincipalId),
                    _ => throw new InvalidOperationException("capability_unavailable"),
                };
            }));
    }

    private sealed class IdentityCapability(Tessera.Providers.IHttpTransport transport, string accessToken, string ownerPrincipalId) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor("onedrive.account.identity", "Verify OneDrive account identity");
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.OwnerPrincipalId != ownerPrincipalId) return Failure("account_unavailable");
            if (invocation.TargetScope != "drive:identity") return Failure("invalid_request");
            var result = await new OneDriveRestAdapter(transport).ValidateAsync(accessToken, cancellationToken).ConfigureAwait(false);
            return result.Succeeded && result.Identity is not null
                ? Success(new { driveId = result.Identity.DriveId, driveType = result.Identity.DriveType, ownerDisplayName = result.Identity.OwnerDisplayName })
                : Failure(result.ErrorCode ?? "provider_unavailable");
        }
    }

    private sealed class ListCapability(Tessera.Providers.IHttpTransport transport, string accessToken, string ownerPrincipalId) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor("onedrive.items.list", "List one bounded page of OneDrive item metadata");
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.OwnerPrincipalId != ownerPrincipalId) return Failure("account_unavailable");
            if (invocation.TargetScope != "drive:children" || invocation.Input.ValueKind != JsonValueKind.Object
                || invocation.Input.EnumerateObject().Any(property => property.Name is not ("accountId" or "folderId" or "maxResults" or "cursor")))
                return Failure("invalid_request");
            var folderId = OptionalString(invocation.Input, "folderId");
            var cursor = OptionalString(invocation.Input, "cursor");
            var maximum = 25;
            if (invocation.Input.TryGetProperty("maxResults", out var maximumValue) && (!maximumValue.TryGetInt32(out maximum) || maximum is < 1 or > 25))
                return Failure("invalid_request");
            try
            {
                var result = await new OneDriveRestAdapter(transport).ListChildrenAsync(accessToken, folderId, maximum, cursor, cancellationToken).ConfigureAwait(false);
                return result.Succeeded ? Success(new { items = result.Items.Select(ItemOutput).ToArray(), cursor = result.Cursor }) : Failure(result.ErrorCode ?? "provider_unavailable");
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }
    }

    private sealed class ItemCapability(Tessera.Providers.IHttpTransport transport, string accessToken, string ownerPrincipalId) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor("onedrive.items.get", "Get metadata for one exact OneDrive item");
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.OwnerPrincipalId != ownerPrincipalId) return Failure("account_unavailable");
            if (invocation.TargetScope != "drive:item" || invocation.Input.ValueKind != JsonValueKind.Object
                || invocation.Input.EnumerateObject().Any(property => property.Name is not ("accountId" or "itemId")))
                return Failure("invalid_request");
            var itemId = OptionalString(invocation.Input, "itemId");
            if (itemId is null) return Failure("invalid_request");
            try
            {
                var result = await new OneDriveRestAdapter(transport).GetItemAsync(accessToken, itemId, cancellationToken).ConfigureAwait(false);
                return result.Succeeded && result.Item is not null ? Success(ItemOutput(result.Item)) : Failure(result.ErrorCode ?? "provider_unavailable");
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }
    }

    private static PluginCapabilityManifest Capability(string id, string description)
        => new(id, "1", description, id, ObjectSchema, ObjectSchema, SideEffectClass.ReadOnly, true, ["onedrive.read"], [SensitivityClass.Confidential], IdempotencySupport.None, VerificationSupport.None);
    private static CapabilityDescriptor DescriptorFor(string id, string description)
        => CapabilityDescriptor.Create(id, "1", description, "{}", "{}", SideEffectClass.ReadOnly, ["onedrive.read"], [SensitivityClass.Confidential], IdempotencySupport.None, VerificationSupport.None);
    private static PluginAccountValidation FailedValidation(bool authRequired)
        => new(authRequired ? AccountLifecycle.AuthRequired : AccountLifecycle.Degraded, authRequired ? AccountHealth.AuthRequired : AccountHealth.Degraded, null, null, [], [], [], null);
    private static string? OptionalString(JsonElement input, string name)
        => !input.TryGetProperty(name, out var value) ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException("invalid input");
    private static object ItemOutput(OneDriveItemMetadata item) => new
    {
        id = item.Id,
        name = item.Name,
        size = item.Size,
        isFolder = item.IsFolder,
        childCount = item.ChildCount,
        mimeType = item.MimeType,
        createdAt = item.CreatedAt,
        lastModifiedAt = item.LastModifiedAt,
    };
    private static CapabilityResult Success(object output)
        => new(CapabilityOutcome.Succeeded, JsonSerializer.SerializeToElement(output), null, null, null);
    private static CapabilityResult Failure(string code)
        => new(CapabilityOutcome.Failed, JsonSerializer.SerializeToElement(new { }), null, null, code);
}