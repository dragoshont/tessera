using Tessera.Core.Egress;
using Tessera.Core.Recipes;
using Tessera.Core.Stores;
using Tessera.Broker.Egress;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Text;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class EgressTests
{
    [Fact]
    public void SsrfGuard_allows_only_https_allow_listed_hosts()
    {
        var guard = new SsrfGuard(["api.example.com"]);

        Assert.True(guard.IsAllowed("https://api.example.com/v1/x"));
        Assert.False(guard.IsAllowed("http://api.example.com/v1/x")); // not https
        Assert.False(guard.IsAllowed("https://evil.example.com/")); // not allow-listed
        Assert.False(guard.IsAllowed("not-a-url"));
    }

    [Fact]
    public void SsrfGuard_with_empty_allow_list_denies_everything()
    {
        var guard = new SsrfGuard([]);
        Assert.False(guard.IsAllowed("https://api.example.com/"));
    }

    [Fact]
    public void CredentialInjector_builds_bearer_and_cookie_headers()
    {
        var bearer = CredentialInjector.BuildHeaders(new CredentialBundle(AccessToken: "AT"), InjectionKind.BearerToken);
        Assert.Equal(("Authorization", "Bearer AT"), bearer[0]);

        var cookies = CredentialInjector.BuildHeaders(
            new CredentialBundle(Cookies: new Dictionary<string, string> { ["sid"] = "1", ["x"] = "2" }),
            InjectionKind.Cookies);
        Assert.Equal("Cookie", cookies[0].Name);
        Assert.Contains("sid=1", cookies[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialInjector_returns_nothing_when_material_missing()
    {
        Assert.Empty(CredentialInjector.BuildHeaders(CredentialBundle.Empty, InjectionKind.BearerToken));
        Assert.Empty(CredentialInjector.BuildHeaders(new CredentialBundle(AccessToken: "AT"), InjectionKind.None));
    }

    [Fact]
    public void CredentialInjector_builds_http_basic_from_username_and_password()
    {
        // The iCloud class: username from extra.username, password from the access token.
        var bundle = new CredentialBundle(
            AccessToken: "app-specific-pw",
            Extra: new Dictionary<string, string> { ["username"] = "me@icloud.com" });
        var headers = CredentialInjector.BuildHeaders(bundle, InjectionKind.Basic);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me@icloud.com:app-specific-pw"));
        Assert.Equal(("Authorization", expected), headers[0]);
    }

    [Fact]
    public void CredentialInjector_basic_needs_both_username_and_password()
    {
        // No username → no header (egress refuses rather than send a malformed credential).
        Assert.Empty(CredentialInjector.BuildHeaders(new CredentialBundle(AccessToken: "pw"), InjectionKind.Basic));
        // No password → no header.
        Assert.Empty(CredentialInjector.BuildHeaders(
            new CredentialBundle(Extra: new Dictionary<string, string> { ["username"] = "me@icloud.com" }),
            InjectionKind.Basic));
    }

    [Theory]
    [InlineData(false, EgressMode.Http, true, EgressDisposition.Disabled)]       // off by default
    [InlineData(true, EgressMode.None, true, EgressDisposition.NotHttpEgress)]   // recipe not http
    [InlineData(true, EgressMode.Http, false, EgressDisposition.HostNotAllowed)] // host not allow-listed
    [InlineData(true, EgressMode.Proxy, false, EgressDisposition.HostNotAllowed)] // proxy is egress-capable too
    public void Evaluate_gates_egress(bool enabled, EgressMode mode, bool hostAllowed, EgressDisposition expected)
    {
        var options = new Core.Configuration.EgressOptions
        {
            Enabled = enabled,
            AllowedHosts = hostAllowed ? ["api.example.com"] : [],
        };
        using var egress = new InjectionEgress(options, new ThrowingForwarder());
        var recipe = new Recipe("t", Egress: mode, UpstreamBaseUrl: "https://api.example.com", Injection: InjectionKind.BearerToken);

        var disposition = egress.Evaluate("https://api.example.com/v1", recipe, new CredentialBundle(AccessToken: "AT"));
        Assert.Equal(expected, disposition);
    }

    [Fact]
    public void Evaluate_reports_missing_credential()
    {
        var options = new Core.Configuration.EgressOptions { Enabled = true, AllowedHosts = ["api.example.com"] };
        using var egress = new InjectionEgress(options, new ThrowingForwarder());
        var recipe = new Recipe("t", Egress: EgressMode.Http, Injection: InjectionKind.BearerToken);

        Assert.Equal(EgressDisposition.NoCredential, egress.Evaluate("https://api.example.com/", recipe, CredentialBundle.Empty));
        Assert.Equal(EgressDisposition.Forwarded, egress.Evaluate("https://api.example.com/", recipe, new CredentialBundle(AccessToken: "AT")));
    }

    // ── Proxy forward (ADR 0022): inject Basic, strip identity, pin destination ──

    [Fact]
    public async Task Proxy_forward_injects_basic_strips_identity_and_pins_destination()
    {
        var options = new Core.Configuration.EgressOptions { Enabled = true, AllowedHosts = ["caldav.icloud.com"] };
        var forwarder = new RecordingForwarder();
        using var egress = new InjectionEgress(options, forwarder);

        var recipe = new Recipe("apple-caldav", Egress: EgressMode.Proxy, Injection: InjectionKind.Basic);
        var bundle = new CredentialBundle(
            AccessToken: "app-specific-pw",
            Extra: new Dictionary<string, string> { ["username"] = "me@icloud.com" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "PROPFIND";
        ctx.Request.Path = "/v1/egress/apple-caldav";
        ctx.Request.Headers["Authorization"] = "Bearer CALLER-APP-TOKEN";
        ctx.Request.Headers["X-Tessera-On-Behalf-Of"] = "USER-AUTHENTIK-TOKEN";
        ctx.Request.Headers["X-Tessera-Upstream"] = "https://caldav.icloud.com/123/calendars/";
        ctx.Request.Headers["X-Tessera-Confirm"] = "true";
        ctx.Request.Headers["Depth"] = "1"; // a real CalDAV protocol header — must pass through

        var upstream = new Uri("https://caldav.icloud.com/123/calendars/home/");
        var outcome = await egress.ForwardAsync(ctx, upstream, recipe, bundle);

        Assert.True(outcome.Forwarded);
        var captured = forwarder.Captured!;

        // Destination is pinned to the validated upstream (not the inbound route path).
        Assert.Equal(upstream, captured.RequestUri);

        // HTTP Basic injected from username:password — and the caller's token is gone.
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me@icloud.com:app-specific-pw"));
        Assert.Equal(expected, captured.Headers.GetValues("Authorization").Single());
        Assert.DoesNotContain("CALLER-APP-TOKEN", string.Join(" ", captured.Headers.GetValues("Authorization")), StringComparison.Ordinal);

        // Every Tessera identity header is stripped (no token/identity leak to Apple).
        Assert.False(captured.Headers.Contains("X-Tessera-On-Behalf-Of"));
        Assert.False(captured.Headers.Contains("X-Tessera-Upstream"));
        Assert.False(captured.Headers.Contains("X-Tessera-Confirm"));

        // A genuine CalDAV protocol header is preserved (denylist strips identity, not protocol).
        Assert.True(captured.Headers.Contains("Depth"));
    }
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // metadata — blocked at connect
    [InlineData("http://10.0.0.5/")]                          // RFC1918 — blocked (public-only)
    [InlineData("http://localhost:9/")]                       // name → loopback via DNS branch — blocked
    public async Task Proxy_forward_connect_pin_blocks_a_dangerous_resolved_ip(string url)
    {
        // The host is allow-listed (a literal IP here), but the connect-time PublicOnly
        // guard wired into InjectionEgress must still refuse a metadata/private address —
        // the defence-in-depth a host allow-list alone can't give (the DNS-rebind case).
        // Uses the REAL YARP forwarder so InjectionEgress's own ConnectCallback runs.
        var host = new Uri(url).Host;
        using var sp = new ServiceCollection().AddLogging().AddHttpForwarder().BuildServiceProvider();
        var forwarder = sp.GetRequiredService<Yarp.ReverseProxy.Forwarder.IHttpForwarder>();
        var options = new Core.Configuration.EgressOptions
        {
            Enabled = true,
            AllowedHosts = [host],   // host passes the allow-list…
            AllowPlainHttp = true,
        };
        using var egress = new InjectionEgress(options, forwarder);
        var recipe = new Recipe("t", Egress: EgressMode.Proxy, Injection: InjectionKind.Basic);
        var bundle = new CredentialBundle(
            AccessToken: "pw", Extra: new Dictionary<string, string> { ["username"] = "u" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Response.Body = new MemoryStream();

        var outcome = await egress.ForwardAsync(ctx, new Uri(url), recipe, bundle);

        // The guards passed the host list, but the connect was refused by the IP pin.
        Assert.Equal(EgressDisposition.Forwarded, outcome.Disposition);
        Assert.NotNull(outcome.Error);
        Assert.NotEqual(Yarp.ReverseProxy.Forwarder.ForwarderError.None, outcome.Error);
    }
    // ── Connect-time SSRF (the DNS-rebind / metadata defense) ─────────────────
    // The host allow-list runs earlier; the transport adds the last-line defense by
    // validating the *resolved* IP at connect time and pinning it. These literal-IP
    // targets are blocked before any socket is opened (no network in the test).

    [Fact]
    public async Task Transport_blocks_a_connect_to_the_cloud_metadata_ip()
    {
        using var transport = new HttpClientTransport();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(
            "GET", "http://169.254.169.254/latest/meta-data/", EmptyHeaders, null, CancellationToken.None));
        Assert.Contains("SSRF address guard", AllMessages(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_blocks_a_connect_to_loopback_by_default()
    {
        using var transport = new HttpClientTransport();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(
            "GET", "http://127.0.0.1:9/", EmptyHeaders, null, CancellationToken.None));
        Assert.Contains("SSRF address guard", AllMessages(error), StringComparison.Ordinal);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    private static string AllMessages(Exception exception)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            sb.Append(e.Message).Append(" | ");
        }

        return sb.ToString();
    }

    private sealed class ThrowingForwarder : Yarp.ReverseProxy.Forwarder.IHttpForwarder
    {
        public ValueTask<Yarp.ReverseProxy.Forwarder.ForwarderError> SendAsync(
            Microsoft.AspNetCore.Http.HttpContext context, string destinationPrefix,
            HttpMessageInvoker httpClient, Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig requestConfig,
            Yarp.ReverseProxy.Forwarder.HttpTransformer transformer) => throw new NotSupportedException();

        public ValueTask<Yarp.ReverseProxy.Forwarder.ForwarderError> SendAsync(
            Microsoft.AspNetCore.Http.HttpContext context, string destinationPrefix,
            HttpMessageInvoker httpClient, Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig requestConfig,
            Yarp.ReverseProxy.Forwarder.HttpTransformer transformer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
