using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tessera.Core.Audit;
using Tessera.Core.Broker;
using Tessera.Core.Configuration;
using Tessera.Core.Policy;
using Tessera.Core.Portal;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Tessera.Broker.Egress;
using Tessera.Identity;
using Tessera.Mcp;
using Tessera.Stores.AzureKeyVault;
using Yarp.ReverseProxy.Forwarder;

namespace Tessera.Broker;

/// <summary>How to build a broker host (paths + test overrides).</summary>
public sealed record BrokerHostOptions
{
    /// <summary>Path to <c>tessera.json</c> (null ⇒ defaults + env).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Path to the policy document (null ⇒ the config's <c>policy.document</c>).</summary>
    public string? PolicyPath { get; init; }

    /// <summary>Override the credential store (tests).</summary>
    public ICredentialStore? StoreOverride { get; init; }

    /// <summary>Override the token validator (tests).</summary>
    public ITokenValidator? ValidatorOverride { get; init; }

    /// <summary>Override the live-view provider (tests) — wire a fake browser worker.</summary>
    public Tessera.Core.Portal.ILiveViewProvider? LiveViewProviderOverride { get; init; }

    /// <summary>Override the environment (tests).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>The broker composition root: config → pipeline → host + endpoints + MCP.</summary>
public static class BrokerHost
{
    /// <summary>Parses <c>--config</c>/<c>--grants</c> and builds the app.</summary>
    public static Task<WebApplication> BuildAppAsync(string[] args, CancellationToken cancellationToken = default)
    {
        string? config = null;
        string? grants = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
            {
                config = args[i + 1];
            }
            else if (args[i] == "--grants")
            {
                grants = args[i + 1];
            }
        }

        return BuildAppAsync(new BrokerHostOptions { ConfigPath = config, PolicyPath = grants }, cancellationToken);
    }

    /// <summary>Builds (but does not start) the broker <see cref="WebApplication"/>.</summary>
    public static async Task<WebApplication> BuildAppAsync(BrokerHostOptions options, CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.LoadConfig(options.ConfigPath, options.Environment);
        var problems = config.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("invalid configuration: " + string.Join("; ", problems));
        }

        var policyPath = options.PolicyPath ?? config.Policy.Document;
        var policy = ConfigLoader.LoadPolicy(policyPath);

        var store = options.StoreOverride
            ?? AzureKeyVaultCredentialStore.FromEnvironment(options.Environment)
            ?? (ICredentialStore)new InMemoryCredentialStore();

        var pdp = new PolicyDecisionPoint(policy.Grants, allowUnverified: config.Identity.Mode == "dev");
        var resolver = new CredentialResolver(policy.Bindings, store);
        var baseAudit = config.Audit.Enabled ? JsonlAuditSink.Open(config.Audit.Path) : (IAuditSink)NullAuditSink.Instance;
        // Wrap the durable sink with a bounded in-memory tail for the portal's
        // activity feed (ADR 0017). The tail mirrors the durable record (written
        // first, always) — it never replaces it; a restart drops the tail, never the
        // JSONL log. TailCapacity = 0 opts out (the feed is then empty).
        IAuditSink audit;
        IAuditTail auditTail;
        if (config.Audit.TailCapacity > 0)
        {
            var ring = new RingBufferAuditSink(baseAudit, config.Audit.TailCapacity);
            audit = ring;
            auditTail = ring;
        }
        else
        {
            audit = baseAudit;
            auditTail = NullAuditTail.Instance;
        }

        var broker = new BrokerCore(pdp, resolver, audit);
        var validator = options.ValidatorOverride ?? BuildValidator(config);

        var status = new BrokerStatus
        {
            StoreKind = store.Kind,
            BrokerEndpointEnabled = false, // fail-closed: no mTLS caller-auth plane in iteration 1
            DelegationEnabled = validator.DelegationEnabled,
        };

        var mcpOptions = new TesseraMcpOptions();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{config.Server.Host}:{config.Server.Port}");

