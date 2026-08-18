using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Plugin.Abstractions;

#pragma warning disable CA2208

namespace Tessera.Plugins.ReginaMaria;

public sealed class ReginaMariaPlugin : ITesseraCapabilityPlugin, ITesseraModelToolPlugin, ITesseraHostPlugin, ITesseraMcpPlugin, ITesseraSetupPlugin
{
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
    });

    private static readonly IReadOnlyList<PluginCapabilityManifest> CapabilityManifests =
    [
        Capability("reginamaria.account.identity", "Verify Regina Maria identity", "rm_account_identity", SideEffectClass.ReadOnly, "reginamaria.identity", VerificationSupport.None),
        Capability("reginamaria.appointments.list", "List Regina Maria appointments", "rm_list_appointments", SideEffectClass.ReadOnly, "reginamaria.appointments.read", VerificationSupport.None),
        Capability("reginamaria.appointments.get", "Get Regina Maria appointment", "rm_list_appointments", SideEffectClass.ReadOnly, "reginamaria.appointments.read", VerificationSupport.None),
        Capability("reginamaria.availability.search", "Search Regina Maria availability", "rm_search_slots", SideEffectClass.ReadOnly, "reginamaria.availability.read", VerificationSupport.None),
        Capability("reginamaria.appointment.propose_book", "Prepare Regina Maria booking", "rm_prepare_appointment", SideEffectClass.ReadOnly, "reginamaria.appointments.write", VerificationSupport.None),
        Capability("reginamaria.appointment.propose_reschedule", "Prepare Regina Maria reschedule", "rm_prepare_appointment", SideEffectClass.ReadOnly, "reginamaria.appointments.write", VerificationSupport.None),
        Capability("reginamaria.appointment.propose_cancel", "Prepare Regina Maria cancellation", "rm_list_appointments", SideEffectClass.ReadOnly, "reginamaria.appointments.write", VerificationSupport.None),
        Capability("reginamaria.appointment.book", "Book Regina Maria appointment", "rm_create_appointment", SideEffectClass.ExternalReversible, "reginamaria.appointments.write", VerificationSupport.ProviderState),
        Capability("reginamaria.appointment.reschedule", "Reschedule Regina Maria appointment", "rm_create_appointment", SideEffectClass.ExternalReversible, "reginamaria.appointments.write", VerificationSupport.ProviderState),
        Capability("reginamaria.appointment.cancel", "Cancel Regina Maria appointment", "rm_cancel_appointment", SideEffectClass.ExternalReversible, "reginamaria.appointments.write", VerificationSupport.ProviderState),
    ];

    public TesseraPluginManifest Manifest { get; } = new(
        "regina-maria",
        "1.0.0",
        "Regina Maria",
        "regina-maria",
        CapabilityManifests);

    public IReadOnlyList<PluginModelToolManifest> ModelTools { get; } =
    [
        new("list_regina_maria_appointments", "reginamaria.appointments.list", "1", "List scheduling logistics for the explicitly selected Regina Maria account. Never infer or substitute a healthcare account.", Schema(new Dictionary<string, object?> { ["upcoming"] = Type("boolean"), ["maxResults"] = Integer(1, 20) })),
        new("search_regina_maria_availability", "reginamaria.availability.search", "1", "Search live scheduling availability for the explicitly selected Regina Maria account. This never books.", Schema(new Dictionary<string, object?> { ["specialty"] = Type("string"), ["service"] = Type("string"), ["doctor"] = Type("string"), ["location"] = Type("string"), ["city"] = Type("string"), ["dateFrom"] = Type("string"), ["dateTo"] = Type("string"), ["timePreferences"] = Type("string"), ["remoteOrInPerson"] = Type("string"), ["maxResults"] = Integer(1, 20) })),
        new("book_regina_maria_appointment", "reginamaria.appointment.book", "1", "Prepare a provider-validated Regina Maria booking for one-use human approval.", BookingSchema(reschedule: false), JobEligible: false, "reginamaria.appointment.propose_book", "1"),
        new("reschedule_regina_maria_appointment", "reginamaria.appointment.reschedule", "1", "Prepare a provider-validated Regina Maria reschedule for one-use human approval.", BookingSchema(reschedule: true), JobEligible: false, "reginamaria.appointment.propose_reschedule", "1"),
        new("cancel_regina_maria_appointment", "reginamaria.appointment.cancel", "1", "Prepare an exact Regina Maria cancellation for one-use human approval.", Schema(new Dictionary<string, object?> { ["appointmentId"] = Type("string"), ["asDependent"] = Type("string") }, ["appointmentId"]), JobEligible: false, "reginamaria.appointment.propose_cancel", "1"),
    ];

    public RequiredMcpServer RequiredMcpServer { get; } = new("reginamaria-mcp", "0.5.42");

    public IReadOnlyList<RequiredMcpTool> RequiredMcpTools { get; } =
    [
        new("rm_session_status", [], [new("alive", "boolean")]),
        new("rm_account_identity", [], [new("provider_account_id", "string"), new("display_name", "string")]),
        new("rm_list_appointments", [], [new("appointments", "array")]),
        new("rm_search_slots", [], [new("slots", "array")]),
        new("rm_prepare_appointment", [new("interval_id", "string"), new("physician_id", "string")], [new("bookable", "boolean"), new("slot_receipt", "string")]),
        new("rm_create_appointment", [new("interval_id", "string"), new("physician_id", "string")], [new("booked", "boolean"), new("id", "string")]),
        new("rm_cancel_appointment", [new("appointment_id", "string")], [new("cancelled", "boolean")]),
    ];

    public PluginSetupDescriptor DescribeSetup(PluginHostConfiguration configuration)
    {
        var options = ReginaMariaHostOptions.Load(configuration);
        return new(
            "regina-maria",
            "Regina Maria",
            options.Enabled && options.Connectors.Count > 0,
            true,
            options.Enabled ? "/api/v1/accounts/regina-maria/connectors" : null,
            options.Enabled ? "account_authorization_required" : "connector_runtime_unavailable");
    }

    public async ValueTask<McpServerContract> DiscoverMcpAsync(
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Account is null) throw new PluginModuleException("account_unavailable");
        return await context.McpRuntime.DiscoverAsync(
            new(context.Account.AccountId, Endpoint(context.Account), AllowPrivateNetwork: true),
            McpCallPolicy.ReadOnly,
            cancellationToken).ConfigureAwait(false);
    }

    public PluginModelToolBinding BindModelTool(string modelToolName, JsonElement arguments, ConnectedAccount? account)
    {
        if (account is null) throw new PluginModuleException("account_unavailable");
        var target = modelToolName switch
        {
            "list_regina_maria_appointments" => "appointments:list",
            "search_regina_maria_availability" => "availability:search",
            "book_regina_maria_appointment" => "appointment:book",
            "reschedule_regina_maria_appointment" => $"appointment/{RequiredModelText(arguments, "oldAppointmentId", 2048)}/reschedule",
            "cancel_regina_maria_appointment" => $"appointment/{RequiredModelText(arguments, "appointmentId", 2048)}/cancel",
            _ => throw new PluginModuleException("tool_not_available"),
        };
        return new(account.AccountId, target, arguments.Clone());
    }

    public void ConfigureServices(IServiceCollection services, PluginHostConfiguration configuration)
    {
        var options = ReginaMariaHostOptions.Load(configuration);
        services.AddSingleton(options);
        if (options.Enabled) services.AddHostedService<ReginaMariaPluginHealthService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<ReginaMariaHostOptions>();
        if (!options.Enabled) return;
        endpoints.MapGet("/api/v1/accounts/regina-maria/connectors", async (
            HttpContext context,
            IPluginRequestIdentity identity,
            CancellationToken token) =>
        {
            if (await identity.ResolveOwnerAsync(context, token) is null) return Problem(401, "unauthenticated");
            return Results.Json(new
            {
                items = options.Connectors.Select(item => new { id = item.Id, displayName = item.DisplayName }).ToArray(),
            });
        });
        endpoints.MapPost("/api/v1/accounts/regina-maria/connect", async (
            HttpContext context,
            ReginaMariaConnectRequest? request,
            IPluginRequestIdentity identity,
            IPluginAccountRuntime accounts,
            ICredentialStore custody,
            IMcpClientRuntime mcpRuntime,
            CancellationToken token) =>
        {
            var owner = await identity.ResolveOwnerAsync(context, token);
            if (owner is null) return Problem(401, "unauthenticated");
            if (request is null || string.IsNullOrWhiteSpace(request.ConnectorId) || string.IsNullOrWhiteSpace(request.DisplayName))
                return Problem(400, "invalid_request");
            var connector = options.Connectors.SingleOrDefault(item => item.Id == request.ConnectorId);
            if (connector is null) return Problem(404, "connector_unavailable");
            var endpoint = CanonicalMcpEndpoint(new Uri(connector.Endpoint));
            var mcp = new ReginaMariaMcp(mcpRuntime, $"regina-maria:{connector.Id}", endpoint);
            var status = await mcp.SessionStatusAsync(token);
            var alive = status.Succeeded && status.Output is { } statusOutput
                && statusOutput.TryGetProperty("alive", out var aliveValue)
                && aliveValue.ValueKind == JsonValueKind.True;
            var mutations = alive && status.Output!.Value.TryGetProperty("mutations_enabled", out var mutationValue)
                && mutationValue.ValueKind == JsonValueKind.True;
            var identityResult = alive ? await mcp.AccountIdentityAsync(token) : null;
            var providerAccountId = "";
            var providerDisplayName = "";
            var identityReady = identityResult?.Succeeded == true
                && TryIdentity(identityResult.Output, out providerAccountId, out providerDisplayName);
            var permissions = Permissions(mutations);
            var bindings = Bindings(mutations);
            var accountId = AccountId(owner, connector.Id);
            var configuration = JsonSerializer.Serialize(new { connectorId = connector.Id, endpoint = connector.Endpoint });
            var current = await accounts.GetAccountAsync(owner, accountId, token);
            if (current?.ProviderAccountId is not null && identityReady
                && !string.Equals(current.ProviderAccountId, providerAccountId, StringComparison.Ordinal))
            {
                await accounts.SetStateAsync(current, AccountLifecycle.Error, AccountHealth.Error, token);
                await accounts.RecomputeJobsHealthAsync(owner, token);
                return Problem(409, "provider_identity_mismatch");
            }
            if (current is not null && current.NonSecretConfigJson != configuration)
                return Problem(409, "connector_binding_conflict");
            var credential = new CredentialBundle(Extra: new Dictionary<string, string>
            {
                ["connector_id"] = connector.Id,
                ["action_credential_ref"] = options.ActionCredentialRef,
            });
            var account = current ?? await accounts.ConnectAsync(
                owner,
                accountId,
                "regina-maria",
                "regina-maria",
                "1.0.0",
                request.DisplayName,
                configuration,
                credential,
                permissions,
                bindings,
                token);
            if (current is not null && custody is ICredentialWriter writer)
                await writer.PutBundleAsync(current.CredentialRef, credential, token);
            if (identityReady)
                account = await accounts.SetValidationAsync(account, new(
                    AccountLifecycle.Connected,
                    AccountHealth.Healthy,
                    providerAccountId,
                    providerDisplayName,
                    permissions,
                    [],
                    bindings,
                    DateTimeOffset.UtcNow), token);
            else
                account = await accounts.SetStateAsync(
                    account,
                    alive ? AccountLifecycle.Degraded : status.Succeeded ? AccountLifecycle.AuthRequired : AccountLifecycle.Degraded,
                    alive ? AccountHealth.Degraded : status.Succeeded ? AccountHealth.AuthRequired : AccountHealth.Degraded,
                    token);
            await accounts.RecomputeJobsHealthAsync(owner, token);
            return Results.Json(AccountResponse(account), statusCode: identityReady ? 201 : 202);
        });
    }

    private static string[] Permissions(bool mutations)
        => mutations
            ? ["reginamaria.identity", "reginamaria.appointments.read", "reginamaria.availability.read", "reginamaria.appointments.write"]
            : ["reginamaria.identity", "reginamaria.appointments.read", "reginamaria.availability.read"];

    private static AccountCapabilityBinding[] Bindings(bool mutations)
    {
        var ids = CapabilityManifests
            .Where(item => mutations || item.SideEffectClass == SideEffectClass.ReadOnly
                && item.RequiredPermissions.All(permission => permission != "reginamaria.appointments.write"))
            .Select(item => item.CapabilityId);
        if (mutations) ids = CapabilityManifests.Select(item => item.CapabilityId);
        return ids.Select(id => new AccountCapabilityBinding("regina-maria", "1.0.0", id, "1")).ToArray();
    }

    internal static bool TryIdentity(JsonElement? output, out string providerId, out string displayName)
    {
        providerId = ""; displayName = "";
        if (output is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("provider_account_id", out var id) || id.ValueKind != JsonValueKind.String
            || !value.TryGetProperty("display_name", out var name) || name.ValueKind != JsonValueKind.String)
            return false;
        providerId = id.GetString() ?? ""; displayName = name.GetString() ?? "";
        return providerId.Length is > 0 and <= 256 && displayName.Length is > 0 and <= 256;
    }

    private static string AccountId(string owner, string connector)
        => "rm-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{connector}")))[..24];

    private static object AccountResponse(ConnectedAccount item) => new
    {
        id = item.AccountId,
        accountId = item.AccountId,
        item.ProviderId,
        item.PluginId,
        item.DisplayName,
        item.ProviderAccountId,
        item.IdentityHint,
        lifecycle = item.Lifecycle.ToContractValue(),
        permissions = item.Permissions,
        capabilityIds = item.CapabilityBindings.Select(value => value.CapabilityId).ToArray(),
        health = item.Health.ToContractValue(),
        item.LastSuccessfulUse,
        item.Version,
    };

    private static IResult Problem(int status, string code)
        => Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });

    public ValueTask<ICapability> CreateCapabilityAsync(
        string capabilityId,
        string capabilityVersion,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        if (capabilityVersion != "1" || context.Account is null)
            throw new InvalidOperationException("capability_unavailable");
        var account = context.Account;
        var mcp = new ReginaMariaMcp(context.McpRuntime, account.AccountId, Endpoint(account));
        if (capabilityId is not ("reginamaria.appointment.book" or "reginamaria.appointment.reschedule" or "reginamaria.appointment.cancel"))
            return ValueTask.FromResult(ReginaMariaCapabilities.Create(capabilityId, mcp, null));
        var manifest = CapabilityManifests.Single(item => item.CapabilityId == capabilityId);
        return ValueTask.FromResult<ICapability>(new DeferredPluginCapability(
            manifest.ToDescriptor(),
            async token =>
            {
                var accountCredential = context.AccountCredential.Extra?.ContainsKey("action_credential_ref") == true
                    ? context.AccountCredential
                    : await context.ResolveCredentialAsync(account.CredentialRef, token).ConfigureAwait(false);
                var actionReference = accountCredential.Extra?.GetValueOrDefault("action_credential_ref");
                if (string.IsNullOrWhiteSpace(actionReference))
                    throw new InvalidOperationException("action_credential_unavailable");
                async ValueTask<string> ActionToken(CancellationToken actionTokenCancellation)
                {
                    var actionCredential = await context.ResolveCredentialAsync(actionReference, actionTokenCancellation).ConfigureAwait(false);
                    var value = actionCredential.Extra?.GetValueOrDefault("action_token") ?? actionCredential.AccessToken;
                    return !string.IsNullOrWhiteSpace(value)
                        ? value
                        : throw new InvalidOperationException("action_credential_unavailable");
                }
                return ReginaMariaCapabilities.Create(capabilityId, mcp, ActionToken);
            }));
    }

    private static PluginCapabilityManifest Capability(
        string id,
        string description,
        string tool,
        SideEffectClass sideEffect,
        string permission,
        VerificationSupport verification) => new(
            id,
            "1",
            description,
            tool,
            ObjectSchema,
            ObjectSchema,
            sideEffect,
            true,
            [permission],
            [SensitivityClass.Restricted],
            IdempotencySupport.Keyed,
            verification);

    private static JsonElement BookingSchema(bool reschedule)
    {
        var properties = new Dictionary<string, object?>
        {
            ["slotReceipt"] = Type("string"), ["intervalId"] = Type("string"), ["physicianId"] = Type("string"), ["serviceId"] = Type("string"), ["asDependent"] = Type("string"),
            ["service"] = Type("string"), ["doctor"] = Type("string"), ["specialty"] = Type("string"), ["location"] = Type("string"),
            ["date"] = Type("string"), ["time"] = Type("string"), ["mode"] = Type("string"), ["price"] = Type("number"), ["currency"] = Type("string"),
        };
        var required = new List<string> { "slotReceipt", "intervalId", "physicianId", "doctor", "specialty", "service", "location", "date", "time" };
        if (reschedule) { properties["oldAppointmentId"] = Type("string"); required.Insert(0, "oldAppointmentId"); }
        return Schema(properties, required);
    }

    private static JsonElement Schema(IReadOnlyDictionary<string, object?> properties, IReadOnlyList<string>? required = null)
        => JsonSerializer.SerializeToElement(new { type = "object", properties, required = required ?? [], additionalProperties = false });

    private static object Type(string type) => new { type };
    private static object Integer(int minimum, int maximum) => new { type = "integer", minimum, maximum };

    private static string RequiredModelText(JsonElement arguments, string name, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new PluginModuleException("invalid_tool_arguments");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new PluginModuleException("invalid_tool_arguments");
        return text;
    }

    private static Uri Endpoint(ConnectedAccount account)
    {
        if (account.ProviderId != "regina-maria") throw new InvalidOperationException("account_unavailable");
        try
        {
            using var document = JsonDocument.Parse(account.NonSecretConfigJson);
            var endpoint = new Uri(document.RootElement.GetProperty("endpoint").GetString()
                ?? throw new InvalidOperationException("invalid_configuration"));
            return CanonicalMcpEndpoint(endpoint);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or UriFormatException)
        {
            throw new InvalidOperationException("invalid_configuration", exception);
        }
    }

    internal static Uri CanonicalMcpEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath.TrimEnd('/') != "/mcp")
            throw new InvalidOperationException("invalid_configuration");
        var builder = new UriBuilder(endpoint) { Path = "/mcp/" };
        return builder.Uri;
    }
}

