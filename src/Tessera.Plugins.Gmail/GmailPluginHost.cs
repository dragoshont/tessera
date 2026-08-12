using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;

namespace Tessera.Plugins.Gmail;

internal static class GmailPluginHost
{
    public static PluginSetupDescriptor DescribeSetup(PluginHostConfiguration configuration)
    {
        var options = LoadOptions(configuration);
        return new(
            "gmail",
            "Gmail",
            options.Enabled,
            true,
            options.Enabled ? "/api/v1/accounts/gmail/connect" : null,
            options.Enabled ? "account_authorization_required" : "oauth_application_unavailable");
    }

    public static void ConfigureServices(IServiceCollection services, PluginHostConfiguration configuration)
    {
        var options = LoadOptions(configuration);
        services.AddSingleton(options);
        if (!options.Enabled) return;
        services.AddSingleton(sp => new GmailOAuthService(
            sp.GetRequiredService<IHttpTransport>(),
            sp.GetRequiredService<ICredentialStore>()));
        services.AddHostedService<GmailPluginTokenRefreshService>();
        services.AddHostedService<GmailPluginSyncService>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<GmailOAuthOptions>();
        if (!options.Enabled) return;
        endpoints.MapPost("/api/v1/accounts/gmail/connect", async (
            HttpContext context,
            GmailConnectRequest? request,
            IPluginRequestIdentity identity,
            GmailOAuthService oauth,
            CancellationToken token) =>
        {
            var owner = await identity.ResolveOwnerAsync(context, token);
            if (owner is null) return Problem(401, "unauthenticated");
            if (request is null || string.IsNullOrWhiteSpace(request.DisplayName))
                return Problem(400, "invalid_request");
            try
            {
                var start = oauth.Begin(owner, request.DisplayName, options);
                return Results.Json(new { authorizeUrl = start.AuthorizeUrl.ToString() });
            }
            catch (InvalidOperationException exception) { return Problem(422, exception.Message); }
            catch (ArgumentException) { return Problem(400, "invalid_request"); }
        });

        endpoints.MapGet("/oauth/gmail/callback", async (
            string? code,
            string? state,
            string? error,
            GmailOAuthService oauth,
            IHttpTransport transport,
            IPluginAccountRuntime accounts,
            ICredentialStore custody,
            CancellationToken token) =>
        {
            if (!string.IsNullOrWhiteSpace(error)) return TextFailure("provider_authorization_failed");
            var completed = await oauth.CompleteAsync(state, code, token);
            if (!completed.Succeeded || completed.OwnerPrincipalId is null
                || completed.DisplayName is null || completed.Credentials is null)
                return TextFailure(completed.ErrorCode ?? "oauth_callback_failed");
            var adapter = new GmailRestAdapter(transport);
            var identityResult = await adapter.ValidateAsync(completed.Credentials.AccessToken!, token);
            if (!identityResult.Succeeded || identityResult.Identity is null)
                return TextFailure(identityResult.ErrorCode ?? "provider_identity_unavailable");
            var proof = await adapter.SearchMessagesAsync(completed.Credentials.AccessToken!, "newer_than:1d", 1, token);
            if (!proof.Succeeded) return TextFailure(proof.ErrorCode ?? "provider_read_unavailable");
            var owner = completed.OwnerPrincipalId;
            var accountId = AccountId(owner, identityResult.Identity.EmailAddress);
            var current = await accounts.GetAccountAsync(owner, accountId, token);
            var permissions = Permissions(completed.GrantedScopes);
            var bindings = Bindings(permissions);
            try
            {
                var account = current ?? await accounts.ConnectAsync(
                    owner,
                    accountId,
                    "gmail",
                    "gmail",
                    "1.0.0",
                    completed.DisplayName,
                    "{}",
                    completed.Credentials,
                    permissions,
                    bindings,
                    token);
                if (current is not null && custody is ICredentialWriter writer)
                    await writer.PutBundleAsync(current.CredentialRef, completed.Credentials, token);
                await accounts.SetValidationAsync(account, new(
                    AccountLifecycle.Connected,
                    AccountHealth.Healthy,
                    identityResult.Identity.EmailAddress,
                    identityResult.Identity.EmailAddress,
                    permissions,
                    completed.GrantedScopes,
                    bindings,
                    DateTimeOffset.UtcNow), token);
                await accounts.RecomputeJobsHealthAsync(owner, token);
                return Results.Text("Gmail connected. You can close this window and return to Tessera.", "text/plain; charset=utf-8");
            }
            catch (Exception exception) when (exception is InvalidOperationException or StoreException)
            { return TextFailure("gmail_account_storage_failed"); }
        });
    }

