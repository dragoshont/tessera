using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Stores;
using Tessera.Providers;

namespace Tessera.Plugins.Gmail;

public sealed class GmailOAuthOptions
{
    public bool Enabled { get; init; }
    public string ClientId { get; init; } = "";
    public string ClientSecretRef { get; init; } = "";
    public string RedirectUri { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = ["https://www.googleapis.com/auth/gmail.readonly"];
}

public sealed record GmailOAuthStart(Uri AuthorizeUrl);

public sealed record GmailOAuthCompletion(
    bool Succeeded,
    string? OwnerPrincipalId,
    string? DisplayName,
    CredentialBundle? Credentials,
    IReadOnlyList<string> GrantedScopes,
    string? ErrorCode);

public enum GmailRefreshStatus { NotDue, Refreshed, AuthRequired, Error }
public sealed record GmailRefreshResult(GmailRefreshStatus Status, string? ErrorCode = null);

public sealed class GmailOAuthService
{
    public const string ClientSecretExtraKey = "client_secret";
    public const string ScopesExtraKey = "oauth_scopes";
    public const string TokenEndpointExtraKey = "oauth_token_endpoint";
    public const string ClientIdExtraKey = "oauth_client_id";
    public const string AccessExpiresAtExtraKey = "access_expires_at";

    private static readonly Uri AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private readonly IHttpTransport _transport;
    private readonly ICredentialStore _custody;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public GmailOAuthService(
        IHttpTransport transport,
        ICredentialStore custody,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null,
        int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(custody);
        _transport = transport;
        _custody = custody;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _capacity = Math.Max(1, capacity);
    }