internal sealed record ReginaMariaMcpResult(
    bool Succeeded,
    bool UnknownOutcome,
    JsonElement? Output,
    string? ErrorCode = null);

internal sealed class ReginaMariaMcp(IMcpClientRuntime runtime, string serverId, Uri endpoint)
{
    public Task<McpServerContract> DiscoverAsync(CancellationToken token)
        => runtime.DiscoverAsync(new(serverId, endpoint, AllowPrivateNetwork: true), McpCallPolicy.ReadOnly, token);
    public Task<ReginaMariaMcpResult> SessionStatusAsync(CancellationToken token) => CallAsync("rm_session_status", JsonSerializer.SerializeToElement(new { }), false, token);
    public Task<ReginaMariaMcpResult> AccountIdentityAsync(CancellationToken token) => CallAsync("rm_account_identity", JsonSerializer.SerializeToElement(new { }), false, token);
    public Task<ReginaMariaMcpResult> ListAppointmentsAsync(JsonElement arguments, CancellationToken token) => CallAsync("rm_list_appointments", arguments, false, token);
    public Task<ReginaMariaMcpResult> SearchSlotsAsync(JsonElement arguments, CancellationToken token) => CallAsync("rm_search_slots", arguments, false, token);
    public Task<ReginaMariaMcpResult> PrepareAppointmentAsync(JsonElement arguments, CancellationToken token) => CallAsync("rm_prepare_appointment", arguments, false, token);
    public Task<ReginaMariaMcpResult> BookAsync(JsonElement arguments, string actionToken, CancellationToken token) => CallAsync("rm_create_appointment", WithActionToken(arguments, actionToken), true, token);
    public Task<ReginaMariaMcpResult> CancelAsync(JsonElement arguments, string actionToken, CancellationToken token) => CallAsync("rm_cancel_appointment", WithActionToken(arguments, actionToken), true, token);

