using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Identity;
using Tessera.Persistence.Sqlite;
using Tessera.Providers;
using Tessera.Providers.R2;

namespace Tessera.Broker;

internal static class ModelGatewayEndpoints
{
    public static void MapModelGatewayEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/settings/model-gateways",async(HttpContext context,ITokenValidator validator,TesseraConfig config,CancellationToken token)=>
        {var boundary=await OwnerAsync(context,validator,config,token).ConfigureAwait(false);return boundary.Error??Results.Json(new{items=config.ModelGateways.Endpoints.Select(item=>new{id=item.Id,displayName=item.DisplayName}).ToArray()});});

        app.MapPost("/api/v1/settings/model-gateways/connect",async(HttpContext context,ConnectRequest? request,ITokenValidator validator,TesseraConfig config,IServiceProvider services,ICredentialStore custody,IHttpTransport transport,CancellationToken token)=>
        {
            var boundary=await OwnerAsync(context,validator,config,token).ConfigureAwait(false);if(boundary.Error is not null)return boundary.Error;var store=services.GetService<SqliteKernelStore>();if(store is null)return Problem(503,"product_storage_unavailable");if(request is null||string.IsNullOrWhiteSpace(request.GatewayId)||string.IsNullOrWhiteSpace(request.Model)||string.IsNullOrWhiteSpace(request.SecretInput)||request.ContextLimit is <256 or >2_000_000||custody is not ICredentialWriter writer)return Problem(422,"invalid_configuration");var gateway=config.ModelGateways.Endpoints.SingleOrDefault(item=>item.Id==request.GatewayId);if(gateway is null)return Problem(404,"gateway_unavailable");var probe=await new OpenAiCompatibleAdapter(transport).ProbeTrustedInternalAsync(gateway.Endpoint,request.SecretInput,token).ConfigureAwait(false);if(!probe.Available)return Problem(probe.ErrorCode=="provider_auth_required"?401:422,probe.ErrorCode??"provider_unavailable");if(!probe.Models.Contains(request.Model,StringComparer.Ordinal))return Problem(422,"invalid_model");var owner=boundary.Owner!;var accountId=StableId(owner,"model-account",gateway.Id);var profileId=StableId(owner,"model-profile",$"{gateway.Id}\n{request.Model}");var credentialRef=ConnectedAccountCredentialRef.Create(owner,accountId);var binding=new AccountCapabilityBinding("model-provider","1.0.0","model.chat.complete","1");var configuration=JsonSerializer.Serialize(new{endpoint=gateway.Endpoint,gatewayId=gateway.Id,pluginVersion="1.0.0"});var current=await store.GetConnectedAccountAsync(owner,accountId,token).ConfigureAwait(false);ConnectedAccount account;
            try
            {
                if(current is null)account=await new R2ConnectedAccountService(store,writer).ConnectAsync(owner,accountId,"openai-compatible","model-provider","1.0.0",gateway.DisplayName,configuration,new CredentialBundle(AccessToken:request.SecretInput),[],[binding],token).ConfigureAwait(false);else{if(current.NonSecretConfigJson!=configuration)return Problem(409,"gateway_binding_conflict");await writer.PutBundleAsync(credentialRef,new CredentialBundle(AccessToken:request.SecretInput),token).ConfigureAwait(false);account=current;}
                if(account.Lifecycle!=AccountLifecycle.Connected||account.Health!=AccountHealth.Healthy)account=await store.SetConnectedAccountStateAsync(owner,accountId,account.Version,AccountLifecycle.Connected,AccountHealth.Healthy,token).ConfigureAwait(false);var existing=await store.GetModelProfileAsync(owner,profileId,token).ConfigureAwait(false);if(existing is null){var now=DateTimeOffset.UtcNow;await store.AddModelProfileAsync(new(owner,profileId,accountId,"openai-compatible-local",gateway.Endpoint,request.Model,request.ContextLimit,true,true,true,now,now,1),token).ConfigureAwait(false);existing=await store.GetModelProfileAsync(owner,profileId,token).ConfigureAwait(false);}await store.RecomputeJobsHealthAsync(owner,token).ConfigureAwait(false);return Results.Json(existing,statusCode:201);
            }
            catch(ProductConcurrencyException){return Problem(409,"version_conflict");}
            catch(Exception exception)when(exception is StoreException or R2AccountStorageException or Microsoft.Data.Sqlite.SqliteException){return Problem(503,"storage_unavailable");}
        });
    }

    private static async Task<Boundary> OwnerAsync(HttpContext context,ITokenValidator validator,TesseraConfig config,CancellationToken token)
    {var user=await PortalEndpoints.ResolveEndUserAsync(context,validator,config).ConfigureAwait(false);if(user?.CanonicalPrincipalId is null||string.IsNullOrWhiteSpace(user.TenantId))return new(null,Problem(401,"unauthenticated"));var store=context.RequestServices.GetService<SqliteKernelStore>();if(store is null)return new(null,Problem(503,"product_storage_unavailable"));await store.AddAsync(PrincipalRef.Create(user.Issuer,user.TenantId,user.Subject,user.PreferredUsername,DateTimeOffset.UtcNow),token).ConfigureAwait(false);return new(user.CanonicalPrincipalId,null);}
    private static string StableId(string owner,string kind,string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{kind}\n{value}")));
    private static IResult Problem(int status,string code)=>Results.Problem(statusCode:status,title:code,extensions:new Dictionary<string,object?>{{"code",code}});
    private sealed record ConnectRequest(string GatewayId,string Model,string SecretInput,int ContextLimit=32768);
    private sealed record Boundary(string? Owner,IResult? Error);
}
