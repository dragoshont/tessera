using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Identity;
using Tessera.Persistence.Sqlite;

namespace Tessera.Broker;

internal static class IntegrationCatalogEndpoints
{
    public static void MapIntegrationCatalogEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v1/integrations/sources",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services) =>
            {
                var boundary = await OwnerAsync(context, validator, config, services, context.RequestAborted)
                    .ConfigureAwait(false);
                return boundary.Error
                    ?? Results.Json(new
                    {
                        items = services.GetRequiredService<IntegrationCatalogService>()
                            .ListSources(),
                    });
            });

        app.MapGet(
            "/api/v1/integrations/search",
            async (
                HttpContext context,
                string? query,
                int? limit,
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
                    var store = services.GetRequiredService<SqliteKernelStore>();
                    var installed = (await store.ListPluginInstallationsAsync(
                                boundary.Owner!,
                                cancellationToken)
                            .ConfigureAwait(false))
                        .Select(plugin => $"{plugin.PluginId}@{plugin.PluginVersion}")
                        .ToHashSet(StringComparer.Ordinal);
                    return Results.Json(
                        await services.GetRequiredService<IntegrationCatalogService>()
                            .SearchAsync(
                                query ?? string.Empty,
                                limit ?? 20,
                                installed,
                                cancellationToken)
                            .ConfigureAwait(false));
                }
                catch (ArgumentException)
                {
                    return Problem(StatusCodes.Status400BadRequest, "invalid_query");
                }
            });

        app.MapPost(
            "/api/v1/integrations/local/{id}/versions/{version}/install",
            async (
                HttpContext context,
                string id,
                string version,
                JsonElement? request,
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
                if (request is not { ValueKind: JsonValueKind.Object }
                    || request.Value.EnumerateObject().Any())
                    return Problem(StatusCodes.Status400BadRequest, "invalid_request");
                if (!TryIdempotencyKey(context, out var idempotencyKey))
                    return Problem(StatusCodes.Status400BadRequest, "invalid_idempotency_key");
                try
                {
                    var result = await services.GetRequiredService<R2PluginCatalog>()
                        .InstallIdempotentAsync(
                            services.GetRequiredService<SqliteKernelStore>(),
                            boundary.Owner!,
                            idempotencyKey!,
                            id,
                            version,
                            cancellationToken)
                        .ConfigureAwait(false);
                    context.Response.Headers["Idempotency-Replayed"] = result.Replayed
                        ? "true"
                        : "false";
                    return Results.Content(
                        result.ResponseBodyJson,
                        "application/json",
                        statusCode: result.StatusCode);
                }
                catch (KeyNotFoundException)
                {
                    return Problem(StatusCodes.Status404NotFound, "reviewed_package_not_found");
                }
                catch (InvalidOperationException exception)
                {
                    return Problem(StatusCodes.Status409Conflict, exception.Message);
                }
            });
    }

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
        await PrincipalRegistration.RegisterForMutationAsync(
                context,
                store,
                PrincipalRef.Create(
                    user.Issuer,
                    user.TenantId,
                    user.Subject,
                    user.PreferredUsername,
                    DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        return new(user.CanonicalPrincipalId, null);
    }

    private static IResult Problem(int status, string code)
        => Results.Problem(
            statusCode: status,
            title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool TryIdempotencyKey(HttpContext context, out string? key)
    {
        key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        return key is { Length: > 0 and <= 128 }
            && key.All(character => character is >= '!' and <= '~');
    }

    private sealed record Boundary(string? Owner, IResult? Error);
}
