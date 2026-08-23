using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Stores;
using Tessera.Providers;

namespace Tessera.Plugins.OneDrive;

public sealed class OneDriveOAuthOptions
{
    public bool Enabled { get; init; }
    public string ClientId { get; init; } = "";
    public string ClientSecretRef { get; init; } = "";
    public string RedirectUri { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "profile", "offline_access", "Files.Read"];
}

public sealed record OneDriveOAuthStart(Uri AuthorizeUrl);
public sealed record OneDriveOAuthCompletion(
    bool Succeeded,
    string? OwnerPrincipalId,
    string? DisplayName,
    CredentialBundle? Credentials,
    IReadOnlyList<string> GrantedScopes,
    string? ErrorCode);
public enum OneDriveRefreshStatus { NotDue, Refreshed, AuthRequired, Error }
public sealed record OneDriveRefreshResult(OneDriveRefreshStatus Status, string? ErrorCode = null);

public sealed class OneDriveOAuthService
{
    public const string ClientSecretExtraKey = "client_secret";
    public const string ScopesExtraKey = "oauth_scopes";
    public const string AccessExpiresAtExtraKey = "access_expires_at";
    private static readonly string[] RequiredScopes = ["openid", "profile", "offline_access", "Files.Read"];
    private static readonly Uri AuthorizationEndpoint = new("https://login.microsoftonline.com/common/oauth2/v2.0/authorize");
    private static readonly Uri TokenEndpoint = new("https://login.microsoftonline.com/common/oauth2/v2.0/token");
    private readonly IHttpTransport _transport;
    private readonly ICredentialStore _custody;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public OneDriveOAuthService(IHttpTransport transport, ICredentialStore custody, TimeProvider? timeProvider = null, TimeSpan? ttl = null, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(custody);
        _transport = transport;
        _custody = custody;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _capacity = Math.Max(1, capacity);
    }

