using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Results;
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

    // ── Result-class enforcement (service-access spec §"Output classes") ───────

    private static (ProviderEgress Egress, FakeTransport Transport) BuildMail(string body = "{\"ok\":true}")
    {
        var store = new InMemoryCredentialStore();
        store.Put("mail-alice", new CredentialBundle(AccessToken: "AT"));
        var pdp = new PolicyDecisionPoint([new Grant(Caller, "gmail", ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding("gmail", "mail-alice", User)], store);
        var transport = new FakeTransport(200, body);
        var recipe = new Recipe("gmail", Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/v1",
            Injection: InjectionKind.BearerToken,
            Tools:
            [
                new RecipeTool("mail_search", "GET", "messages", "read:mail.metadata", OutputClass: ResultClass.Metadata),
                new RecipeTool("mail_read", "GET", "messages/{handle}", "read:mail.body", OutputClass: ResultClass.FullBody),
            ]);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport, metadataMaxBytes: 16);
        return (egress, transport);
    }

    [Fact]
    public async Task A_full_body_tool_requires_a_handle_and_does_not_call_upstream_without_one()
    {
        var (egress, transport) = BuildMail();
        var result = await egress.CallAsync(ChatCaller(), Alice(), "gmail", "mail_read", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.BadRequest, result.Status);
        Assert.Equal(0, transport.Calls); // never reached upstream — no bulk read
        Assert.Contains("handle", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_body_tool_reads_by_a_target_scoped_handle()
    {
        var (egress, transport) = BuildMail("the full body");
        var result = await egress.CallAsync(ChatCaller(), Alice(), "gmail", "mail_read", "{\"handle\":\"gmail:msg-42\"}", confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal($"https://{Host}/v1/messages/msg-42", transport.LastUrl);
        Assert.Equal(ResultClass.FullBody, result.OutputClass);
    }

    [Fact]
    public async Task A_handle_from_another_provider_is_rejected()
    {
        var (egress, transport) = BuildMail();
        // A handle minted for a different target must not be replayed against gmail.
        var result = await egress.CallAsync(ChatCaller(), Alice(), "gmail", "mail_read", "{\"handle\":\"graph-mail:msg-1\"}", confirmed: false);

        Assert.Equal(ProviderCallStatus.BadRequest, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task A_handle_is_url_encoded_into_the_path_no_injection()
    {
        var (egress, transport) = BuildMail();
        var result = await egress.CallAsync(ChatCaller(), Alice(), "gmail", "mail_read", "{\"handle\":\"gmail:a/b?c\"}", confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        // The slash + query chars are percent-encoded so they can't escape the path segment.
        Assert.Equal($"https://{Host}/v1/messages/a%2Fb%3Fc", transport.LastUrl);
    }

    [Fact]
    public async Task A_metadata_result_is_capped_tighter_than_a_full_body()
    {
        var big = new string('x', 1000);
        var (egress, _) = BuildMail(big);
        var result = await egress.CallAsync(ChatCaller(), Alice(), "gmail", "mail_search", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal(ResultClass.Metadata, result.OutputClass);
        Assert.Equal(16, result.Body!.Length); // capped at metadataMaxBytes, not the 1MB body cap
    }

    // ── Service-owned shared-key fallback end-to-end (ADR 0020, F10) ───────────

    private static (ProviderEgress Egress, FakeTransport Transport) BuildSharedKey()
    {
        // A media provider backed by a SHARED service key (principal: null). bob is
        // granted; mallory is not. The key must reach a granted user via the
        // fallback and NEVER reach an ungranted one (the PDP gates before resolve).
        var store = new InMemoryCredentialStore();
        store.Put("seerr-key", new CredentialBundle(AccessToken: "SHARED-HOUSEHOLD-KEY"));
        var grants = new[] { new Grant(Caller, "seerr", ["read:*", "use:request"], "bob@example.com") };
        var pdp = new PolicyDecisionPoint(grants);
        var resolver = new CredentialResolver(
            [new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service)], store);
        var transport = new FakeTransport(200, "{\"ok\":true}");
        var recipe = new Recipe("seerr", Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/v1",
            Injection: InjectionKind.BearerToken,
            Tools: [new RecipeTool("seerr_requests", "GET", "request", "read:requests")]);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);
        return (egress, transport);
    }

    [Fact]
    public async Task A_granted_user_reaches_the_shared_service_key()
    {
        var (egress, transport) = BuildSharedKey();
        var bob = new EndUserAssertion("bob@example.com", "https://issuer/v2.0", VerificationMethod.OidcJwt, "bob@example.com");

        var result = await egress.CallAsync(ChatCaller(), bob, "seerr", "seerr_requests", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal(1, transport.Calls);
        // The shared key was injected — but never returned to the caller.
        Assert.Equal("Bearer SHARED-HOUSEHOLD-KEY", transport.LastHeaders!["Authorization"]);
        Assert.DoesNotContain("SHARED-HOUSEHOLD-KEY", result.Body ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ungranted_user_gets_nothing_from_the_shared_service_key()
    {
        var (egress, transport) = BuildSharedKey();
        var mallory = new EndUserAssertion("mallory@example.com", "https://issuer/v2.0", VerificationMethod.OidcJwt, "mallory@example.com");

        var result = await egress.CallAsync(ChatCaller(), mallory, "seerr", "seerr_requests", null, confirmed: false);

        // Denied by the PDP BEFORE the resolver ever touches the shared key.
        Assert.Equal(ProviderCallStatus.Denied, result.Status);
        Assert.Equal(0, transport.Calls);
        Assert.Null(result.Body);
    }

    // ── Query-param forwarding (allow-listed, URL-encoded) ────────────────────

    private static (ProviderEgress Egress, FakeTransport Transport) BuildQueryRecipe()
    {
        var store = new InMemoryCredentialStore();
        store.Put("sonarr-alice", new CredentialBundle(AccessToken: "AT"));
        var pdp = new PolicyDecisionPoint([new Grant(Caller, "sonarr", ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding("sonarr", "sonarr-alice", User)], store);
        var transport = new FakeTransport();
        var recipe = new Recipe("sonarr", Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/api/v3",
            Injection: InjectionKind.BearerToken,
            Tools:
            [
                // A list tool that forwards an allow-listed set of query params.
                new RecipeTool("sonarr_missing", "GET", "wanted/missing", "read:missing",
                    Query: ["pageSize", "sortKey", "sortDirection"]),
            ]);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);
        return (egress, transport);
    }

    [Fact]
    public async Task Allow_listed_query_params_are_forwarded_and_encoded()
    {
        var (egress, transport) = BuildQueryRecipe();
        var args = "{\"pageSize\":50,\"sortKey\":\"air date\",\"sortDirection\":\"descending\"}";
        var result = await egress.CallAsync(ChatCaller(), Alice(), "sonarr", "sonarr_missing", args, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        // Number forwarded raw, string URL-encoded; only the declared names appear.
        Assert.Equal($"https://{Host}/api/v3/wanted/missing?pageSize=50&sortKey=air%20date&sortDirection=descending", transport.LastUrl);
    }

    [Fact]
    public async Task An_undeclared_query_param_is_not_forwarded()
    {
        var (egress, transport) = BuildQueryRecipe();
        // 'apikey' + 'admin' are NOT in the allow-list — an agent can't smuggle them onto the URL.
        var args = "{\"pageSize\":10,\"apikey\":\"leak\",\"admin\":true}";
        var result = await egress.CallAsync(ChatCaller(), Alice(), "sonarr", "sonarr_missing", args, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal($"https://{Host}/api/v3/wanted/missing?pageSize=10", transport.LastUrl);
        Assert.DoesNotContain("apikey", transport.LastUrl!, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", transport.LastUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_query_string_when_no_declared_args_present()
    {
        var (egress, transport) = BuildQueryRecipe();
        var result = await egress.CallAsync(ChatCaller(), Alice(), "sonarr", "sonarr_missing", "{}", confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal($"https://{Host}/api/v3/wanted/missing", transport.LastUrl); // no trailing '?'
    }

    // ── API-key-header injection (the Servarr / Seerr provider class) ──────────

    private static (ProviderEgress Egress, FakeTransport Transport) BuildApiKeyRecipe(string header = "X-Api-Key", string? injection = "apikey")
    {
        var store = new InMemoryCredentialStore();
        store.Put("sonarr-key", new CredentialBundle(AccessToken: "THE-API-KEY"));
        var pdp = new PolicyDecisionPoint([new Grant(Caller, "sonarr", ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding("sonarr", "sonarr-key", User)], store);
        var transport = new FakeTransport();
        var recipe = new Recipe("sonarr", Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/api/v3",
            Injection: injection == "apikey" ? InjectionKind.ApiKeyHeader : InjectionKind.None,
            InjectionHeader: header == "X-Api-Key" ? null : header,
            Tools: [new RecipeTool("sonarr_series", "GET", "series", "read:series")]);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);
        return (egress, transport);
    }

    [Fact]
    public async Task Api_key_header_injects_the_access_token_into_x_api_key()
    {
        var (egress, transport) = BuildApiKeyRecipe();
        var result = await egress.CallAsync(ChatCaller(), Alice(), "sonarr", "sonarr_series", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        // The key goes in X-Api-Key (NOT Authorization: Bearer) — the Servarr shape.
        Assert.Equal("THE-API-KEY", transport.LastHeaders!["X-Api-Key"]);
        Assert.False(transport.LastHeaders!.ContainsKey("Authorization"));
        // …and is never returned to the caller.
        Assert.DoesNotContain("THE-API-KEY", result.Body ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_key_header_uses_the_recipe_header_name()
    {
        var (egress, transport) = BuildApiKeyRecipe(header: "X-Plex-Token");
        var result = await egress.CallAsync(ChatCaller(), Alice(), "sonarr", "sonarr_series", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal("THE-API-KEY", transport.LastHeaders!["X-Plex-Token"]);
    }

    [Fact]
    public async Task Api_key_header_refuses_when_no_key_is_stored()
    {
        var store = new InMemoryCredentialStore(); // no bundle
        var pdp = new PolicyDecisionPoint([new Grant(Caller, "sonarr", ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding("sonarr", "sonarr-key", User)], store);
        var transport = new FakeTransport();
        var recipe = new Recipe("sonarr", Egress: EgressMode.Http, UpstreamBaseUrl: $"https://{Host}/api/v3",
            Injection: InjectionKind.ApiKeyHeader,
            Tools: [new RecipeTool("sonarr_series", "GET", "series", "read:series")]);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [recipe], new SsrfGuard([Host]), transport);

        var result = await egress.CallAsync(ChatCaller(), Alice(), "sonarr", "sonarr_series", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.NoCredential, result.Status); // fail-safe: no unauthenticated call
        Assert.Equal(0, transport.Calls);
    }
}


