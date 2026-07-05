using System.Net;
using System.Text;
using Tessera.Broker.Egress;
using Tessera.Core.Egress;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// Unit tests for the provider HTTP transport. The key invariant (a CI guard for the bug the
/// conformance run caught): a form POST — the OAuth token exchange — must reach the upstream as
/// <c>application/x-www-form-urlencoded</c>, not silently rewritten to JSON (which made the AS
/// unable to parse <c>grant_type</c>). Uses a loopback echo server + a loopback-permitting guard.
/// </summary>
public sealed class HttpClientTransportTests
{
    [Fact]
    public async Task Sends_the_callers_content_type_not_forced_json()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var contentType = ctx.Request.ContentType;
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
            return (contentType, body);
        });

        using var transport = new HttpClientTransport(new AddressGuard(allowLoopback: true));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept"] = "application/json",
        };
        var resp = await transport.SendAsync("POST", $"http://127.0.0.1:{port}/", headers, "grant_type=refresh_token&x=1");

        Assert.Equal(200, resp.Status);
        var (receivedContentType, receivedBody) = await serverTask;
        Assert.StartsWith("application/x-www-form-urlencoded", receivedContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grant_type=refresh_token", receivedBody, StringComparison.Ordinal);
        listener.Stop();
    }

    [Fact]
    public async Task Defaults_to_json_when_no_content_type_is_given()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var contentType = ctx.Request.ContentType;
            var bytes = Encoding.UTF8.GetBytes("{}");
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
            return contentType;
        });

        using var transport = new HttpClientTransport(new AddressGuard(allowLoopback: true));
        await transport.SendAsync("POST", $"http://127.0.0.1:{port}/",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "{\"a\":1}");

        var receivedContentType = await serverTask;
        Assert.StartsWith("application/json", receivedContentType, StringComparison.OrdinalIgnoreCase);
        listener.Stop();
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
