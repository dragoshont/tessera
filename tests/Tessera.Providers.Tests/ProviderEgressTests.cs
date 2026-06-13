using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Providers.Tests;

public sealed class ProviderEgressTests
{
    private const string Caller = "chat://librechat";
    private const string User = "alice@example.com";
    private const string Target = "portal";
    private const string Host = "api.example.com";

    private static Recipe PortalRecipe() => new(
        Target: Target,
        Egress: EgressMode.Http,
        UpstreamBaseUrl: $"https://{Host}/v1",
        Injection: InjectionKind.Cookies,
        Tools:
        [
            new RecipeTool("portal_list_items", "GET", "items", "read:items", StepUp: false, "List items"),
            new RecipeTool("portal_book", "POST", "book", "write:book", StepUp: true, "Book (write)"),
        ],
        ExtraHeaders: new Dictionary<string, string> { ["X-Api-Key"] = "{extra:apiKey}" });

    private static (ProviderEgress Egress, FakeTransport Transport) Build(
        FakeTransport? transport = null,
        bool grantWrite = false,
        bool seedCredential = true)
    {
        var store = new InMemoryCredentialStore();
        if (seedCredential)
        {
            store.Put("portal-alice", new CredentialBundle(
                Cookies: new Dictionary<string, string> { ["session"] = "COOKIEVALUE" },
                Extra: new Dictionary<string, string> { ["apiKey"] = "SECRETKEY" }));
        }

        var grants = new List<Grant>
        {
            new(Caller, Target, ["read:*"], User),
        };
        if (grantWrite)
        {
            grants.Add(new Grant(Caller, Target, ["write:*"], User, StepUpActions: ["write:*"]));
        }

        var pdp = new PolicyDecisionPoint(grants);
        var resolver = new CredentialResolver(
            [new TargetBinding(Target, "portal-alice", User)], store);
        var t = transport ?? new FakeTransport();
        var egress = new ProviderEgress(
            new PolicyDecisionPointAdapter(pdp.Evaluate),
            resolver,
            [PortalRecipe()],
            new SsrfGuard([Host]),
            t);
        return (egress, t);
    }

    private static CallerIdentity ChatCaller() => new(Caller, VerificationMethod.Network);
    private static EndUserAssertion Alice() => new(User, "https://issuer/v2.0", VerificationMethod.OidcJwt, User);

    [Fact]
    public async Task Read_tool_injects_cookie_and_returns_result()
    {
        var (egress, transport) = Build();
        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.True(result.Ok);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("GET", transport.LastMethod);
        Assert.Equal("https://api.example.com/v1/items", transport.LastUrl);
        // The cookie was injected; the API-key placeholder resolved from the bundle extra.
        Assert.Equal("session=COOKIEVALUE", transport.LastHeaders!["Cookie"]);
        Assert.Equal("SECRETKEY", transport.LastHeaders!["X-Api-Key"]);
    }

    [Fact]
    public async Task Write_tool_requires_confirmation_and_does_not_call_upstream()
    {
        var (egress, transport) = Build(grantWrite: true);
        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_book", "{}", confirmed: false);

        Assert.Equal(ProviderCallStatus.StepUpRequired, result.Status);
        Assert.Equal(0, transport.Calls); // NEVER called the provider without confirmation
    }

    [Fact]
    public async Task Write_tool_proceeds_when_confirmed()
    {
        var (egress, transport) = Build(grantWrite: true);
        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_book", "{\"slot\":1}", confirmed: true);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("POST", transport.LastMethod);
        Assert.Equal("{\"slot\":1}", transport.LastBody);
    }