    public GmailOAuthStart Begin(string ownerPrincipalId, string displayName, GmailOAuthOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecretRef)
            || !Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirectUri))
            throw new InvalidOperationException("gmail_oauth_not_configured");
        var label = displayName.Trim();
        if (label.Length > 256 || label.Any(char.IsControl))
            throw new ArgumentException("Gmail display name is invalid.", nameof(displayName));

        var pkce = PkcePair.Generate();
        var state = NewState();
        var scopes = options.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var pending = new Pending(ownerPrincipalId, label, options.ClientId, options.ClientSecretRef,
            redirectUri, scopes, pkce.Verifier, _timeProvider.GetUtcNow() + _ttl);
        lock (_gate)
        {
            SweepExpired();
            if (_pending.Count >= _capacity)
            {
                var evict = _pending.MinBy(item => item.Value.ExpiresAt).Key;
                _pending.Remove(evict);
            }
            _pending[state] = pending;
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = PkcePair.Method,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
        };
        return new(new UriBuilder(AuthorizationEndpoint) { Query = EncodeForm(query) }.Uri);
    }

    public async Task<GmailOAuthCompletion> CompleteAsync(string? state, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code)) return Failed("invalid_oauth_callback");
        Pending? pending;
        lock (_gate)
        {
            SweepExpired();
            pending = _pending.Remove(state, out var value) ? value : null;
        }
        if (pending is null) return Failed("oauth_state_invalid_or_expired");

        CredentialBundle clientCredential;
        try { clientCredential = await _custody.GetBundleAsync(pending.ClientSecretRef, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is StoreException or IOException) { return Failed("oauth_client_secret_unavailable"); }
        var clientSecret = clientCredential.Extra?.GetValueOrDefault(ClientSecretExtraKey) ?? clientCredential.AccessToken;
        if (string.IsNullOrWhiteSpace(clientSecret)) return Failed("oauth_client_secret_unavailable");

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = pending.ClientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = pending.RedirectUri.ToString(),
            ["code_verifier"] = pending.Verifier,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept"] = "application/json",
        };
        TransportResponse response;
        try { response = await _transport.SendAsync("POST", TokenEndpoint.ToString(), headers, EncodeForm(form), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Failed("oauth_token_exchange_failed"); }
        if (response.Status is 400 or 401) return Failed("oauth_grant_rejected");
        if (response.Status is < 200 or >= 300 || Encoding.UTF8.GetByteCount(response.Body) > 64 * 1024) return Failed("oauth_token_exchange_failed");

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var accessToken = StringProperty(root, "access_token");
            var refreshToken = StringProperty(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken)) return Failed("oauth_refresh_token_required");
            var granted = StringProperty(root, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? pending.Scopes;
            if (!granted.Contains("https://www.googleapis.com/auth/gmail.readonly", StringComparer.Ordinal)) return Failed("gmail_read_scope_required");
            var extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ScopesExtraKey] = string.Join(' ', granted.Order(StringComparer.Ordinal)),
                [TokenEndpointExtraKey] = TokenEndpoint.ToString(),
                [ClientIdExtraKey] = pending.ClientId,
            };
            if (root.TryGetProperty("expires_in", out var expiresValue) && expiresValue.TryGetInt64(out var expiresSeconds) && expiresSeconds is > 0 and <= 86400)
                extra[AccessExpiresAtExtraKey] = (_timeProvider.GetUtcNow() + TimeSpan.FromSeconds(expiresSeconds)).ToString("O");
            return new(true, pending.OwnerPrincipalId, pending.DisplayName, new CredentialBundle(accessToken, refreshToken, Extra: extra), granted, null);
        }
        catch (JsonException) { return Failed("oauth_token_response_malformed"); }
    }

    public async Task<GmailRefreshResult> RefreshIfNeededAsync(string credentialRef, GmailOAuthOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);
        ArgumentNullException.ThrowIfNull(options);
        CredentialBundle current;
        try { current = await _custody.GetBundleAsync(credentialRef, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is StoreException or IOException) { return new(GmailRefreshStatus.Error, "credential_store_unavailable"); }
        if (!current.HasRefreshToken) return new(GmailRefreshStatus.AuthRequired, "oauth_refresh_token_required");
        if (current.Extra?.TryGetValue(AccessExpiresAtExtraKey, out var rawExpiry) == true
            && DateTimeOffset.TryParse(rawExpiry, out var expiry)
            && expiry - _timeProvider.GetUtcNow() > TimeSpan.FromMinutes(10))
            return new(GmailRefreshStatus.NotDue);
        if (_custody is not ICredentialWriter writer) return new(GmailRefreshStatus.Error, "credential_store_not_writable");
        CredentialBundle clientCredential;
        try { clientCredential = await _custody.GetBundleAsync(options.ClientSecretRef, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is StoreException or IOException) { return new(GmailRefreshStatus.Error, "oauth_client_secret_unavailable"); }
        var clientSecret = clientCredential.Extra?.GetValueOrDefault(ClientSecretExtraKey) ?? clientCredential.AccessToken;
        if (string.IsNullOrWhiteSpace(clientSecret)) return new(GmailRefreshStatus.Error, "oauth_client_secret_unavailable");
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken!,
            ["client_id"] = options.ClientId,
            ["client_secret"] = clientSecret,
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept"] = "application/json",
        };
        TransportResponse response;
        try { response = await _transport.SendAsync("POST", TokenEndpoint.ToString(), headers, EncodeForm(form), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(GmailRefreshStatus.Error, "oauth_refresh_failed"); }
        if (response.Status is 400 or 401) return new(GmailRefreshStatus.AuthRequired, "oauth_grant_rejected");
        if (response.Status is < 200 or >= 300 || Encoding.UTF8.GetByteCount(response.Body) > 64 * 1024) return new(GmailRefreshStatus.Error, "oauth_refresh_failed");
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var access = StringProperty(root, "access_token");
            if (string.IsNullOrWhiteSpace(access)) return new(GmailRefreshStatus.Error, "oauth_token_response_malformed");
            var refresh = StringProperty(root, "refresh_token") ?? current.RefreshToken;
            var extra = new Dictionary<string, string>(current.Extra ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            if (root.TryGetProperty("scope", out var scopes) && scopes.ValueKind == JsonValueKind.String) extra[ScopesExtraKey] = scopes.GetString()!;
            if (root.TryGetProperty("expires_in", out var expiresValue) && expiresValue.TryGetInt64(out var expiresSeconds) && expiresSeconds is > 0 and <= 86400)
                extra[AccessExpiresAtExtraKey] = (_timeProvider.GetUtcNow() + TimeSpan.FromSeconds(expiresSeconds)).ToString("O");
            await writer.PutBundleAsync(credentialRef, current with { AccessToken = access, RefreshToken = refresh, Extra = extra }, cancellationToken).ConfigureAwait(false);
            return new(GmailRefreshStatus.Refreshed);
        }
        catch (JsonException) { return new(GmailRefreshStatus.Error, "oauth_token_response_malformed"); }
        catch (StoreException) { return new(GmailRefreshStatus.Error, "credential_store_unavailable"); }
    }

    public async Task<bool> RevokeAsync(CredentialBundle bundle, CancellationToken cancellationToken = default)
    {
        var token = bundle.RefreshToken ?? bundle.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) return true;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept"] = "application/json",
        };
        try
        {
            var response = await _transport.SendAsync("POST", "https://oauth2.googleapis.com/revoke", headers, EncodeForm(new Dictionary<string, string> { ["token"] = token }), cancellationToken).ConfigureAwait(false);
            return response.Status is >= 200 and < 300;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var state in _pending.Where(item => item.Value.ExpiresAt < now).Select(item => item.Key).ToArray()) _pending.Remove(state);
    }

    private static GmailOAuthCompletion Failed(string code) => new(false, null, null, null, [], code);
    private static string? StringProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string NewState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    private static string EncodeForm(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join('&', values.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));

    private sealed record Pending(
        string OwnerPrincipalId,
        string DisplayName,
        string ClientId,
        string ClientSecretRef,
        Uri RedirectUri,
        string[] Scopes,
        string Verifier,
        DateTimeOffset ExpiresAt);
}