    public OneDriveOAuthStart Begin(string ownerPrincipalId, string displayName, OneDriveOAuthOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecretRef)
            || !Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirectUri))
            throw new InvalidOperationException("onedrive_oauth_not_configured");
        var label = displayName.Trim();
        if (label.Length > 256 || label.Any(char.IsControl)) throw new ArgumentException("OneDrive display name is invalid.", nameof(displayName));
        ValidateScopes(options.Scopes);
        var pkce = PkcePair.Generate();
        var state = NewState();
        var scopes = options.Scopes.Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            SweepExpired();
            if (_pending.Count >= _capacity) _pending.Remove(_pending.MinBy(item => item.Value.ExpiresAt).Key);
            _pending[state] = new(ownerPrincipalId, label, options.ClientId, options.ClientSecretRef, redirectUri, scopes, pkce.Verifier, _timeProvider.GetUtcNow() + _ttl);
        }
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = PkcePair.Method,
        };
        return new(new UriBuilder(AuthorizationEndpoint) { Query = EncodeForm(query) }.Uri);
    }

    public async Task<OneDriveOAuthCompletion> CompleteAsync(string? state, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code)) return Failed("invalid_oauth_callback");
        Pending? pending;
        lock (_gate)
        {
            SweepExpired();
            pending = _pending.Remove(state, out var value) ? value : null;
        }
        if (pending is null) return Failed("oauth_state_invalid_or_expired");
        var clientSecret = await ClientSecretAsync(pending.ClientSecretRef, cancellationToken).ConfigureAwait(false);
        if (clientSecret is null) return Failed("oauth_client_secret_unavailable");
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = pending.ClientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = pending.RedirectUri.ToString(),
            ["code_verifier"] = pending.Verifier,
            ["scope"] = string.Join(' ', pending.Scopes),
        };
        var response = await SendTokenAsync(form, cancellationToken).ConfigureAwait(false);
        if (response.ErrorCode is not null) return Failed(response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            var access = StringProperty(root, "access_token");
            var refresh = StringProperty(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh)) return Failed("oauth_refresh_token_required");
            var granted = Scopes(root, pending.Scopes);
            if (!HasRequiredScopes(granted)) return Failed("onedrive_required_scopes_missing");
            return new(true, pending.OwnerPrincipalId, pending.DisplayName, new(access, refresh, Extra: Extras(root, granted)), granted, null);
        }
        catch (JsonException) { return Failed("oauth_token_response_malformed"); }
    }

    public async Task<OneDriveRefreshResult> RefreshIfNeededAsync(string credentialRef, OneDriveOAuthOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);
        CredentialBundle current;
        try { current = await _custody.GetBundleAsync(credentialRef, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is StoreException or IOException) { return new(OneDriveRefreshStatus.Error, "credential_store_unavailable"); }
        if (!current.HasRefreshToken) return new(OneDriveRefreshStatus.AuthRequired, "oauth_refresh_token_required");
        if (current.Extra?.TryGetValue(AccessExpiresAtExtraKey, out var rawExpiry) == true
            && DateTimeOffset.TryParse(rawExpiry, out var expiry)
            && expiry - _timeProvider.GetUtcNow() > TimeSpan.FromMinutes(10))
            return new(OneDriveRefreshStatus.NotDue);
        if (_custody is not ICredentialWriter writer) return new(OneDriveRefreshStatus.Error, "credential_store_not_writable");
        var clientSecret = await ClientSecretAsync(options.ClientSecretRef, cancellationToken).ConfigureAwait(false);
        if (clientSecret is null) return new(OneDriveRefreshStatus.Error, "oauth_client_secret_unavailable");
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken!,
            ["client_id"] = options.ClientId,
            ["client_secret"] = clientSecret,
            ["scope"] = string.Join(' ', options.Scopes),
        };
        var response = await SendTokenAsync(form, cancellationToken).ConfigureAwait(false);
        if (response.ErrorCode == "oauth_grant_rejected") return new(OneDriveRefreshStatus.AuthRequired, response.ErrorCode);
        if (response.ErrorCode is not null) return new(OneDriveRefreshStatus.Error, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            var access = StringProperty(root, "access_token");
            var refresh = StringProperty(root, "refresh_token");
            var granted = Scopes(root, options.Scopes);
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) || !HasRequiredScopes(granted))
                return new(OneDriveRefreshStatus.Error, "oauth_token_response_malformed");
            await writer.PutBundleAsync(credentialRef, current with { AccessToken = access, RefreshToken = refresh, Extra = Extras(root, granted) }, cancellationToken).ConfigureAwait(false);
            return new(OneDriveRefreshStatus.Refreshed);
        }
        catch (JsonException) { return new(OneDriveRefreshStatus.Error, "oauth_token_response_malformed"); }
        catch (StoreException) { return new(OneDriveRefreshStatus.Error, "credential_store_unavailable"); }
    }

    private async Task<string?> ClientSecretAsync(string reference, CancellationToken token)
    {
        try
        {
            var credential = await _custody.GetBundleAsync(reference, token).ConfigureAwait(false);
            return credential.Extra?.GetValueOrDefault(ClientSecretExtraKey) ?? credential.AccessToken;
        }
        catch (Exception exception) when (exception is StoreException or IOException) { return null; }
    }

    private async Task<TokenResult> SendTokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept"] = "application/json",
        };
        TransportResponse response;
        try { response = await _transport.SendAsync("POST", TokenEndpoint.ToString(), headers, EncodeForm(form), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null, "oauth_token_exchange_failed"); }
        if (response.Status is 400 or 401) return new(null, "oauth_grant_rejected");
        if (response.Status is < 200 or >= 300 || Encoding.UTF8.GetByteCount(response.Body) > 64 * 1024)
            return new(null, "oauth_token_exchange_failed");
        return new(response.Body, null);
    }

    internal static void ValidateScopes(IReadOnlyList<string> scopes)
    {
        var allowed = new HashSet<string>(["openid", "profile", "offline_access", "Files.Read"], StringComparer.Ordinal);
        if (!HasRequiredScopes(scopes) || scopes.Any(scope => !allowed.Contains(scope)))
            throw new InvalidOperationException("onedrive_oauth_scopes_invalid");
    }

    private static bool HasRequiredScopes(IReadOnlyList<string> scopes)
        => RequiredScopes.All(required => scopes.Contains(required, StringComparer.Ordinal));

    private Dictionary<string, string> Extras(JsonElement root, IReadOnlyList<string> scopes)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal) { [ScopesExtraKey] = string.Join(' ', scopes) };
        if (root.TryGetProperty("expires_in", out var expiresValue) && expiresValue.TryGetInt64(out var seconds) && seconds is > 0 and <= 86400)
            extra[AccessExpiresAtExtraKey] = (_timeProvider.GetUtcNow() + TimeSpan.FromSeconds(seconds)).ToString("O");
        return extra;
    }

    private void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var state in _pending.Where(item => item.Value.ExpiresAt < now).Select(item => item.Key).ToArray()) _pending.Remove(state);
    }

    private static OneDriveOAuthCompletion Failed(string code) => new(false, null, null, null, [], code);
    private static string[] Scopes(JsonElement root, IReadOnlyList<string> fallback)
        => StringProperty(root, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? fallback.ToArray();
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

    private sealed record Pending(string OwnerPrincipalId, string DisplayName, string ClientId, string ClientSecretRef, Uri RedirectUri, string[] Scopes, string Verifier, DateTimeOffset ExpiresAt);
    private sealed record TokenResult(string? Body, string? ErrorCode);
}