        // Keep the audit JSONL the signal: quiet ASP.NET's per-request logging
        // (every /healthz, /readyz probe) so it doesn't drown the broker's own
        // secret-free decision audit. Lifetime ("now listening") stays visible.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        var services = builder.Services;
        services.AddSingleton(config);
        services.AddSingleton(policy);
        services.AddSingleton<IReadOnlyList<Recipe>>(policy.Recipes);
        services.AddSingleton(pdp);
        services.AddSingleton(resolver);
        services.AddSingleton(store);
        services.AddSingleton(audit);
        services.AddSingleton(auditTail);
        services.AddSingleton(broker);
        services.AddSingleton(validator);
        services.AddSingleton(status);
        services.AddHttpForwarder();
        services.AddSingleton(sp => new InjectionEgress(config.Egress, sp.GetRequiredService<IHttpForwarder>()));
        // Provider egress (ADR 0014): the real HTTP transport + the gateway the MCP
        // surface uses to inject a credential by identity. Disabled (every call
        // refused) until egress.enabled — so deploying never opens an upstream path.
        services.AddSingleton<Tessera.Providers.IHttpTransport>(new Egress.HttpClientTransport());
        services.AddSingleton<Tessera.Mcp.IProviderGateway>(sp => BrokerProviderGateway.Build(
            config, pdp, resolver, policy.Recipes, sp.GetRequiredService<Tessera.Providers.IHttpTransport>()));
        services.AddTesseraMcp(mcpOptions);

        // Admin-portal read-model (ADR 0016): people + connection health projected
        // over the policy + store status, plus a fail-closed live-view provider for
        // the captcha hand-off. Both are secret-free; the live-view provider opens no
        // remote browser until a worker adapter is wired. Adding a connection writes
        // a binding back to the policy document (files stay the source of truth);
        // on a read-only mount (GitOps) the write is skipped and the add is in-memory.
        Action<LoadedPolicy>? persist = string.IsNullOrEmpty(policyPath)
            ? null
            : updated => ConfigLoader.SavePolicy(policyPath, updated);
        services.AddSingleton(new PortalService(policy, resolver, config.Portal.Admins, persist));
        services.AddSingleton<Tessera.Core.Portal.ILiveViewProvider>(
            BuildLiveViewProvider(config, options));

        var app = builder.Build();
        ServePortalSpa(app, config);
        MapEndpoints(app);
        app.MapPortalEndpoints();
        app.MapMcp("/mcp");

        // Startup self-test (read-only) — proves the authorize+resolve spine against
        // the real store without any upstream call. Time-boxed + fail-soft so a slow
        // or unreachable store can never crashloop the broker.
        var environment = options.Environment;
        var selftestTarget = Get(environment, "TESSERA_SELFTEST_TARGET");
        if (!string.IsNullOrEmpty(selftestTarget))
        {
            var principal = Get(environment, "TESSERA_SELFTEST_PRINCIPAL");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                status.SelfTest = await SelfTest
                    .RunAsync(broker, selftestTarget, string.IsNullOrEmpty(principal) ? null : principal, config.Identity.TrustDomain, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                status.SelfTest = new SelfTestResult(
                    selftestTarget, principal, "error", "self-test timed out reaching the store", null, null, false);
            }
        }