    internal static string[] Permissions(IReadOnlyList<string> scopes)
    {
        var values = new HashSet<string>(StringComparer.Ordinal) { "gmail.readonly" };
        if (scopes.Contains("https://www.googleapis.com/auth/gmail.compose", StringComparer.Ordinal))
        { values.Add("gmail.compose"); values.Add("gmail.send"); }
        if (scopes.Contains("https://www.googleapis.com/auth/gmail.send", StringComparer.Ordinal)) values.Add("gmail.send");
        return values.Order(StringComparer.Ordinal).ToArray();
    }

    internal static AccountCapabilityBinding[] Bindings(IReadOnlyList<string> permissions)
        => new GmailPlugin().Manifest.Capabilities
            .Where(item => item.RequiredPermissions.All(permission => permissions.Contains(permission, StringComparer.Ordinal)))
            .Select(item => new AccountCapabilityBinding("gmail", "1.0.0", item.CapabilityId, item.Version))
            .ToArray();

    private static GmailOAuthOptions LoadOptions(PluginHostConfiguration configuration)
    {
        GmailOAuthOptions options = new();
        if (!string.IsNullOrWhiteSpace(configuration.ConfigPath) && File.Exists(configuration.ConfigPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configuration.ConfigPath));
            if (document.RootElement.TryGetProperty("gmailOAuth", out var root)
                && root.ValueKind == JsonValueKind.Object)
                options = new()
                {
                    Enabled = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                    ClientId = Text(root, "clientId"),
                    ClientSecretRef = Text(root, "clientSecretRef"),
                    RedirectUri = Text(root, "redirectUri"),
                    Scopes = root.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                        ? scopes.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()
                        : options.Scopes,
                };
        }
        var enabledOverride = configuration.GetEnvironmentVariable("TESSERA_GMAIL_OAUTH_ENABLED");
        var scopesOverride = configuration.GetEnvironmentVariable("TESSERA_GMAIL_OAUTH_SCOPES");
        options = new()
        {
            Enabled = enabledOverride is null ? options.Enabled : enabledOverride is "1" or "true" or "TRUE",
            ClientId = configuration.GetEnvironmentVariable("TESSERA_GMAIL_OAUTH_CLIENT_ID") ?? options.ClientId,
            ClientSecretRef = configuration.GetEnvironmentVariable("TESSERA_GMAIL_OAUTH_CLIENT_SECRET_REF") ?? options.ClientSecretRef,
            RedirectUri = configuration.GetEnvironmentVariable("TESSERA_GMAIL_OAUTH_REDIRECT_URI") ?? options.RedirectUri,
            Scopes = scopesOverride is null ? options.Scopes : scopesOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(GmailOAuthOptions options)
    {
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecretRef)
            || !Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme != Uri.UriSchemeHttps && !redirect.IsLoopback)
            throw new InvalidOperationException("gmail_oauth_not_configured");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "https://www.googleapis.com/auth/gmail.readonly",
            "https://www.googleapis.com/auth/gmail.send",
            "https://www.googleapis.com/auth/gmail.compose",
        };
        if (!options.Scopes.Contains("https://www.googleapis.com/auth/gmail.readonly", StringComparer.Ordinal)
            || options.Scopes.Any(scope => !allowed.Contains(scope)))
            throw new InvalidOperationException("gmail_oauth_scopes_invalid");
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string AccountId(string owner, string email)
        => "gmail-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{email.ToLowerInvariant()}")))[..24];

    private static IResult Problem(int status, string code)
        => Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult TextFailure(string code)
        => Results.Text($"Gmail connection failed: {code.Replace('_', ' ')}.", "text/plain; charset=utf-8", statusCode: 400);

    private static string Text(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private sealed record GmailConnectRequest(string DisplayName);
}

