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
        var audit = config.Audit.Enabled ? JsonlAuditSink.Open(config.Audit.Path) : (IAuditSink)NullAuditSink.Instance;
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

        var app = builder.Build();
        MapEndpoints(app);
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
