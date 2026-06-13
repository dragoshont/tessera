using Tessera.Core.Recipes;
using Tessera.Core.Stores;
using Tessera.Broker.Egress;
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

    [Theory]
    [InlineData(false, EgressMode.Http, true, EgressDisposition.Disabled)]       // off by default
    [InlineData(true, EgressMode.None, true, EgressDisposition.NotHttpEgress)]   // recipe not http
    [InlineData(true, EgressMode.Http, false, EgressDisposition.HostNotAllowed)] // host not allow-listed
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
