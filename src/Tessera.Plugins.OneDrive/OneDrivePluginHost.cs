using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;

namespace Tessera.Plugins.OneDrive;

internal static class OneDrivePluginHost
{
    public static PluginSetupDescriptor DescribeSetup(PluginHostConfiguration configuration)
    {
        var options = LoadOptions(configuration);
        return new("onedrive", "OneDrive", options.Enabled, true,
            options.Enabled ? "/api/v1/accounts/onedrive/connect" : null,
            options.Enabled ? "account_authorization_required" : "oauth_application_unavailable");
    }

    public static void ConfigureServices(IServiceCollection services, PluginHostConfiguration configuration)
    {
        var options = LoadOptions(configuration);
        services.AddSingleton(options);
        if (!options.Enabled) return;
        services.AddSingleton(sp => new OneDriveOAuthService(sp.GetRequiredService<IHttpTransport>(), sp.GetRequiredService<ICredentialStore>()));
        services.AddHostedService<OneDriveTokenRefreshService>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<OneDriveOAuthOptions>();
        if (!options.Enabled) return;
        endpoints.MapPost("/api/v1/accounts/onedrive/connect", async (HttpContext context, ConnectRequest? request, IPluginRequestIdentity identity, OneDriveOAuthService oauth, CancellationToken token) =>
        {
            var owner = await identity.ResolveOwnerAsync(context, token);
            if (owner is null) return Problem(401, "unauthenticated");
            if (request is null || string.IsNullOrWhiteSpace(request.DisplayName)) return Problem(400, "invalid_request");
            try { return Results.Json(new { authorizeUrl = oauth.Begin(owner, request.DisplayName, options).AuthorizeUrl.ToString() }); }
            catch (InvalidOperationException exception) { return Problem(422, exception.Message); }
            catch (ArgumentException) { return Problem(400, "invalid_request"); }
        });

        endpoints.MapGet("/oauth/onedrive/callback", async (string? code, string? state, string? error, OneDriveOAuthService oauth, IHttpTransport transport, IPluginAccountRuntime accounts, ICredentialStore custody, CancellationToken token) =>
        {
            if (!string.IsNullOrWhiteSpace(error)) return TextFailure("provider_authorization_failed");
            var completed = await oauth.CompleteAsync(state, code, token);
            if (!completed.Succeeded || completed.OwnerPrincipalId is null || completed.DisplayName is null || completed.Credentials is null)
                return TextFailure(completed.ErrorCode ?? "oauth_callback_failed");
            var identity = await new OneDriveRestAdapter(transport).ValidateAsync(completed.Credentials.AccessToken!, token);
            if (!identity.Succeeded || identity.Identity is null) return TextFailure(identity.ErrorCode ?? "provider_identity_unavailable");
            var owner = completed.OwnerPrincipalId;
            var accountId = AccountId(owner, identity.Identity.DriveId);
            var current = await accounts.GetAccountAsync(owner, accountId, token);
            var bindings = new OneDrivePlugin().Manifest.Capabilities.Select(item => new AccountCapabilityBinding("onedrive", "1.0.0", item.CapabilityId, item.Version)).ToArray();
            try
            {
                var account = current ?? await accounts.ConnectAsync(owner, accountId, "onedrive", "onedrive", "1.0.0", completed.DisplayName, "{}", completed.Credentials, ["onedrive.read"], bindings, token);
                if (current is not null && custody is ICredentialWriter writer) await writer.PutBundleAsync(current.CredentialRef, completed.Credentials, token);
                await accounts.SetValidationAsync(account, new(AccountLifecycle.Connected, AccountHealth.Healthy, identity.Identity.DriveId,
                    identity.Identity.OwnerDisplayName ?? identity.Identity.DriveId, ["onedrive.read"], completed.GrantedScopes, bindings, DateTimeOffset.UtcNow), token);
                await accounts.RecomputeJobsHealthAsync(owner, token);
                return Results.Text("OneDrive connected. You can close this window and return to Tessera.", "text/plain; charset=utf-8");
            }
            catch (Exception exception) when (exception is InvalidOperationException or StoreException) { return TextFailure("onedrive_account_storage_failed"); }
        });
    }

