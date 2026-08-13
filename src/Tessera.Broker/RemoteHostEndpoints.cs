using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Identity;
using Tessera.Persistence.Sqlite;

namespace Tessera.Broker;

public static class RemoteHostEndpoints
{
    private static readonly JsonSerializerOptions PublicJson = new(JsonSerializerDefaults.Web);

    public static void MapRemoteHostEndpoints(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/host-pairings")
                || context.Request.Path.StartsWithSegments("/api/v1/hosts"))
                context.Response.Headers.CacheControl = "no-store";
            if (IsRemoteHostMutation(context.Request)
                && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = 64 * 1024;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
                when ((context.Request.Path.StartsWithSegments("/api/v1/host-pairings")
                    || context.Request.Path.StartsWithSegments("/api/v1/hosts"))
                    && !context.Response.HasStarted)
            {
                context.Response.Clear();
                await Problem(503, "product_storage_unavailable").ExecuteAsync(context).ConfigureAwait(false);
            }
        });

        app.MapPost("/api/v1/host-pairings", async (
            HttpContext context, HostPairingCreateRequest? request,
            ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            try { RemoteHostValidation.ValidateLowerHex(request!.ClaimSecretHash, 64, nameof(request.ClaimSecretHash)); }
            catch (ArgumentException) { return Problem(400, "pairing_invalid_request"); }
            var now = DateTimeOffset.UtcNow;
            var pairingId = $"pairing-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
            var expiresAt = now.Add(RemoteHostValidation.MaximumPairingTtl);
            var requestHash = Hash(request);
            var result = await boundary.Store!.CreateHostPairingAsync(
                boundary.Owner!, pairingId, request.ClaimSecretHash, key, requestHash,
                now, expiresAt, token).ConfigureAwait(false);
            if (result.Receipt is null) return Problem(409, result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt!.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });

        app.MapGet("/api/v1/host-pairings/{pairingId}", async (
            HttpContext context, string pairingId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var pairing = await boundary.Store!.GetHostPairingAsync(boundary.Owner!, pairingId, token).ConfigureAwait(false);
            if (pairing is null) return Problem(404, "pairing_not_found");
            NoStore(context);
            return Results.Json(PairingDto(pairing));
        });

        app.MapPost("/api/v1/host-pairings/{pairingId}/claim", async (
            HttpContext context, string pairingId, HostClaimRequest? request,
            SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            var requestHash = Hash(request!);
            try
            {
                var publicKey = RemoteHostValidation.NormalizeP256PublicJwk(request!.PublicKeyJwk.GetRawText());
                var claim = new HostClaim(publicKey, request.Protection, request.Platform, request.Architecture,
                    request.AgentVersion, request.ProtocolVersion,
                    request.RequestedCapabilities.Select(item => new RequestedHostCapability(
                        item.CapabilityId, item.CapabilityVersion, item.SchemaHash, item.SideEffectClass)).ToArray(),
                    request.RequestedResources.Select(item => new RequestedHostResource(
                        item.ResourceId, item.Type, item.DisplayName, item.Fingerprint, item.State)).ToArray());
                var result = await store.ClaimHostPairingAsync(
                    pairingId, request.ClaimSecret, claim, key, requestHash, DateTimeOffset.UtcNow, token)
                    .ConfigureAwait(false);
                if (result.Receipt is null) return PairingProblem(result.Error!);
                NoStore(context);
                return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    result.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "pairing_invalid_request"); }
            catch (JsonException) { return Problem(400, "pairing_invalid_request"); }
        });

        app.MapPost("/api/v1/host-pairings/{pairingId}/confirm", async (
            HttpContext context, string pairingId, HostConfirmRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            var requestHash = Hash(request!);
            try
            {
                var hostId = $"host-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
                var result = await boundary.Store!.ConfirmHostPairingAsync(
                    boundary.Owner!, pairingId, request!.ExpectedVersion, request.ConfirmationCode,
                    hostId, request.DisplayName,
                    request.CapabilityGrants.Select(item => new HostCapabilityGrantRequest(item.CapabilityId, item.CapabilityVersion)).ToArray(),
                    request.ResourceGrants.Select(item => new HostResourceGrantRequest(item.ResourceId, item.AccessMode)).ToArray(),
                    key, requestHash, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                if (result.Receipt is null) return PairingProblem(result.Error!);
                NoStore(context);
                return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    result.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "pairing_invalid_request"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return Problem(503, "product_storage_unavailable"); }
        });

        app.MapPost("/api/v1/host-pairings/{pairingId}/cancel", async (
            HttpContext context, string pairingId, VersionRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            var requestHash = Hash(request!);
            var result = await boundary.Store!.CancelHostPairingAsync(
                boundary.Owner!, pairingId, request!.ExpectedVersion, key, requestHash,
                DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (result.Receipt is null) return PairingProblem(result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });

        app.MapGet("/api/v1/hosts", async (
            HttpContext context, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var hosts = await boundary.Store!.ListRemoteHostsAsync(boundary.Owner!, token).ConfigureAwait(false);
            NoStore(context);
            return Results.Json(new { items = hosts.Select(HostSummaryDto).ToArray(), nextCursor = (string?)null });
        });

        app.MapGet("/api/v1/hosts/{hostId}", async (
            HttpContext context, string hostId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var host = await boundary.Store!.GetRemoteHostDetailAsync(boundary.Owner!, hostId, token).ConfigureAwait(false);
            if (host is null) return Problem(404, "host_not_found");
            NoStore(context);
            return Results.Json(HostDetailDto(host));
        });

        app.MapPut("/api/v1/hosts/{hostId}/grants", async (
            HttpContext context, string hostId, HostGrantsRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "host_invalid_request");
            var requestHash = Hash(request!);
            try
            {
                var result = await boundary.Store!.UpdateRemoteHostGrantsAsync(
                    boundary.Owner!, hostId, request!.ExpectedVersion,
                    request.CapabilityGrants.Select(item => new HostCapabilityGrantRequest(item.CapabilityId, item.CapabilityVersion)).ToArray(),
                    request.ResourceGrants.Select(item => new HostResourceGrantRequest(item.ResourceId, item.AccessMode)).ToArray(),
                    key, requestHash, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                if (result.Receipt is null) return HostProblem(result.Error!);
                NoStore(context);
                return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    result.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "host_invalid_request"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return Problem(503, "product_storage_unavailable"); }
        });

        app.MapPost("/api/v1/hosts/{hostId}/revoke", async (
            HttpContext context, string hostId, VersionRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "host_invalid_request");
            var requestHash = Hash(request!);
            var result = await boundary.Store!.RevokeRemoteHostAsync(
                boundary.Owner!, hostId, request!.ExpectedVersion, key, requestHash,
                DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (result.Receipt is null) return HostProblem(result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });
    }

    private static async Task<RemoteBoundary> BoundaryAsync(
        HttpContext context, ITokenValidator validator, TesseraConfig config,
        IServiceProvider services, CancellationToken token)
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config).ConfigureAwait(false);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId))
            return new(null, null, Problem(401, "unauthenticated"));
        var store = services.GetService<SqliteKernelStore>();
        if (store is null) return new(null, null, Problem(503, "product_storage_unavailable"));
        var principal = PrincipalRef.Create(user.Issuer, user.TenantId, user.Subject,
            user.PreferredUsername, DateTimeOffset.UtcNow);
        await store.AddAsync(principal, token).ConfigureAwait(false);
        return new(store, principal.PrincipalId, null);
    }

    private static object PairingDto(HostPairing pairing) => new
    {
        pairingId = pairing.PairingId,
        state = pairing.State,
        requestedHost = pairing.RequestedHost is null ? null : new
        {
            protection = pairing.RequestedHost.Protection,
            platform = pairing.RequestedHost.Platform,
            architecture = pairing.RequestedHost.Architecture,
            agentVersion = pairing.RequestedHost.AgentVersion,
            protocolVersion = pairing.RequestedHost.ProtocolVersion,
            requestedCapabilities = pairing.RequestedHost.RequestedCapabilities,
            requestedResources = pairing.RequestedHost.RequestedResources,
        },
        expiresAt = pairing.ExpiresAt,
        version = pairing.Version,
    };

    private static object HostSummaryDto(RemoteHost host) => new
    {
        hostId = host.HostId,
        displayName = host.DisplayName,
        platform = host.Platform,
        architecture = host.Architecture,
        lifecycle = host.Lifecycle,
        connectionStatus = host.ConnectionStatus,
        agentVersion = host.AgentVersion,
        protocolVersion = host.ProtocolVersion,
        lastSeenAt = host.LastSeenAt,
        pairedAt = host.PairedAt,
        revokedAt = host.RevokedAt,
        version = host.Version,
    };

    private static object HostDetailDto(RemoteHostDetail detail) => new
    {
        host = HostSummaryDto(detail.Host),
        capabilities = detail.Capabilities.Select(item => new
        {
            item.CapabilityId, item.CapabilityVersion, item.SchemaHash, item.SideEffectClass, item.AdvertisedAt,
        }).ToArray(),
        capabilityGrants = detail.CapabilityGrants.Select(item => new
        {
            item.CapabilityId, item.CapabilityVersion, item.GrantedAt, item.RevokedAt, item.Version,
        }).ToArray(),
        resources = detail.Resources.Select(item => new
        {
            item.ResourceId, item.Type, item.DisplayName, item.Fingerprint, item.State, item.AdvertisedAt, item.Version,
        }).ToArray(),
        resourceGrants = detail.ResourceGrants.Select(item => new
        {
            item.ResourceId, item.AccessMode, item.GrantedAt, item.RevokedAt, item.Version,
        }).ToArray(),
    };

    private static bool IsRemoteHostMutation(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
            && (request.Path.StartsWithSegments("/api/v1/host-pairings")
                || request.Path.StartsWithSegments("/api/v1/hosts"))
            || HttpMethods.IsPut(request.Method)
            && request.Path.StartsWithSegments("/api/v1/hosts");

    private static IResult PairingProblem(string code) => Problem(code switch
    {
        "pairing_not_found" => 404,
        "pairing_invalid_request" => 400,
        "pairing_expired" or "pairing_canceled" or "pairing_consumed" or
        "pairing_attempts_exceeded" or "pairing_confirmation_mismatch" or
        "pairing_grant_not_requested" or "pairing_version_conflict" or
        "idempotency_conflict" => 409,
        _ => 400,
    }, code);

    private static IResult HostProblem(string code) => Problem(code switch
    {
        "host_not_found" => 404,
        "host_version_conflict" or "host_revoked" or "host_grant_not_advertised" or
        "idempotency_conflict" => 409,
        _ => 400,
    }, code);

    private static string Hash<T>(T request)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, PublicJson)));

    private static string? IdempotencyKey(HttpContext context)
    {
        var value = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        try { RemoteHostValidation.ValidateIdentifier(value ?? string.Empty, "Idempotency-Key"); return value; }
        catch (ArgumentException) { return null; }
    }

    private static bool IsJson(HttpContext context)
        => context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static bool Valid(HostClaimRequest? request)
        => ValidRequest(request)
            && request!.PublicKeyJwk.ValueKind == JsonValueKind.Object
            && request.RequestedCapabilities is not null
            && request.RequestedResources is not null
            && request.RequestedCapabilities.All(ValidRequest)
            && request.RequestedResources.All(ValidRequest);

    private static bool Valid(HostPairingCreateRequest? request)
        => ValidRequest(request) && !string.IsNullOrWhiteSpace(request!.ClaimSecretHash);

    private static bool Valid(HostConfirmRequest? request)
        => ValidRequest(request)
            && request!.ExpectedVersion >= 1
            && request.CapabilityGrants is not null
            && request.ResourceGrants is not null
            && request.CapabilityGrants.All(ValidRequest)
            && request.ResourceGrants.All(ValidRequest);

    private static bool Valid(HostGrantsRequest? request)
        => ValidRequest(request)
            && request!.ExpectedVersion >= 1
            && request.CapabilityGrants is not null
            && request.ResourceGrants is not null
            && request.CapabilityGrants.All(ValidRequest)
            && request.ResourceGrants.All(ValidRequest);

    private static bool Valid(VersionRequest? request) => ValidRequest(request) && request!.ExpectedVersion >= 1;

    private static bool ValidRequest(StrictRequest? request)
        => request is not null && request.ExtensionData?.Count is not > 0;

    private static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private static IResult Problem(int status, string code)
        => Results.Problem(statusCode: status, title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed record RemoteBoundary(SqliteKernelStore? Store, string? Owner, IResult? Error);

    public abstract class StrictRequest
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }

    public sealed class HostClaimRequest : StrictRequest
    {
        public string ClaimSecret { get; init; } = "";
        public JsonElement PublicKeyJwk { get; init; }
        public string Protection { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Architecture { get; init; } = "";
        public string AgentVersion { get; init; } = "";
        public string ProtocolVersion { get; init; } = "";
        public CapabilityAdvertisementRequest[] RequestedCapabilities { get; init; } = [];
        public ResourceAdvertisementRequest[] RequestedResources { get; init; } = [];
    }

    public sealed class HostPairingCreateRequest : StrictRequest
    {
        public string ClaimSecretHash { get; init; } = "";
    }

    public sealed class HostConfirmRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
        public string ConfirmationCode { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public CapabilityGrantDto[] CapabilityGrants { get; init; } = [];
        public ResourceGrantDto[] ResourceGrants { get; init; } = [];
    }

    public sealed class HostGrantsRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
        public CapabilityGrantDto[] CapabilityGrants { get; init; } = [];
        public ResourceGrantDto[] ResourceGrants { get; init; } = [];
    }

    public sealed class VersionRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
    }

    public sealed class CapabilityAdvertisementRequest : StrictRequest
    {
        public string CapabilityId { get; init; } = "";
        public string CapabilityVersion { get; init; } = "";
        public string SchemaHash { get; init; } = "";
        public string SideEffectClass { get; init; } = "";
    }

    public sealed class ResourceAdvertisementRequest : StrictRequest
    {
        public string ResourceId { get; init; } = "";
        public string Type { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public string State { get; init; } = "";
    }

    public sealed class CapabilityGrantDto : StrictRequest
    {
        public string CapabilityId { get; init; } = "";
        public string CapabilityVersion { get; init; } = "";
    }

    public sealed class ResourceGrantDto : StrictRequest
    {
        public string ResourceId { get; init; } = "";
        public string AccessMode { get; init; } = "";
    }
}