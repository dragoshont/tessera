using Tessera.Core.Recipes;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Providers.Tests;

public sealed class SessionRefresherTests
{
    private static Recipe Recipe() => new(
        "portal",
        Egress: EgressMode.Http,
        UpstreamBaseUrl: "https://api.example.com/v1",
        Injection: InjectionKind.Cookies);

    private static RefreshSpec Spec() => new("refresh", "POST", "access_token", "refresh_token", AbsorbSetCookie: true);

    private static CredentialBundle Current() => new(
        AccessToken: "OLD_AT",
        RefreshToken: "OLD_RT",
        Cookies: new Dictionary<string, string> { ["session"] = "OLD" });

    [Fact]
    public async Task Rotates_and_writes_back_new_tokens()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\",\"refresh_token\":\"NEW_RT\"}");
        var writer = new CapturingWriter();
        var refresher = new SessionRefresher(transport, writer);

        var result = await refresher.RefreshAsync(Recipe(), Spec(), "portal-alice", Current());

        Assert.Equal(RefreshStatus.Rotated, result.Status);
        Assert.Equal("portal-alice", writer.LastName);
        Assert.Equal("NEW_AT", writer.LastBundle!.AccessToken);
        Assert.Equal("NEW_RT", writer.LastBundle!.RefreshToken);
    }

    [Fact]
    public async Task Dead_refresh_token_is_reported_not_relogged_in()
    {
        var transport = new FakeTransport(401, "unauthorized");
        var writer = new CapturingWriter();
        var refresher = new SessionRefresher(transport, writer);

        var result = await refresher.RefreshAsync(Recipe(), Spec(), "portal-alice", Current());

        Assert.Equal(RefreshStatus.Dead, result.Status);
        Assert.Null(writer.LastBundle); // never wrote — and never tried to log in
    }

    [Fact]
    public async Task Not_configured_when_no_spec()
    {
        var refresher = new SessionRefresher(new FakeTransport(), new CapturingWriter());
        var result = await refresher.RefreshAsync(Recipe(), spec: null, "portal-alice", Current());
        Assert.Equal(RefreshStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task Absorbs_rotated_cookies_from_set_cookie()
    {
        var transport = new SetCookieTransport("session=NEW; Path=/; HttpOnly");
        var writer = new CapturingWriter();
        var refresher = new SessionRefresher(transport, writer);

        var result = await refresher.RefreshAsync(Recipe(), Spec(), "portal-alice", Current());

        Assert.Equal(RefreshStatus.Rotated, result.Status);
        Assert.Equal("NEW", writer.LastBundle!.Cookies!["session"]);
    }

    // ── OAuth absolute token URL + SSRF guard (F3) ────────────────────────────

    [Fact]
    public async Task Uses_an_absolute_token_url_on_a_different_host()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\"}");
        var writer = new CapturingWriter();
        // Graph: data API graph.microsoft.com, token endpoint login.microsoftonline.com.
        var guard = new Tessera.Core.Egress.SsrfGuard(["graph.microsoft.com", "login.microsoftonline.com"]);
        var refresher = new SessionRefresher(transport, writer, guard);
        var recipe = new Recipe("graph", Egress: EgressMode.Http, UpstreamBaseUrl: "https://graph.microsoft.com/v1.0", Injection: InjectionKind.BearerToken);
        var spec = new RefreshSpec("ignored", TokenUrl: "https://login.microsoftonline.com/common/oauth2/v2.0/token");

        var result = await refresher.RefreshAsync(recipe, spec, "graph-alice", new CredentialBundle(AccessToken: "OLD", RefreshToken: "RT"));

        Assert.Equal(RefreshStatus.Rotated, result.Status);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", transport.LastUrl);
    }

    [Fact]
    public async Task Refuses_a_refresh_url_off_the_ssrf_allow_list()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW\"}");
        var writer = new CapturingWriter();
        // The data host is allowed, but the token URL points elsewhere — refuse.
        var guard = new Tessera.Core.Egress.SsrfGuard(["graph.microsoft.com"]);
        var refresher = new SessionRefresher(transport, writer, guard);
        var recipe = new Recipe("graph", Egress: EgressMode.Http, UpstreamBaseUrl: "https://graph.microsoft.com/v1.0", Injection: InjectionKind.BearerToken);
        var spec = new RefreshSpec("ignored", TokenUrl: "https://evil.example.com/token");

        var result = await refresher.RefreshAsync(recipe, spec, "graph-alice", new CredentialBundle(AccessToken: "OLD", RefreshToken: "RT"));

        Assert.Equal(RefreshStatus.Error, result.Status);
        Assert.Equal(0, transport.Calls);
        Assert.Null(writer.LastBundle);
    }

    private sealed class CapturingWriter : ICredentialWriter
    {
        public string? LastName { get; private set; }
        public CredentialBundle? LastBundle { get; private set; }

        public Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
        {
            LastName = name;
            LastBundle = bundle;
            return Task.CompletedTask;
        }
    }

    private sealed class SetCookieTransport : IHttpTransport
    {
        private readonly string _setCookie;
        public SetCookieTransport(string setCookie) => _setCookie = setCookie;

        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportResponse(200, new Dictionary<string, string> { ["Set-Cookie"] = _setCookie }, ""));
    }
}