    [Fact]
    public async Task Denied_when_no_grant_for_the_user()
    {
        var (egress, transport) = Build();
        var bob = new EndUserAssertion("bob@example.com", "https://issuer/v2.0", VerificationMethod.OidcJwt, "bob@example.com");
        var result = await egress.CallAsync(ChatCaller(), bob, Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Denied, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task No_credential_when_bundle_missing()
    {
        var (egress, transport) = Build(seedCredential: false);
        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.NoCredential, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Unknown_tool_is_not_allowed()
    {
        var (egress, transport) = Build();
        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_delete_everything", null, confirmed: true);

        Assert.Equal(ProviderCallStatus.NotAllowed, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Host_off_the_allow_list_is_refused()
    {
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", new CredentialBundle(Cookies: new Dictionary<string, string> { ["s"] = "1" }));
        var pdp = new PolicyDecisionPoint([new Grant(Caller, Target, ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding(Target, "portal-alice", User)], store);
        var transport = new FakeTransport();
        // Recipe points at evil.example.com, but the SSRF guard only allows api.example.com.
        var recipe = new Recipe(Target, Egress: EgressMode.Http, UpstreamBaseUrl: "https://evil.example.com",
            Injection: InjectionKind.Cookies, Tools: [new RecipeTool("portal_list_items", "GET", "items", "read:items")]);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);

        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);
        Assert.Equal(ProviderCallStatus.NotAllowed, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Cookie_map_builds_named_cookies_from_token_fields()
    {
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", new CredentialBundle(AccessToken: "AT123", RefreshToken: "RT456"));
        var pdp = new PolicyDecisionPoint([new Grant(Caller, Target, ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding(Target, "portal-alice", User)], store);
        var transport = new FakeTransport();
        var recipe = new Recipe(Target, Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/v1",
            Injection: InjectionKind.Cookies,
            Tools: [new RecipeTool("portal_list_items", "GET", "items", "read:items")],
            CookieMap: new Dictionary<string, string>
            {
                ["TokenSSO"] = "access_token",
                ["RefreshTokenSSO"] = "refresh_token",
            });
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);

        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal("TokenSSO=AT123; RefreshTokenSSO=RT456", transport.LastHeaders!["Cookie"]);
    }

    [Fact]
    public async Task Cookie_map_refuses_when_a_named_source_is_missing()
    {
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", new CredentialBundle(AccessToken: "AT123")); // no refresh token
        var pdp = new PolicyDecisionPoint([new Grant(Caller, Target, ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding(Target, "portal-alice", User)], store);
        var transport = new FakeTransport();
        var recipe = new Recipe(Target, Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/v1",
            Injection: InjectionKind.Cookies,
            Tools: [new RecipeTool("portal_list_items", "GET", "items", "read:items")],
            CookieMap: new Dictionary<string, string>
            {
                ["TokenSSO"] = "access_token",
                ["RefreshTokenSSO"] = "refresh_token",
            });
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);

        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.NoCredential, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Env_placeholder_resolves_provider_wide_header()
    {
        Environment.SetEnvironmentVariable("TESSERA_TEST_SUBKEY", "SUBKEY789");
        try
        {
            var store = new InMemoryCredentialStore();
            store.Put("portal-alice", new CredentialBundle(Cookies: new Dictionary<string, string> { ["s"] = "1" }));
            var pdp = new PolicyDecisionPoint([new Grant(Caller, Target, ["read:*"], User)]);
            var resolver = new CredentialResolver([new TargetBinding(Target, "portal-alice", User)], store);
            var transport = new FakeTransport();
            var recipe = new Recipe(Target, Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/v1",
                Injection: InjectionKind.Cookies,
                Tools: [new RecipeTool("portal_list_items", "GET", "items", "read:items")],
                ExtraHeaders: new Dictionary<string, string> { ["Ocp-Apim-Subscription-Key"] = "{env:TESSERA_TEST_SUBKEY}" });
            var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);

            var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

            Assert.Equal(ProviderCallStatus.Completed, result.Status);
            Assert.Equal("SUBKEY789", transport.LastHeaders!["Ocp-Apim-Subscription-Key"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERA_TEST_SUBKEY", null);
        }
    }
}