    private async Task<ReginaMariaMcpResult> CallAsync(string tool, JsonElement arguments, bool mutating, CancellationToken token)
    {
        var values = arguments.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        var result = await runtime.CallAsync(
            new(serverId, endpoint, AllowPrivateNetwork: true),
            tool,
            values,
            new(TimeSpan.FromSeconds(30), 512 * 1024, mutating),
            token).ConfigureAwait(false);
        return result.Outcome switch
        {
            McpInvocationOutcome.Succeeded when result.StructuredOutput is { } output => new(true, false, output),
            McpInvocationOutcome.UnknownOutcome => new(false, true, null, result.ErrorCode ?? "unknown_outcome"),
            _ => new(false, false, null, result.ErrorCode ?? "provider_unavailable"),
        };
    }

    private static JsonElement WithActionToken(JsonElement arguments, string actionToken)
    {
        if (string.IsNullOrWhiteSpace(actionToken)) throw new ArgumentException("Action token is required.", nameof(actionToken));
        var values = arguments.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        values["_tessera_action_token"] = JsonSerializer.SerializeToElement(actionToken);
        return JsonSerializer.SerializeToElement(values);
    }
}

internal sealed record ReginaMariaConnector(string Id, string DisplayName, string Endpoint);

internal sealed record ReginaMariaHostOptions(
    bool Enabled,
    bool AllowPlainHttp,
    string ActionCredentialRef,
    IReadOnlyList<ReginaMariaConnector> Connectors)
{
    public static ReginaMariaHostOptions Load(PluginHostConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ConfigPath) || !File.Exists(configuration.ConfigPath))
            return new(false, false, "", []);
        using var document = JsonDocument.Parse(File.ReadAllText(configuration.ConfigPath));
        if (!document.RootElement.TryGetProperty("reginaMaria", out var root)
            || root.ValueKind != JsonValueKind.Object)
            return new(false, false, "", []);
        var enabled = root.TryGetProperty("enabled", out var enabledValue) && enabledValue.ValueKind == JsonValueKind.True;
        var allowPlainHttp = root.TryGetProperty("allowPlainHttp", out var plainValue)
            && plainValue.ValueKind == JsonValueKind.True;
        var actionRef = root.TryGetProperty("actionCredentialRef", out var actionValue)
            && actionValue.ValueKind == JsonValueKind.String ? actionValue.GetString() ?? "" : "";
        var connectors = new List<ReginaMariaConnector>();
        if (root.TryGetProperty("connectors", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var item in values.EnumerateArray())
                connectors.Add(new(
                    item.GetProperty("id").GetString() ?? throw new InvalidOperationException("invalid_configuration"),
                    item.GetProperty("displayName").GetString() ?? throw new InvalidOperationException("invalid_configuration"),
                    item.GetProperty("endpoint").GetString() ?? throw new InvalidOperationException("invalid_configuration")));
        if (enabled && (string.IsNullOrWhiteSpace(actionRef) || connectors.Count is < 1 or > 8
            || connectors.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != connectors.Count
            || connectors.Select(item => item.Endpoint).Distinct(StringComparer.Ordinal).Count() != connectors.Count))
            throw new InvalidOperationException("invalid_configuration");
        foreach (var connector in connectors)
        {
            if (connector.Id.Length is < 1 or > 64
                || !connector.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || string.IsNullOrWhiteSpace(connector.DisplayName)
                || connector.DisplayName.Length > 128
                || !Uri.TryCreate(connector.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(endpoint.UserInfo)
                || !string.IsNullOrEmpty(endpoint.Query)
                || !string.IsNullOrEmpty(endpoint.Fragment)
                || endpoint.AbsolutePath.TrimEnd('/') != "/mcp"
                || endpoint.Scheme == "http" && !allowPlainHttp)
                throw new InvalidOperationException("invalid_configuration");
        }
        return new(enabled, allowPlainHttp, actionRef, connectors);
    }
}

