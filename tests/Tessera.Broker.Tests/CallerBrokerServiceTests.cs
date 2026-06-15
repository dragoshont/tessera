using Tessera.Core.Audit;
using Tessera.Core.Broker;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Tessera.Identity;
using Tessera.Mcp;
using Tessera.Providers;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// The caller authentication plane (ADR 0021): a non-human caller authenticates from
/// its app-only token (+ an optional forwarded end-user token) into a DISTINCT
/// verified caller, then dispatches into the existing broker spine. These exercise
/// the authentication branches (the new security logic) directly, and the dispatch
/// through the real <see cref="BrokerProviderGateway"/> / <see cref="BrokerCore"/>.
/// </summary>
public sealed class CallerBrokerServiceTests
{
    private const string CallerApp = "caller-app";
    private const string UserAlice = "user-alice";
    private const string CallerId = "media-mcp";

    // ── Authentication (Mode P / Mode U, fail-closed) ─────────────────────────

    [Fact]
    public async Task Authenticates_an_app_only_caller_in_mode_p()
    {
        var svc = AuthOnlyService();
        var id = await svc.AuthenticateAsync(CallerApp, onBehalfOfToken: null);

        Assert.True(id.Authenticated);
        Assert.Equal(CallerId, id.Caller!.Id);
        Assert.Equal(VerificationMethod.OidcJwt, id.Caller.VerifiedVia);
        Assert.True(id.Caller.IsVerified);
        Assert.Null(id.OnBehalfOf); // Mode P: no end-user
    }

    [Fact]
    public async Task Authenticates_an_app_caller_with_a_forwarded_end_user_in_mode_u()
    {
        var svc = AuthOnlyService();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        Assert.True(id.Authenticated);
        Assert.Equal(CallerId, id.Caller!.Id);
        Assert.NotNull(id.OnBehalfOf);
        Assert.Equal("alice-oid", id.OnBehalfOf!.Subject);
        Assert.True(id.OnBehalfOf.IsVerified);
    }