        status.Ready = true;
        EmitStartupBanner(config, status);
        return app;
    }

    private static ITokenValidator BuildValidator(TesseraConfig config)
    {
        if (config.Identity.Mode == "oidc" && !string.IsNullOrWhiteSpace(config.Identity.Oidc.Issuer))
        {
            return EntraTokenValidator.Create(new OidcValidationOptions
            {
                Issuer = config.Identity.Oidc.Issuer,
                Audience = config.Identity.Oidc.Audience,
                TenantId = config.Identity.Oidc.TenantId,
                AllowedTenants = config.Identity.Oidc.AllowedTenants,
            });
        }

        return new DenyAllTokenValidator($"OIDC delegation not configured (identity.mode={config.Identity.Mode})");
    }

    /// <summary>
    /// Builds the live-view provider for the captcha hand-off (ADR 0016 §3). A test
    /// override wins; otherwise a real <see cref="WorkerLiveViewProvider"/> over an
    /// <see cref="HttpLiveViewWorker"/> when <c>liveView.enabled</c> (config is
    /// already validated to carry a valid absolute worker URL); otherwise the
    /// fail-closed <see cref="DisabledLiveViewProvider"/>. The optional caller token
    /// (authenticating the broker to the worker) comes from the environment, never
    /// the config file (it is a secret).
    /// </summary>
    private static Tessera.Core.Portal.ILiveViewProvider BuildLiveViewProvider(TesseraConfig config, BrokerHostOptions options)
    {
        if (options.LiveViewProviderOverride is not null)
        {
            return options.LiveViewProviderOverride;
        }

        if (!config.LiveView.Enabled)
        {
            return Tessera.Core.Portal.DisabledLiveViewProvider.Instance;
        }

        var callerToken = Get(options.Environment, "TESSERA_LIVEVIEW_WORKER_TOKEN");
        var worker = new HttpLiveViewWorker(new Uri(config.LiveView.WorkerArmUrl, UriKind.Absolute), callerToken);
        return new WorkerLiveViewProvider(worker, config.LiveView.DefaultTtlSeconds);
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

        app.MapGet("/readyz", (BrokerStatus s) =>
            s.Ready ? Results.Json(new { ready = true }) : Results.Json(new { ready = false }, statusCode: 503));

        app.MapGet("/status", (BrokerStatus s) => Results.Json(new StatusResponse(
            Ready: s.Ready,
            Store: s.StoreKind,
            BrokerEndpoint: s.BrokerEndpointEnabled ? "enabled" : "fail-closed",
            Delegation: s.DelegationEnabled ? "enabled" : "fail-closed (no audience configured)",
            SelfTest: s.SelfTest)));

        // The mTLS caller-auth plane (for CLI/n8n callers) is not wired in iteration 1,
        // so the network brokering endpoint fails closed. The chat reaches the broker
        // through the MCP surface (/mcp), not this endpoint.
        app.MapPost("/v1/broker", () => Results.Json(
            new { error = "broker endpoint is fail-closed: no caller authenticator configured (mTLS/SVID auth plane not enabled). Use the MCP surface at /mcp." },
            statusCode: 503));
    }

    /// <summary>
    /// Serves the built admin-portal SPA at <c>/</c> when <c>portal.webRoot</c> points
    /// at an existing directory (the <c>web/dist</c> output). Same origin as the API,
    /// so the SPA's fetches need no CORS and no second deployment. The SPA fallback
    /// (index.html for client-side routes) has the lowest priority, so it never
    /// shadows <c>/portal</c>, <c>/mcp</c>, or the health endpoints. Unset = API only,
    /// so existing API-only deployments are unaffected.
    /// </summary>
    private static void ServePortalSpa(WebApplication app, TesseraConfig config)
    {
        var webRoot = config.Portal.WebRoot;
        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
        {
            return;
        }

        var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webRoot));
        var staticOptions = new StaticFileOptions { FileProvider = fileProvider };
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(staticOptions);
        // Client-side routes (e.g. /accounts, /admin/users) fall back to the SPA shell.
        app.MapFallbackToFile("index.html", staticOptions);
    }

    private static void EmitStartupBanner(TesseraConfig config, BrokerStatus status)
    {
        var banner = new
        {
            @event = "tessera.start",
            listen = $"{config.Server.Host}:{config.Server.Port}",
            store = status.StoreKind,
            broker_endpoint = status.BrokerEndpointEnabled ? "enabled" : "fail-closed",
            delegation = status.DelegationEnabled ? "enabled" : "fail-closed",
            mcp = "/mcp",
            selftest = status.SelfTest,
        };
        Console.WriteLine(JsonSerializer.Serialize(banner));
    }

    private static string? Get(IReadOnlyDictionary<string, string?>? environment, string key)
    {
        if (environment is not null)
        {
            return environment.TryGetValue(key, out var value) ? value : null;
        }

        return System.Environment.GetEnvironmentVariable(key);
    }
}

/// <summary>The /status payload (secret-free).</summary>
/// <param name="Ready">Whether startup completed.</param>
/// <param name="Store">The backing store kind.</param>
/// <param name="BrokerEndpoint"><c>enabled</c> or <c>fail-closed</c>.</param>
/// <param name="Delegation"><c>enabled</c> or <c>fail-closed (...)</c>.</param>
/// <param name="SelfTest">The startup self-test result, if any.</param>
public sealed record StatusResponse(
    bool Ready,
    string Store,
    string BrokerEndpoint,
    string Delegation,
    SelfTestResult? SelfTest);