internal sealed partial class GmailPluginTokenRefreshService(
    IPluginAccountRuntime accounts,
    GmailOAuthService oauth,
    GmailOAuthOptions options,
    ILogger<GmailPluginTokenRefreshService> logger) : BackgroundService
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
        foreach (var account in await accounts.ListAccountsAsync("gmail", token))
        {
            if (account.Lifecycle == AccountLifecycle.AuthRequired) continue;
            GmailRefreshResult result;
            try { result = await oauth.RefreshIfNeededAsync(account.CredentialRef, options, token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { LogRefreshFailure(logger, account.AccountId, exception); result = new(GmailRefreshStatus.Error, "oauth_refresh_failed"); }
            if (result.Status == GmailRefreshStatus.NotDue) continue;
            try
            {
                await accounts.SetStateAsync(
                    account,
                    result.Status == GmailRefreshStatus.Refreshed ? AccountLifecycle.Connected
                        : result.Status == GmailRefreshStatus.AuthRequired ? AccountLifecycle.AuthRequired : AccountLifecycle.Degraded,
                    result.Status == GmailRefreshStatus.Refreshed ? AccountHealth.Healthy
                        : result.Status == GmailRefreshStatus.AuthRequired ? AccountHealth.AuthRequired : AccountHealth.Degraded,
                    token);
                await accounts.RecomputeJobsHealthAsync(account.OwnerPrincipalId, token);
            }
            catch (ProductConcurrencyException) { }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refresh failed for Account {AccountId}.")]
    private static partial void LogRefreshFailure(ILogger logger, string accountId, Exception exception);
}

internal sealed partial class GmailPluginSyncService(
    IPluginAccountRuntime accounts,
    ICredentialStore custody,
    IHttpTransport transport,
    GmailOAuthService oauth,
    GmailOAuthOptions options,
    ILogger<GmailPluginSyncService> logger) : BackgroundService
{
    internal const int InitialLookbackDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncPassAsync(stoppingToken);
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    internal async Task SyncPassAsync(CancellationToken token)
    {
        foreach (var account in await accounts.ListAccountsAsync("gmail", token))
        {
            if (account.Lifecycle is AccountLifecycle.AuthRequired or AccountLifecycle.Disabled or AccountLifecycle.Revoked) continue;
            try { await SyncAccountAsync(account, token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { LogSyncFailure(logger, account.AccountId, exception); }
        }
    }

    private async Task SyncAccountAsync(ConnectedAccount account, CancellationToken token)
    {
        var refresh = await oauth.RefreshIfNeededAsync(account.CredentialRef, options, token);
        if (refresh.Status == GmailRefreshStatus.AuthRequired) { await MarkAuthRequiredAsync(account, token); return; }
        if (refresh.Status == GmailRefreshStatus.Error) return;
        var bundle = await custody.GetBundleAsync(account.CredentialRef, token);
        if (!bundle.HasAccessToken) return;
        var adapter = new GmailRestAdapter(transport);
        var state = await accounts.GetCursorAsync(account.OwnerPrincipalId, account.AccountId, "gmail", "history", token);
        IReadOnlyList<GmailMessageMetadata> messages;
        string historyId;
        if (state is null)
        {
            var identity = await adapter.ValidateAsync(bundle.AccessToken!, token);
            if (!identity.Succeeded || identity.Identity is null) { if (identity.ErrorCode == "provider_auth_required") await MarkAuthRequiredAsync(account, token); return; }
            var initial = await adapter.SearchMessagesAsync(bundle.AccessToken!, $"newer_than:{InitialLookbackDays}d", 25, token);
            if (!initial.Succeeded) { if (initial.ErrorCode == "provider_auth_required") await MarkAuthRequiredAsync(account, token); return; }
            messages = initial.Messages; historyId = identity.Identity.HistoryId;
        }
        else
        {
            var history = await adapter.GetHistoryAsync(bundle.AccessToken!, state.Cursor, token);
            if (history.CursorExpired)
            {
                var identity = await adapter.ValidateAsync(bundle.AccessToken!, token);
                var initial = await adapter.SearchMessagesAsync(bundle.AccessToken!, $"newer_than:{InitialLookbackDays}d", 25, token);
                if (!identity.Succeeded || identity.Identity is null || !initial.Succeeded) return;
                messages = initial.Messages; historyId = identity.Identity.HistoryId;
            }
            else if (!history.Succeeded) { if (history.ErrorCode == "provider_auth_required") await MarkAuthRequiredAsync(account, token); return; }
            else { messages = history.Messages; historyId = history.HistoryId!; }
        }
        var now = DateTimeOffset.UtcNow;
        var observations = messages.Select(message => Observation(account, message, now)).ToArray();
        await accounts.CommitCursorAsync(
            new(account.OwnerPrincipalId, account.AccountId, "gmail", "history", historyId, JsonSerializer.Serialize(new { initialLookbackDays = InitialLookbackDays }), now, state?.Version ?? 0),
            observations.Select(item => item.Evidence).ToArray(),
            observations.Select(item => item.Event).ToArray(),
            token);
    }

    private static (EvidenceRecord Evidence, ObservationEvent Event) Observation(
        ConnectedAccount account,
        GmailMessageMetadata message,
        DateTimeOffset observedAt)
    {
        var stable = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{account.AccountId}\n{message.Id}")));
        var evidenceId = $"evidence:gmail:{stable}";
        var content = JsonSerializer.Serialize(new { message.Id, message.ThreadId, message.LabelIds, message.InternalDate, message.From, message.To, message.Subject, message.Date });
        var evidence = EvidenceRecord.Create(
            evidenceId,
            account.OwnerPrincipalId,
            "gmail.message.observed",
            $"{account.AccountId}:{message.Id}",
            $"gmail://account/{account.AccountId}/message/{message.Id}",
            observedAt,
            message.InternalDate,
            "sha256",
            1,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            RetentionState.Active,
            SensitivityClass.Confidential,
            ProducerRef.Create("plugin:gmail", "1.0.0"),
            1);
        var observation = ObservationEvent.Create(
            $"event:gmail:{stable}",
            account.OwnerPrincipalId,
            "GmailMessageObserved",
            message.InternalDate ?? observedAt,
            observedAt,
            [account.AccountId],
            [message.Id, message.ThreadId],
            [evidenceId],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["accountId"] = account.AccountId,
                ["messageId"] = message.Id,
                ["threadId"] = message.ThreadId,
            },
            ProducerRef.Create("plugin:gmail", "1.0.0"),
            1);
        return (evidence, observation);
    }

    private async Task MarkAuthRequiredAsync(ConnectedAccount account, CancellationToken token)
    {
        try
        {
            await accounts.SetStateAsync(account, AccountLifecycle.AuthRequired, AccountHealth.AuthRequired, token);
            await accounts.RecomputeJobsHealthAsync(account.OwnerPrincipalId, token);
        }
        catch (ProductConcurrencyException) { }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail incremental sync failed for Account {AccountId}.")]
    private static partial void LogSyncFailure(ILogger logger, string accountId, Exception exception);
}