    internal static OneDriveOAuthOptions LoadOptions(PluginHostConfiguration configuration)
    {
        OneDriveOAuthOptions options = new();
        if (!string.IsNullOrWhiteSpace(configuration.ConfigPath) && File.Exists(configuration.ConfigPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configuration.ConfigPath));
            if (document.RootElement.TryGetProperty("oneDriveOAuth", out var root) && root.ValueKind == JsonValueKind.Object)
                options = new()
                {
                    Enabled = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                    ClientId = Text(root, "clientId"),
                    ClientSecretRef = Text(root, "clientSecretRef"),
                    RedirectUri = Text(root, "redirectUri"),
                    Scopes = root.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                        ? scopes.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : options.Scopes,
                };
        }
        var enabledOverride = configuration.GetEnvironmentVariable("TESSERA_ONEDRIVE_OAUTH_ENABLED");
        var scopeOverride = configuration.GetEnvironmentVariable("TESSERA_ONEDRIVE_OAUTH_SCOPES");
        options = new()
        {
            Enabled = enabledOverride is null ? options.Enabled : enabledOverride is "1" or "true" or "TRUE",
            ClientId = configuration.GetEnvironmentVariable("TESSERA_ONEDRIVE_OAUTH_CLIENT_ID") ?? options.ClientId,
            ClientSecretRef = configuration.GetEnvironmentVariable("TESSERA_ONEDRIVE_OAUTH_CLIENT_SECRET_REF") ?? options.ClientSecretRef,
            RedirectUri = configuration.GetEnvironmentVariable("TESSERA_ONEDRIVE_OAUTH_REDIRECT_URI") ?? options.RedirectUri,
            Scopes = scopeOverride is null ? options.Scopes : scopeOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(OneDriveOAuthOptions options)
    {
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecretRef)
            || !Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme != Uri.UriSchemeHttps && !redirect.IsLoopback
            || !string.Equals(redirect.AbsolutePath, "/oauth/onedrive/callback", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(redirect.Query)
            || !string.IsNullOrEmpty(redirect.Fragment))
            throw new InvalidOperationException("onedrive_oauth_not_configured");
        OneDriveOAuthService.ValidateScopes(options.Scopes);
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string AccountId(string owner, string driveId)
        => "onedrive-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{driveId}")))[..24];
    private static IResult Problem(int status, string code)
        => Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
    private static IResult TextFailure(string code)
        => Results.Text($"OneDrive connection failed: {code.Replace('_', ' ')}.", "text/plain; charset=utf-8", statusCode: 400);
    private sealed record ConnectRequest(string DisplayName);
}

internal sealed class OneDriveTokenRefreshService(IPluginAccountRuntime accounts, OneDriveOAuthService oauth, OneDriveOAuthOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshPassAsync(stoppingToken);
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task RefreshPassAsync(CancellationToken token)
    {
        foreach (var account in await accounts.ListAccountsAsync("onedrive", token))
        {
            if (account.Lifecycle == AccountLifecycle.AuthRequired) continue;
            var result = await oauth.RefreshIfNeededAsync(account.CredentialRef, options, token);
            if (result.Status == OneDriveRefreshStatus.NotDue) continue;
            try
            {
                await accounts.SetStateAsync(account,
                    result.Status == OneDriveRefreshStatus.Refreshed ? AccountLifecycle.Connected : result.Status == OneDriveRefreshStatus.AuthRequired ? AccountLifecycle.AuthRequired : AccountLifecycle.Degraded,
                    result.Status == OneDriveRefreshStatus.Refreshed ? AccountHealth.Healthy : result.Status == OneDriveRefreshStatus.AuthRequired ? AccountHealth.AuthRequired : AccountHealth.Degraded, token);
                await accounts.RecomputeJobsHealthAsync(account.OwnerPrincipalId, token);
            }
            catch (ProductConcurrencyException) { }
        }
    }
}