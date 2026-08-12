using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Identity;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;

namespace Tessera.Broker;

internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v1/setup/status",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken cancellationToken) =>
            {
                var boundary = await OwnerAsync(
                        context,
                        validator,
                        config,
                        services,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (boundary.Error is not null) return boundary.Error;
                return Results.Json(
                    await StatusAsync(
                            boundary.Owner!,
                            config,
                            services,
                            cancellationToken)
                        .ConfigureAwait(false));
            });

        app.MapPost(
            "/api/v1/setup/bootstrap",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken cancellationToken) =>
            {
                var boundary = await OwnerAsync(
                        context,
                        validator,
                        config,
                        services,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (boundary.Error is not null) return boundary.Error;
                try
                {
                    await services.GetRequiredService<ModelGatewayBootstrapService>()
                        .BootstrapAsync(boundary.Owner!, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Json(
                        await StatusAsync(
                                boundary.Owner!,
                                config,
                                services,
                                cancellationToken)
                            .ConfigureAwait(false));
                }
                catch (InvalidOperationException exception)
                {
                    return Problem(
                        exception.Message is "provider_auth_required"
                            ? StatusCodes.Status401Unauthorized
                            : StatusCodes.Status422UnprocessableEntity,
                        exception.Message);
                }
            });
    }

    private static async Task<object> StatusAsync(
        string owner,
        TesseraConfig config,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<SqliteKernelStore>();
        var accounts = await store.ListConnectedAccountsAsync(owner, cancellationToken)
            .ConfigureAwait(false);
        var plugins = await store.ListPluginInstallationsAsync(owner, cancellationToken)
            .ConfigureAwait(false);
        var model = await services.GetRequiredService<ModelGatewayBootstrapService>()
            .GetStateAsync(owner, cancellationToken)
            .ConfigureAwait(false);
        var descriptors = services.GetRequiredService<IReadOnlyList<PluginSetupDescriptor>>();
        var integrations = descriptors
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)
            .Select(descriptor =>
            {
                var installation = plugins.FirstOrDefault(plugin =>
                    plugin.PluginId == descriptor.PluginId && plugin.Enabled);
                var account = accounts
                    .Where(item => item.PluginId == descriptor.PluginId
                        && item.Lifecycle != AccountLifecycle.Revoked)
                    .OrderBy(item => AccountRank(item.Lifecycle))
                    .FirstOrDefault();
                var state = installation is null
                    ? "NOT_INSTALLED"
                    : !descriptor.RuntimeConfigured
                        ? "UNAVAILABLE"
                        : AccountState(account);
                return new
                {
                    id = descriptor.PluginId,
                    name = descriptor.DisplayName,
                    state,
                    runtimeState = installation is null
                        ? "NOT_INSTALLED"
                        : descriptor.RuntimeConfigured ? "READY" : "UNAVAILABLE",
                    accountId = account?.AccountId,
                    accountHealth = account?.Health.ToContractValue(),
                    detailCode = account is null ? descriptor.DetailCode : null,
                    connectPath = state is "READY_TO_CONNECT" or "AUTH_REQUIRED"
                        ? descriptor.ConnectPath
                        : null,
                };
            })
            .ToArray();
        var canOpenChat = model.State == "CONNECTED";
        var serverVersion = typeof(BrokerHost).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        return new
        {
            server = new
            {
                state = "CONNECTED",
                displayName = config.ServerIdentity.DisplayName,
                version = serverVersion,
            },
            ai = new
            {
                state = model.State,
                gatewayId = model.GatewayId,
                displayName = model.DisplayName,
                model = model.Model,
                profileId = model.ProfileId,
                detailCode = model.DetailCode,
            },
            integrations,
            canOpenChat,
            requiredActionCount = integrations.Count(item =>
                item.state is "READY_TO_CONNECT" or "AUTH_REQUIRED")
                + (canOpenChat ? 0 : 1),
        };
    }

    private static string AccountState(ConnectedAccount? account)
        => account?.Lifecycle switch
        {
            null => "READY_TO_CONNECT",
            AccountLifecycle.Connected => "CONNECTED",
            AccountLifecycle.AuthRequired => "AUTH_REQUIRED",
            AccountLifecycle.Degraded or AccountLifecycle.Error => "DEGRADED",
            AccountLifecycle.Disabled => "DISABLED",
            _ => "READY_TO_CONNECT",
        };

    private static int AccountRank(AccountLifecycle lifecycle)
        => lifecycle switch
        {
            AccountLifecycle.Connected => 0,
            AccountLifecycle.AuthRequired => 1,
            AccountLifecycle.Degraded => 2,
            AccountLifecycle.Error => 3,
            AccountLifecycle.Disabled => 4,
            _ => 5,
        };

    private static async Task<Boundary> OwnerAsync(
        HttpContext context,
        ITokenValidator validator,
        TesseraConfig config,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config)
            .ConfigureAwait(false);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId))
            return new(null, Problem(StatusCodes.Status401Unauthorized, "unauthenticated"));
        var store = services.GetService<SqliteKernelStore>();
        if (store is null)
            return new(
                null,
                Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    "product_storage_unavailable"));
        await store.AddAsync(
                PrincipalRef.Create(
                    user.Issuer,
                    user.TenantId,
                    user.Subject,
                    user.PreferredUsername,
                    DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        var catalog = services.GetService<R2PluginCatalog>();
        if (catalog is not null)
            await catalog.EnsureInstalledAsync(
                    store,
                    user.CanonicalPrincipalId,
                    cancellationToken)
                .ConfigureAwait(false);
        return new(user.CanonicalPrincipalId, null);
    }

    private static IResult Problem(int status, string code)
        => Results.Problem(
            statusCode: status,
            title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed record Boundary(string? Owner, IResult? Error);
}