    [Fact]
    public async Task Rejects_a_user_token_presented_as_the_caller()
    {
        var svc = AuthOnlyService();
        var id = await svc.AuthenticateAsync(UserAlice, onBehalfOfToken: null);

        Assert.False(id.Authenticated);
        Assert.Contains("user token", id.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_missing_caller_token()
    {
        var svc = AuthOnlyService();
        var id = await svc.AuthenticateAsync(callerToken: null, onBehalfOfToken: null);

        Assert.False(id.Authenticated);
        Assert.Contains("no caller token", id.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_an_unknown_caller_token()
    {
        var svc = AuthOnlyService();
        var id = await svc.AuthenticateAsync("not-a-real-token", onBehalfOfToken: null);

        Assert.False(id.Authenticated);
    }

    [Fact]
    public async Task Rejects_an_app_only_on_behalf_of_token()
    {
        // A second app token must not be accepted as the FOR-WHOM — a human is required there.
        var validator = new FakeTokenValidator()
            .AddApp(CallerApp, CallerId)
            .AddApp("other-app", "other-mcp");
        var svc = new CallerBrokerService(validator, BrokerOnly(), DisabledProviderGateway.Instance);

        var id = await svc.AuthenticateAsync(CallerApp, "other-app");

        Assert.False(id.Authenticated);
        Assert.Contains("app-only", id.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_closed_when_the_validator_is_not_configured()
    {
        // DelegationEnabled=false ⇒ the validator rejects every token (the same gate
        // the endpoint enforces before dispatch).
        var validator = new FakeTokenValidator { DelegationEnabled = false }.AddApp(CallerApp, CallerId);
        var svc = new CallerBrokerService(validator, BrokerOnly(), DisabledProviderGateway.Instance);

        var id = await svc.AuthenticateAsync(CallerApp, onBehalfOfToken: null);

        Assert.False(id.Authenticated);
    }

    // ── Dispatch (Mode U: distinct caller + forwarded end-user) ───────────────

    [Fact]
    public async Task Lists_provider_tools_for_a_verified_caller()
    {
        var (svc, _, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        var listed = svc.ListTools(id);

        Assert.True(listed.Authenticated);
        Assert.Contains(listed.Tools, t => t.Tool == "sonarr_series" && t.Plane == "read");
        Assert.Contains(listed.Tools, t => t.Tool == "sonarr_search" && t.Plane == "use");
    }

    [Fact]
    public async Task A_read_call_succeeds_and_hits_the_upstream()
    {
        var (svc, transport, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        var result = await svc.CallAsync(id, "sonarr", "sonarr_series", argsJson: null, confirmed: false);

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("https://sonarr.example/api/v3/series", transport.LastUrl);
        Assert.Equal("Bearer AT", transport.LastHeaders!["Authorization"]);
    }

    [Fact]
    public async Task A_write_call_requires_confirmation_then_proceeds()
    {
        var (svc, transport, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        var unconfirmed = await svc.CallAsync(id, "sonarr", "sonarr_search", "{}", confirmed: false);
        Assert.Equal("stepup", unconfirmed.Status);
        Assert.Equal(0, transport.Calls); // never reached upstream without confirmation

        var confirmed = await svc.CallAsync(id, "sonarr", "sonarr_search", "{}", confirmed: true);
        Assert.Equal("completed", confirmed.Status);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("POST", transport.LastMethod);
    }

    [Fact]
    public async Task Check_reports_allow_for_a_granted_action()
    {
        var (svc, _, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        var check = await svc.CheckAsync(id, "sonarr", "read:series");

        Assert.Equal("allow", check.Effect);
    }

    [Fact]
    public async Task An_ungranted_caller_is_denied()
    {
        // The validator knows a second app, but no grant names it — default deny.
        var (svc, transport, _) = BuildDispatch(extraApp: ("intruder-token", "intruder-mcp"));
        var id = await svc.AuthenticateAsync("intruder-token", UserAlice);

        Assert.True(id.Authenticated); // authenticated…
        var result = await svc.CallAsync(id, "sonarr", "sonarr_series", null, confirmed: false);
        Assert.Equal("denied", result.Status); // …but not authorized
        Assert.Equal(0, transport.Calls);
    }

    // ── Dispatch (Mode P: caller-only, service-owned key) ─────────────────────

    [Fact]
    public async Task A_mode_p_caller_only_call_succeeds_against_a_service_binding()
    {
        var (svc, transport, _) = BuildDispatch(modeP: true);
        var id = await svc.AuthenticateAsync(CallerApp, onBehalfOfToken: null);

        Assert.Null(id.OnBehalfOf);
        var result = await svc.CallAsync(id, "sonarr", "sonarr_series", null, confirmed: false);

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, transport.Calls);
    }

    // ── The egress gate + the audit fix ───────────────────────────────────────

    [Fact]
    public async Task A_call_is_not_allowed_when_egress_is_disabled()
    {
        var (svc, _, _) = BuildDispatch(egressEnabled: false);
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        var result = await svc.CallAsync(id, "sonarr", "sonarr_series", null, confirmed: false);

        Assert.Equal("notallowed", result.Status); // the gateway is disabled until egress.enabled
    }

    [Fact]
    public async Task A_brokered_call_is_authorization_audited()
    {
        var (svc, _, audit) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        await svc.CallAsync(id, "sonarr", "sonarr_series", null, confirmed: false);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(CallerId, entry.Caller);
        Assert.True(entry.CallerVerified);
        Assert.Equal("sonarr", entry.Target);
        Assert.Equal("read:series", entry.Action);
        Assert.Equal(Effect.Allow, entry.Effect);
    }

    [Fact]
    public async Task An_unauthenticated_dispatch_is_refused_without_an_upstream_call()
    {
        var (svc, transport, _) = BuildDispatch();
        var anonymous = CallerResolution.Fail("no caller token");

        var result = await svc.CallAsync(anonymous, "sonarr", "sonarr_series", null, confirmed: false);

        Assert.Equal("unauthenticated", result.Status);
        Assert.Equal(0, transport.Calls);
    }

    // ── op=invoke: address a tool by its HTTP (method, path) ──────────────────

    [Fact]
    public async Task Invoke_resolves_a_tool_by_method_and_path()
    {
        var (svc, transport, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        // The MCP knows the URL it was going to call; Tessera maps it to sonarr_series.
        var result = await svc.InvokeAsync(id, "sonarr", "GET", "/series", argsJson: null, confirmed: false);

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("https://sonarr.example/api/v3/series", transport.LastUrl);
    }

    [Fact]
    public async Task Invoke_normalises_the_leading_slash()
    {
        var (svc, _, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        // Recipe path is "series" (no slash); the caller sends "/series" — they match.
        var withSlash = await svc.InvokeAsync(id, "sonarr", "GET", "/series", null, confirmed: false);
        var without = await svc.InvokeAsync(id, "sonarr", "GET", "series", null, confirmed: false);

        Assert.Equal("completed", withSlash.Status);
        Assert.Equal("completed", without.Status);
    }

    [Fact]
    public async Task Invoke_refuses_an_unknown_method_path()
    {
        var (svc, transport, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        var result = await svc.InvokeAsync(id, "sonarr", "DELETE", "/series/42", null, confirmed: false);

        Assert.Equal("notallowed", result.Status); // no declared tool matches → the recipe is the allow-list
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Invoke_honours_step_up_on_a_write_tool()
    {
        var (svc, transport, _) = BuildDispatch();
        var id = await svc.AuthenticateAsync(CallerApp, UserAlice);

        // POST /command is the write tool (sonarr_search) — needs confirmation.
        var unconfirmed = await svc.InvokeAsync(id, "sonarr", "POST", "/command", "{}", confirmed: false);
        Assert.Equal("stepup", unconfirmed.Status);
        Assert.Equal(0, transport.Calls);

        var confirmed = await svc.InvokeAsync(id, "sonarr", "POST", "/command", "{}", confirmed: true);
        Assert.Equal("completed", confirmed.Status);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Invoke_is_unauthenticated_without_a_caller()
    {
        var (svc, transport, _) = BuildDispatch();
        var anonymous = CallerResolution.Fail("no caller token");

        var result = await svc.InvokeAsync(anonymous, "sonarr", "GET", "/series", null, confirmed: false);

        Assert.Equal("unauthenticated", result.Status);
        Assert.Equal(0, transport.Calls);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static CallerBrokerService AuthOnlyService()
    {
        var validator = new FakeTokenValidator()
            .AddApp(CallerApp, CallerId)
            .AddUser(UserAlice, "alice-oid", "alice@example.com");
        return new CallerBrokerService(validator, BrokerOnly(), DisabledProviderGateway.Instance);
    }

    private static BrokerCore BrokerOnly() =>
        new(new PolicyDecisionPoint(), new CredentialResolver([], new InMemoryCredentialStore()));

    private static (CallerBrokerService Svc, FakeTransport Transport, RecordingAudit Audit) BuildDispatch(
        bool egressEnabled = true,
        bool modeP = false,
        (string Token, string AppId)? extraApp = null)
    {
        var store = new InMemoryCredentialStore();
        var transport = new FakeTransport();
        var audit = new RecordingAudit();

        Grant grant;
        TargetBinding binding;
        if (modeP)
        {
            store.Put("sonarr-svc", new CredentialBundle(AccessToken: "AT"));
            grant = new Grant(CallerId, "sonarr", ["read:*", "use:*"], OnBehalfOf: null, StepUpActions: ["use:search"]);
            binding = new TargetBinding("sonarr", "sonarr-svc");
        }
        else
        {
            store.Put("sonarr-alice", new CredentialBundle(AccessToken: "AT"));
            grant = new Grant(CallerId, "sonarr", ["read:*", "use:*"], "alice@example.com", StepUpActions: ["use:search"]);
            binding = new TargetBinding("sonarr", "sonarr-alice", "alice@example.com");
        }

        var pdp = new PolicyDecisionPoint([grant]);
        var resolver = new CredentialResolver([binding], store);
        var broker = new BrokerCore(pdp, resolver, audit);
        var config = new TesseraConfig
        {
            Egress = new EgressOptions { Enabled = egressEnabled, AllowedHosts = ["sonarr.example"] },
        };
        var gateway = BrokerProviderGateway.Build(config, pdp, resolver, [SonarrRecipe()], transport, audit);

        var validator = new FakeTokenValidator()
            .AddApp(CallerApp, CallerId)
            .AddUser(UserAlice, "alice-oid", "alice@example.com");
        if (extraApp is { } extra)
        {
            validator.AddApp(extra.Token, extra.AppId);
        }

        return (new CallerBrokerService(validator, broker, gateway), transport, audit);
    }

    private static Recipe SonarrRecipe() => new(
        Target: "sonarr",
        Egress: EgressMode.Http,
        UpstreamBaseUrl: "https://sonarr.example/api/v3",
        Injection: InjectionKind.BearerToken,
        Tools:
        [
            new RecipeTool("sonarr_series", "GET", "series", "read:series", StepUp: false, "List monitored series"),
            new RecipeTool("sonarr_search", "POST", "command", "use:search", StepUp: true, "Trigger a search"),
        ]);

    /// <summary>An audit sink that records every decision for assertions.</summary>
    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential) =>
            Entries.Add(AuditEntry.From(request, decision, credential));
    }

    /// <summary>A fake transport: records the last request, returns a canned 200. No network.</summary>
    private sealed class FakeTransport : IHttpTransport
    {
        public string? LastMethod { get; private set; }
        public string? LastUrl { get; private set; }
        public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }
        public int Calls { get; private set; }

        public Task<TransportResponse> SendAsync(
            string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMethod = method;
            LastUrl = url;
            LastHeaders = headers;
            return Task.FromResult(new TransportResponse(200, new Dictionary<string, string>(), "{\"ok\":true}"));
        }
    }
}
