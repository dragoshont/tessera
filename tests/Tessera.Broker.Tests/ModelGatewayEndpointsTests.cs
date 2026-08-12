using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Kernel;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Providers;
using Tessera.Providers.R2;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class ModelGatewayEndpointsTests:IAsyncLifetime
{
    private const string Owner="owner@example.com";
    private WebApplication _app=null!;
    private HttpClient _client=null!;
    private InMemoryCredentialStore _custody=null!;
    private PublicTransport _public=null!;
    private GatewayTransport _gateway=null!;
    private string _directory=null!;

    public async Task InitializeAsync()
    {
        var port=FreePort();
        _directory=Directory.CreateTempSubdirectory("tessera-model-gateway-test").FullName;
        var configPath=Path.Combine(_directory,"tessera.json");
        await File.WriteAllTextAsync(configPath,JsonSerializer.Serialize(new{server=new{host="127.0.0.1",port},identity=new{mode="dev",trustDomain="tessera.local"},policy=new{@default="deny"},audit=new{enabled=false},modelGateways=new{enabled=true,allowPlainHttp=true,endpoints=new[]{new{id="homelab",displayName="Homelab LiteLLM",endpoint="http://litellm.default.svc.cluster.local:4000/v1"}}}}));
        var grantsPath=Path.Combine(_directory,"grants.json");
        await File.WriteAllTextAsync(grantsPath,"{\"grants\":[],\"bindings\":[],\"recipes\":[]}");
        _custody=new();_public=new();_gateway=new();
        _app=await BrokerHost.BuildAppAsync(new BrokerHostOptions{ConfigPath=configPath,PolicyPath=grantsPath,StoreOverride=_custody,TransportOverride=_public,InternalTransportOverride=_gateway,ProductDatabasePath=Path.Combine(_directory,"product.db"),PluginRoot=Path.Combine(_directory,"no-catalog")});
        await _app.StartAsync();
        _client=new(){BaseAddress=new Uri($"http://127.0.0.1:{port}")};
    }

    public async Task DisposeAsync(){_client.Dispose();await _app.DisposeAsync();try{Directory.Delete(_directory,true);}catch(IOException){}}

    [Fact]
    public async Task Fixed_gateway_creates_custodied_profile_and_routes_streaming_and_tools_internally()
    {
        var list=await SendAsync(HttpMethod.Get,"/api/v1/settings/model-gateways");
        Assert.Equal(HttpStatusCode.OK,list.StatusCode);Assert.Contains("Homelab LiteLLM",await list.Content.ReadAsStringAsync(),StringComparison.Ordinal);
        var response=await SendAsync(HttpMethod.Post,"/api/v1/settings/model-gateways/connect",new{gatewayId="homelab",model="real-model",secretInput="dedicated-key",contextLimit=65536});
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);var profile=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();Assert.Equal("real-model",profile.GetProperty("model").GetString());Assert.Equal("openai-compatible-local",profile.GetProperty("adapterKind").GetString());Assert.Equal(1,_gateway.BufferedCalls);Assert.Equal(0,_public.Calls);
        var owner=PrincipalRef.Create("https://dev.tessera.local","dev",Owner,Owner,DateTimeOffset.UtcNow).PrincipalId;var store=_app.Services.GetRequiredService<SqliteKernelStore>();var stored=await store.GetModelProfileAsync(owner,profile.GetProperty("profileId").GetString()!);Assert.NotNull(stored);var account=await store.GetConnectedAccountAsync(owner,stored.AccountId);Assert.NotNull(account);Assert.Equal("dedicated-key",(await _custody.GetBundleAsync(account.CredentialRef)).AccessToken);Assert.DoesNotContain("dedicated-key",account.NonSecretConfigJson,StringComparison.Ordinal);
        var adapter=new OpenAiCompatibleAdapter(_app.Services.GetRequiredService<IHttpTransport>());var deltas=new List<string>();var streamed=await adapter.StreamTurnTrustedInternalAsync(stored.Endpoint,"dedicated-key",stored.Model,"hello",[],(delta,_)=>{deltas.Add(delta);return ValueTask.CompletedTask;});Assert.True(streamed.Succeeded,streamed.ErrorCode);Assert.Equal("streamed answer",string.Concat(deltas));var tools=await adapter.CompleteTurnTrustedInternalAsync(stored.Endpoint,"dedicated-key",stored.Model,"use tool",[new("current_time","Time",JsonSerializer.SerializeToElement(new{type="object"}))]);Assert.True(tools.Succeeded,tools.ErrorCode);Assert.Equal("current_time",Assert.Single(tools.ToolCalls).Name);Assert.Equal(0,_public.Calls);
        var unknown=await SendAsync(HttpMethod.Post,"/api/v1/settings/model-gateways/connect",new{gatewayId="attacker",model="real-model",secretInput="key",contextLimit=65536});Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method,string path,object? body=null){var request=new HttpRequestMessage(method,path);request.Headers.Add("X-Tessera-Dev-Principal",Owner);if(body is not null)request.Content=JsonContent.Create(body);return await _client.SendAsync(request);}
    private static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private sealed class PublicTransport:IHttpTransport{public int Calls{get;private set;}public Task<TransportResponse> SendAsync(string method,string url,IReadOnlyDictionary<string,string> headers,string? body,CancellationToken cancellationToken=default){Calls++;return Task.FromResult(new TransportResponse(502,new Dictionary<string,string>(),"{}"));}}
    private sealed class GatewayTransport:IHttpTransport,IStreamingHttpTransport
    {
        public int BufferedCalls{get;private set;}
        public Task<TransportResponse> SendAsync(string method,string url,IReadOnlyDictionary<string,string> headers,string? body,CancellationToken cancellationToken=default){BufferedCalls++;if(method=="GET"&&url.EndsWith("/models",StringComparison.Ordinal))return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"{\"data\":[{\"id\":\"real-model\"}]}"));return Task.FromResult(new TransportResponse(200,new Dictionary<string,string>(),"""{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"tool-1","type":"function","function":{"name":"current_time","arguments":"{}"}}]}}]}"""));}
        public async Task<StreamingTransportResponse> SendStreamingAsync(string method,string url,IReadOnlyDictionary<string,string> headers,string? body,Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask> onChunk,int maximumResponseBytes,CancellationToken cancellationToken=default){var bytes=Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"streamed answer\"}}]}\n\ndata: [DONE]\n\n");await onChunk(bytes,cancellationToken);return new(200,new Dictionary<string,string>(),null);}
    }
}