internal sealed record ReginaMariaConnectRequest(string ConnectorId, string DisplayName);

internal sealed partial class ReginaMariaPluginHealthService(
    IPluginAccountRuntime accounts,
    IMcpClientRuntime runtime,
    ILogger<ReginaMariaPluginHealthService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            await HealthPassAsync(stoppingToken);
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task HealthPassAsync(CancellationToken token)
    {
        foreach (var account in await accounts.ListAccountsAsync("regina-maria", token))
        {
            try
            {
                using var configuration = JsonDocument.Parse(account.NonSecretConfigJson);
                var endpoint = configuration.RootElement.GetProperty("endpoint").GetString()
                    ?? throw new InvalidOperationException("invalid_configuration");
                var mcp = new ReginaMariaMcp(
                    runtime,
                    $"regina-maria:{account.AccountId}",
                    ReginaMariaPlugin.CanonicalMcpEndpoint(new Uri(endpoint)));
                var status = await mcp.SessionStatusAsync(token);
                var alive = status.Succeeded && status.Output is { } output
                    && output.TryGetProperty("alive", out var value) && value.ValueKind == JsonValueKind.True;
                var identity = alive ? await mcp.AccountIdentityAsync(token) : null;
                if (alive && identity?.Succeeded == true
                    && ReginaMariaPlugin.TryIdentity(identity.Output, out var providerId, out var displayName)
                    && (account.ProviderAccountId is null || account.ProviderAccountId == providerId))
                    await accounts.SetValidationAsync(account, new(
                        AccountLifecycle.Connected,
                        AccountHealth.Healthy,
                        providerId,
                        displayName,
                        account.Permissions,
                        account.ProviderScopes,
                        account.CapabilityBindings,
                        DateTimeOffset.UtcNow), token);
                else
                    await accounts.SetStateAsync(
                        account,
                        alive ? AccountLifecycle.Error : status.Succeeded ? AccountLifecycle.AuthRequired : AccountLifecycle.Degraded,
                        alive ? AccountHealth.Error : status.Succeeded ? AccountHealth.AuthRequired : AccountHealth.Degraded,
                        token);
                await accounts.RecomputeJobsHealthAsync(account.OwnerPrincipalId, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogHealthFailure(logger, account.AccountId, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regina Maria plugin health check failed for account {AccountId}.")]
    private static partial void LogHealthFailure(ILogger logger, string accountId, Exception exception);
}

internal static class ReginaMariaCapabilities
{
    public static ICapability Create(string capabilityId, ReginaMariaMcp mcp, Func<CancellationToken, ValueTask<string>>? actionToken) => capabilityId switch
    {
        "reginamaria.account.identity" => new ReadCapability(capabilityId, "Verify Regina Maria identity", mcp, ReadKind.Identity),
        "reginamaria.appointments.list" => new ReadCapability(capabilityId, "List Regina Maria appointments", mcp, ReadKind.List),
        "reginamaria.appointments.get" => new ReadCapability(capabilityId, "Get Regina Maria appointment", mcp, ReadKind.Get),
        "reginamaria.availability.search" => new ReadCapability(capabilityId, "Search Regina Maria availability", mcp, ReadKind.Search),
        "reginamaria.appointment.propose_book" => new ProposalCapability(capabilityId, "Prepare Regina Maria booking", mcp, false),
        "reginamaria.appointment.propose_reschedule" => new ProposalCapability(capabilityId, "Prepare Regina Maria reschedule", mcp, true),
        "reginamaria.appointment.propose_cancel" => new CancelProposalCapability(mcp),
        "reginamaria.appointment.book" => new BookCapability(capabilityId, "Book Regina Maria appointment", mcp, false, actionToken ?? throw new InvalidOperationException("action_credential_unavailable")),
        "reginamaria.appointment.reschedule" => new BookCapability(capabilityId, "Reschedule Regina Maria appointment", mcp, true, actionToken ?? throw new InvalidOperationException("action_credential_unavailable")),
        "reginamaria.appointment.cancel" => new CancelCapability(mcp, actionToken ?? throw new InvalidOperationException("action_credential_unavailable")),
        _ => throw new InvalidOperationException("capability_unavailable"),
    };

    private enum ReadKind { Identity, List, Get, Search }

    private sealed class ReadCapability(string id, string description, ReginaMariaMcp mcp, ReadKind kind) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor(id, description, SideEffectClass.ReadOnly, kind == ReadKind.Identity ? "reginamaria.identity" : kind == ReadKind.Search ? "reginamaria.availability.read" : "reginamaria.appointments.read", VerificationSupport.None);
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken token = default)
        {
            try
            {
                var result = kind switch
                {
                    ReadKind.Identity when invocation.TargetScope == "account:identity" => await mcp.AccountIdentityAsync(token),
                    ReadKind.List when invocation.TargetScope == "appointments:list" => await mcp.ListAppointmentsAsync(ListArguments(invocation.Input), token),
                    ReadKind.Get when invocation.TargetScope.StartsWith("appointment/", StringComparison.Ordinal) => await GetAppointmentAsync(invocation, token),
                    ReadKind.Search when invocation.TargetScope == "availability:search" => await mcp.SearchSlotsAsync(SearchArguments(invocation.Input), token),
                    _ => new(false, false, null, "invalid_request"),
                };
                return Result(result);
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }

        private async Task<ReginaMariaMcpResult> GetAppointmentAsync(CapabilityInvocation invocation, CancellationToken token)
        {
            if (!invocation.Input.TryGetProperty("appointmentId", out var value) || value.ValueKind != JsonValueKind.String) return new(false, false, null, "invalid_request");
            var appointmentId = ProviderReference(value.GetString());
            if (invocation.TargetScope != $"appointment/{appointmentId}") return new(false, false, null, "invalid_request");
            var listed = await mcp.ListAppointmentsAsync(JsonSerializer.SerializeToElement(new { upcoming = true, first = 0, max_results = 20 }), token);
            if (!listed.Succeeded || listed.Output is null) return listed;
            var appointment = Appointment(listed.Output.Value, appointmentId);
            return appointment is null ? new(false, false, null, "appointment_not_found") : new(true, false, JsonSerializer.SerializeToElement(new { appointment = appointment.Value.Clone() }));
        }
    }

    private sealed class ProposalCapability(string id, string description, ReginaMariaMcp mcp, bool reschedule) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor(id, description, SideEffectClass.ReadOnly, "reginamaria.appointments.write", VerificationSupport.None);
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken token = default)
        {
            try
            {
                var arguments = BookingArguments(invocation.Input, reschedule);
                if (invocation.TargetScope != (reschedule ? $"appointment/{arguments.GetProperty("old_appointment_id").GetString()}/reschedule" : "appointment:book")) return Failure("invalid_request");
                var result = await mcp.PrepareAppointmentAsync(arguments, token);
                if (!result.Succeeded || result.Output is null) return Result(result);
                if (!PreflightReferencesMatch(result.Output.Value, arguments)) return Failure("slot_not_bookable");
                return Success(CanonicalBooking(result.Output.Value, arguments, reschedule));
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }
    }

    private sealed class CancelProposalCapability(ReginaMariaMcp mcp) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor("reginamaria.appointment.propose_cancel", "Prepare Regina Maria cancellation", SideEffectClass.ReadOnly, "reginamaria.appointments.write", VerificationSupport.None);
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken token = default)
        {
            try
            {
                var id = RequiredAppointmentId(invocation);
                var dependent = OptionalString(invocation.Input, "asDependent", 256);
                var listed = await mcp.ListAppointmentsAsync(JsonSerializer.SerializeToElement(new { upcoming = true, first = 0, max_results = 20, as_dependent = dependent }), token);
                if (!listed.Succeeded || listed.Output is null) return Result(listed);
                var appointment = Appointment(listed.Output.Value, id);
                return appointment is null ? Failure("appointment_not_found") : Success(CanonicalCancellation(appointment.Value, invocation.Input));
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }
    }

    private sealed class BookCapability(string id, string description, ReginaMariaMcp mcp, bool reschedule, Func<CancellationToken, ValueTask<string>> actionToken) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor(id, description, SideEffectClass.ExternalReversible, "reginamaria.appointments.write", VerificationSupport.ProviderState);
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken token = default)
        {
            try
            {
                var arguments = BookingArguments(invocation.Input, reschedule);
                var oldId = reschedule ? arguments.GetProperty("old_appointment_id").GetString() : null;
                if (invocation.TargetScope != (reschedule ? $"appointment/{oldId}/reschedule" : "appointment:book")) return Failure("invalid_request");
                var preflight = await mcp.PrepareAppointmentAsync(arguments, token);
                if (!preflight.Succeeded || preflight.Output is null || !PreflightMatches(invocation.Input, arguments, preflight.Output.Value)) return Failure(preflight.ErrorCode ?? "provider_preflight_mismatch");
                var mutationArguments = arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
                mutationArguments["confirm"] = JsonSerializer.SerializeToElement(true);
                var result = await mcp.BookAsync(JsonSerializer.SerializeToElement(mutationArguments), await actionToken(token), token);
                if (!result.Succeeded && !result.UnknownOutcome) return MutationFailure(result);
                if (result.Succeeded && (result.Output is not { } successful || !successful.TryGetProperty("booked", out var booked) || booked.ValueKind != JsonValueKind.True)) return Failure("provider_rejected");
                var providerId = result.Output is { } output && output.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
                var dependent = OptionalString(invocation.Input, "asDependent", 256);
                var listed = await mcp.ListAppointmentsAsync(JsonSerializer.SerializeToElement(new { upcoming = true, first = 0, max_results = 20, as_dependent = dependent }), token);
                if (!listed.Succeeded || listed.Output is null) return Unknown(providerId, result.ErrorCode ?? "verification_failed");
                var match = providerId is null ? FindNaturalMatch(listed.Output.Value, invocation.Input) : Appointment(listed.Output.Value, providerId);
                if (match is null) return Unknown(providerId, result.ErrorCode ?? "verification_failed");
                if (reschedule && Appointment(listed.Output.Value, oldId!) is not null && providerId != oldId) return Unknown(providerId, "old_appointment_still_present");
                return new(CapabilityOutcome.Succeeded, JsonSerializer.SerializeToElement(new { appointment = match.Value.Clone(), oldAppointmentId = oldId, reconciled = result.UnknownOutcome }), providerId ?? AppointmentId(match.Value), "provider_verified", null);
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }
    }

    private sealed class CancelCapability(ReginaMariaMcp mcp, Func<CancellationToken, ValueTask<string>> actionToken) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor("reginamaria.appointment.cancel", "Cancel Regina Maria appointment", SideEffectClass.ExternalReversible, "reginamaria.appointments.write", VerificationSupport.ProviderState);
        public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation, CancellationToken token = default)
        {
            try
            {
                var id = RequiredAppointmentId(invocation);
                var dependent = OptionalString(invocation.Input, "asDependent", 256);
                var result = await mcp.CancelAsync(JsonSerializer.SerializeToElement(new { appointment_id = id, confirm = true, as_dependent = dependent }), await actionToken(token), token);
                if (!result.Succeeded && !result.UnknownOutcome) return MutationFailure(result);
                if (result.Succeeded && (result.Output is not { } successful || !successful.TryGetProperty("cancelled", out var cancelled) || cancelled.ValueKind != JsonValueKind.True)) return Failure("provider_rejected");
                var listed = await mcp.ListAppointmentsAsync(JsonSerializer.SerializeToElement(new { upcoming = true, first = 0, max_results = 20, as_dependent = dependent }), token);
                if (!listed.Succeeded || listed.Output is null) return Unknown(id, result.ErrorCode ?? "verification_failed");
                return Appointment(listed.Output.Value, id) is null
                    ? new(CapabilityOutcome.Succeeded, JsonSerializer.SerializeToElement(new { appointmentId = id, cancelled = true, reconciled = result.UnknownOutcome }), id, "provider_verified", null)
                    : Unknown(id, "appointment_still_present");
            }
            catch (ArgumentException) { return Failure("invalid_request"); }
        }
    }

    private static CapabilityDescriptor DescriptorFor(string id, string description, SideEffectClass sideEffect, string permission, VerificationSupport verification)
        => CapabilityDescriptor.Create(id, "1", description, "{}", "{}", sideEffect, [permission], [SensitivityClass.Restricted], IdempotencySupport.Keyed, verification);

    private static JsonElement ListArguments(JsonElement input)
    {
        Only(input, "accountId", "upcoming", "first", "maxResults");
        var upcoming = !input.TryGetProperty("upcoming", out var upcomingValue) || upcomingValue.ValueKind == JsonValueKind.True;
        if (input.TryGetProperty("upcoming", out upcomingValue) && upcomingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException();
        var first = input.TryGetProperty("first", out var firstValue) && firstValue.TryGetInt32(out var parsedFirst) ? parsedFirst : 0;
        var maximum = input.TryGetProperty("maxResults", out var maxValue) && maxValue.TryGetInt32(out var parsedMax) ? parsedMax : 20;
        if (first < 0 || maximum is < 1 or > 20) throw new ArgumentException();
        return JsonSerializer.SerializeToElement(new { upcoming, first, max_results = maximum });
    }

    private static JsonElement SearchArguments(JsonElement input)
    {
        Only(input, "accountId", "specialty", "service", "doctor", "location", "city", "dateFrom", "dateTo", "timePreferences", "remoteOrInPerson", "maxResults");
        string? Text(string name, int max) { if (!input.TryGetProperty(name, out var value)) return null; if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(); var text = value.GetString(); if (string.IsNullOrWhiteSpace(text) || text.Length > max) throw new ArgumentException(); return text; }
        var maximum = input.TryGetProperty("maxResults", out var maxValue) && maxValue.TryGetInt32(out var parsed) ? parsed : 8;
        if (maximum is < 1 or > 20) throw new ArgumentException();
        return JsonSerializer.SerializeToElement(new { specialty = Text("specialty", 256), service = Text("service", 256), doctor = Text("doctor", 256), district = Text("city", 128) ?? Text("location", 128), date_start = Text("dateFrom", 32), part_of_day = Text("timePreferences", 64), mode = Text("remoteOrInPerson", 64) ?? "any", max_results = maximum });
    }

    private static JsonElement BookingArguments(JsonElement input, bool reschedule)
    {
        Only(input, "accountId", "slotReceipt", "intervalId", "physicianId", "serviceId", "service", "doctor", "specialty", "location", "date", "time", "mode", "price", "currency", "oldAppointmentId", "asDependent");
        var serviceId = input.TryGetProperty("serviceId", out var value) && value.ValueKind == JsonValueKind.String ? ProviderReference(value.GetString()) : null;
        return JsonSerializer.SerializeToElement(new { slot_receipt = ProviderReference(RequiredString(input, "slotReceipt", 4096)), interval_id = ProviderReference(RequiredString(input, "intervalId", 2048)), physician_id = ProviderReference(RequiredString(input, "physicianId", 2048)), service_id = serviceId, service = OptionalString(input, "service", 256), agree_virtual = true, old_appointment_id = reschedule ? ProviderReference(RequiredString(input, "oldAppointmentId", 2048)) : null, as_dependent = OptionalString(input, "asDependent", 256) });
    }

    private static string RequiredAppointmentId(CapabilityInvocation invocation)
    {
        var id = ProviderReference(RequiredString(invocation.Input, "appointmentId", 2048));
        if (invocation.TargetScope != $"appointment/{id}/cancel") throw new ArgumentException();
        Only(invocation.Input, "accountId", "appointmentId", "doctor", "specialty", "service", "location", "date", "time", "price", "currency", "asDependent");
        return id;
    }

    private static JsonElement? Appointment(JsonElement output, string id)
    {
        if (!output.TryGetProperty("appointments", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var matches = items.EnumerateArray().Where(item => AppointmentId(item) == id).ToArray();
        return matches.Length == 1 ? matches[0].Clone() : null;
    }

    private static JsonElement? FindNaturalMatch(JsonElement output, JsonElement input)
    {
        if (!output.TryGetProperty("appointments", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var date = OptionalString(input, "date", 32); var time = OptionalString(input, "time", 16); var doctor = OptionalString(input, "doctor", 256);
        var matches = items.EnumerateArray().Where(item => (date is null || OptionalString(item, "date", 32) == date) && (time is null || OptionalString(item, "time", 16) == time) && (doctor is null || OptionalString(item, "doctor", 256) == doctor)).ToArray();
        return matches.Length == 1 ? matches[0].Clone() : null;
    }

    private static JsonElement CanonicalBooking(JsonElement provider, JsonElement arguments, bool reschedule)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["slotReceipt"] = RequiredString(provider, "slot_receipt", 4096), ["intervalId"] = RequiredString(provider, "interval_id", 2048), ["physicianId"] = RequiredString(provider, "physician_id", 2048), ["serviceId"] = RequiredString(provider, "service_id", 2048), ["service"] = RequiredString(provider, "service", 256), ["doctor"] = RequiredString(provider, "doctor", 256), ["specialty"] = RequiredString(provider, "specialty", 256), ["location"] = RequiredString(provider, "location", 256), ["date"] = RequiredString(provider, "date", 32), ["time"] = RequiredString(provider, "time", 16), ["mode"] = RequiredString(provider, "mode", 32) };
        if (provider.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number) values["price"] = price.GetDecimal();
        if (provider.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String) values["currency"] = currency.GetString();
        if (reschedule) values["oldAppointmentId"] = RequiredString(arguments, "old_appointment_id", 2048);
        if (OptionalString(arguments, "as_dependent", 256) is { } dependent) values["asDependent"] = dependent;
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement CanonicalCancellation(JsonElement appointment, JsonElement input)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["appointmentId"] = RequiredString(appointment, "id", 2048) };
        foreach (var name in new[] { "doctor", "specialty", "location", "date", "time" }) if (OptionalString(appointment, name, 256) is { } value) values[name] = value;
        if (appointment.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Array && services.GetArrayLength() > 0 && services[0].ValueKind == JsonValueKind.String) values["service"] = services[0].GetString();
        if (OptionalString(input, "asDependent", 256) is { } dependent) values["asDependent"] = dependent;
        return JsonSerializer.SerializeToElement(values);
    }

    private static bool PreflightReferencesMatch(JsonElement provider, JsonElement arguments)
        => provider.TryGetProperty("bookable", out var bookable) && bookable.ValueKind == JsonValueKind.True && SameReference(provider, "slot_receipt", arguments, "slot_receipt") && SameReference(provider, "interval_id", arguments, "interval_id") && SameReference(provider, "physician_id", arguments, "physician_id");

    private static bool PreflightMatches(JsonElement approved, JsonElement arguments, JsonElement provider)
    {
        if (!PreflightReferencesMatch(provider, arguments)) return false;
        foreach (var name in new[] { "service", "doctor", "specialty", "location", "date", "time", "mode", "currency" }) if (OptionalString(approved, name, 256) != OptionalString(provider, name, 256)) return false;
        return !approved.TryGetProperty("price", out var approvedPrice) || provider.TryGetProperty("price", out var providerPrice) && approvedPrice.GetRawText() == providerPrice.GetRawText();
    }

    private static bool SameReference(JsonElement left, string leftName, JsonElement right, string rightName) => left.TryGetProperty(leftName, out var leftValue) && leftValue.ValueKind == JsonValueKind.String && right.TryGetProperty(rightName, out var rightValue) && rightValue.ValueKind == JsonValueKind.String && leftValue.GetString() == rightValue.GetString();
    private static string? AppointmentId(JsonElement item) => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    private static string ProviderReference(string? value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException(); return value; }
    private static string RequiredString(JsonElement input, string name, int max) => OptionalString(input, name, max) ?? throw new ArgumentException();
    private static string? OptionalString(JsonElement input, string name, int max) { if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null; if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(); var text = value.GetString(); if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(character => char.IsControl(character) && character != '\t')) throw new ArgumentException(); return text; }
    private static void Only(JsonElement input, params string[] names) { if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException(); var allowed = names.ToHashSet(StringComparer.Ordinal); if (input.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new ArgumentException(); }
    private static CapabilityResult Result(ReginaMariaMcpResult result) => result.Succeeded && result.Output is { } output ? Success(output) : new(result.UnknownOutcome ? CapabilityOutcome.UnknownOutcome : CapabilityOutcome.Failed, JsonSerializer.SerializeToElement(new { }), null, null, result.ErrorCode ?? "provider_unavailable");
    private static CapabilityResult MutationFailure(ReginaMariaMcpResult result) => result.UnknownOutcome ? Unknown(null, result.ErrorCode ?? "provider_unavailable") : Failure(result.ErrorCode ?? "provider_unavailable");
    private static CapabilityResult Success(JsonElement output) => new(CapabilityOutcome.Succeeded, output, null, null, null);
    private static CapabilityResult Failure(string code) => new(CapabilityOutcome.Failed, JsonSerializer.SerializeToElement(new { }), null, null, code);
    private static CapabilityResult Unknown(string? receipt, string code) => new(CapabilityOutcome.UnknownOutcome, JsonSerializer.SerializeToElement(new { }), receipt, null, code);
}