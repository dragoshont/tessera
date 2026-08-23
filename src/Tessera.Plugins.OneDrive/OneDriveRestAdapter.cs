using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Tessera.Providers;

namespace Tessera.Plugins.OneDrive;

public sealed record OneDriveIdentity(string DriveId, string DriveType, string? OwnerDisplayName);
public sealed record OneDriveItemMetadata(
    string Id,
    string Name,
    long Size,
    bool IsFolder,
    int? ChildCount,
    string? MimeType,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastModifiedAt);
public sealed record OneDriveIdentityResult(bool Succeeded, OneDriveIdentity? Identity, string? ErrorCode = null);
public sealed record OneDriveListResult(bool Succeeded, IReadOnlyList<OneDriveItemMetadata> Items, string? Cursor, string? ErrorCode = null);
public sealed record OneDriveItemResult(bool Succeeded, OneDriveItemMetadata? Item, string? ErrorCode = null);

public sealed class OneDriveRestAdapter(IHttpTransport transport, int maximumResponseBytes = 256 * 1024)
{
    private const string GraphOrigin = "https://graph.microsoft.com/";
    private const string ApiRoot = "v1.0/me/drive/";
    private const int MaximumItems = 25;
    private const int MaximumIdCharacters = 2_048;
    private const int MaximumCursorCharacters = 2_048;

    public async Task<OneDriveIdentityResult> ValidateAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(ApiRoot.TrimEnd('/'), accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, null, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            var owner = root.TryGetProperty("owner", out var ownerValue)
                && ownerValue.ValueKind == JsonValueKind.Object
                && ownerValue.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object
                ? OptionalText(user, "displayName", 512)
                : null;
            return new(true, new(RequiredText(root, "id", MaximumIdCharacters), RequiredText(root, "driveType", 64), owner));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(false, null, "provider_malformed");
        }
    }

    public async Task<OneDriveListResult> ListChildrenAsync(
        string accessToken,
        string? folderId = null,
        int maximumItems = MaximumItems,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems is < 1 or > MaximumItems) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (folderId is not null) ValidateIdentifier(folderId, nameof(folderId));
        if (cursor is not null && folderId is not null) throw new ArgumentException("A OneDrive cursor cannot be combined with a folder ID.", nameof(cursor));
        string path;
        try
        {
            path = cursor is null
                ? folderId is null
                    ? $"{ApiRoot}root/children?$top={maximumItems}"
                    : $"{ApiRoot}items/{Uri.EscapeDataString(folderId)}/children?$top={maximumItems}"
                : DecodeCursor(cursor);
        }
        catch (FormatException)
        {
            return new(false, [], null, "invalid_cursor");
        }
        var response = await SendAsync(path, accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, [], null, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            if (!root.TryGetProperty("value", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() > maximumItems)
                throw new InvalidDataException("OneDrive children are malformed or exceed the requested bound.");
            var items = values.EnumerateArray().Select(ParseItem).ToArray();
            var next = OptionalText(root, "@odata.nextLink", 8 * 1024);
            return new(true, items, next is null ? null : EncodeCursor(ValidateNextLink(next)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(false, [], null, "provider_malformed");
        }
    }

    public async Task<OneDriveItemResult> GetItemAsync(string accessToken, string itemId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(itemId, nameof(itemId));
        var response = await SendAsync($"{ApiRoot}items/{Uri.EscapeDataString(itemId)}", accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, null, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var item = ParseItem(document.RootElement);
            return item.Id == itemId ? new(true, item) : new(false, null, "provider_malformed");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(false, null, "provider_malformed");
        }
    }

    private async Task<RawResult> SendAsync(string path, string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return new(false, null, "provider_auth_required");
        if (!IsAllowedPath(path)) return new(false, null, "invalid_cursor");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Accept"] = "application/json",
        };
        TransportResponse response;
        try { response = await transport.SendAsync("GET", GraphOrigin + path, headers, null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, null, "provider_timeout"); }
        catch (TransportResponseTooLargeException) { return new(false, null, "provider_result_too_large"); }
        catch (Exception) { return new(false, null, "provider_unavailable"); }
        if (response.Status == 401) return new(false, null, "provider_auth_required");
        if (response.Status == 403) return new(false, null, "provider_forbidden");
        if (response.Status == 404) return new(false, null, "provider_not_found");
        if (response.Status == 429) return new(false, null, "rate_limited");
        if (response.Status is < 200 or >= 300) return new(false, null, "provider_unavailable");
        if (Encoding.UTF8.GetByteCount(response.Body) > maximumResponseBytes) return new(false, null, "provider_result_too_large");
        try { using var _ = JsonDocument.Parse(response.Body); }
        catch (JsonException) { return new(false, null, "provider_malformed"); }
        return new(true, response.Body, null);
    }

    private static OneDriveItemMetadata ParseItem(JsonElement item)
    {
        var id = RequiredText(item, "id", MaximumIdCharacters);
        var name = RequiredText(item, "name", 512);
        var size = RequiredNonNegativeInt64(item, "size");
        var isFolder = item.TryGetProperty("folder", out var folder);
        var isFile = item.TryGetProperty("file", out var file);
        if (isFolder == isFile) throw new InvalidDataException("OneDrive item type is malformed.");
        int? childCount = null;
        string? mimeType = null;
        if (isFolder)
        {
            if (folder.ValueKind != JsonValueKind.Object
                || !folder.TryGetProperty("childCount", out var count)
                || !count.TryGetInt32(out var parsedCount)
                || parsedCount < 0)
                throw new InvalidDataException("OneDrive folder metadata is malformed.");
            childCount = parsedCount;
        }
        else
        {
            if (file.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OneDrive file metadata is malformed.");
            mimeType = OptionalText(file, "mimeType", 256);
        }
        return new(id, name, size, isFolder, childCount, mimeType,
            OptionalDateTime(item, "createdDateTime"), OptionalDateTime(item, "lastModifiedDateTime"));
    }

    private static string ValidateNextLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "graph.microsoft.com"
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("OneDrive next link host is invalid.");
        var path = uri.PathAndQuery.TrimStart('/');
        if (!IsAllowedChildrenPagePath(path)) throw new InvalidDataException("OneDrive next link path is invalid.");
        return path;
    }

    private static bool IsAllowedPath(string value)
        => (value == ApiRoot.TrimEnd('/') || value.StartsWith(ApiRoot, StringComparison.Ordinal))
            && !value.Contains("//", StringComparison.Ordinal)
            && !value.Any(char.IsControl);

    private static bool IsAllowedChildrenPagePath(string value)
    {
        if (!IsAllowedPath(value) || !Uri.TryCreate(GraphOrigin + value, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath;
        var rootChildren = path == "/v1.0/me/drive/root/children";
        var itemChildren = path.StartsWith("/v1.0/me/drive/items/", StringComparison.Ordinal)
            && path.EndsWith("/children", StringComparison.Ordinal)
            && path["/v1.0/me/drive/items/".Length..^"/children".Length].Length > 0
            && !path["/v1.0/me/drive/items/".Length..^"/children".Length].Contains('/', StringComparison.Ordinal);
        if (!rootChildren && !itemChildren) return false;
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .All(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0]) is "$top" or "$skiptoken");
    }

    private static string EncodeCursor(string path)
    {
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(path));
        if (cursor.Length > MaximumCursorCharacters) throw new InvalidDataException("OneDrive cursor exceeds the bound.");
        return cursor;
    }

    private static string DecodeCursor(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumCursorCharacters || cursor.Any(char.IsControl))
            throw new FormatException("OneDrive cursor is invalid.");
        var bytes = new byte[cursor.Length];
        if (!Base64Url.TryDecodeFromChars(cursor, bytes, out var written)) throw new FormatException("OneDrive cursor is invalid.");
        var path = new UTF8Encoding(false, true).GetString(bytes, 0, written);
        return IsAllowedChildrenPagePath(path) ? path : throw new FormatException("OneDrive cursor is invalid.");
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdCharacters || value.Any(char.IsControl))
            throw new ArgumentException("OneDrive item ID is invalid.", parameterName);
    }

    private static string RequiredText(JsonElement value, string property, int maximumCharacters)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"OneDrive {property} is missing.");
        var result = item.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximumCharacters || result.Any(char.IsControl))
            throw new InvalidDataException($"OneDrive {property} is malformed.");
        return result.Trim();
    }

    private static string? OptionalText(JsonElement value, string property, int maximumCharacters)
        => !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null
            : item.ValueKind == JsonValueKind.String
                ? RequiredText(value, property, maximumCharacters)
                : throw new InvalidDataException($"OneDrive {property} is malformed.");

    private static long RequiredNonNegativeInt64(JsonElement value, string property)
        => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt64(out var result) && result >= 0
                ? result
                : throw new InvalidDataException($"OneDrive {property} is malformed.");

    private static DateTimeOffset? OptionalDateTime(JsonElement value, string property)
    {
        var text = OptionalText(value, property, 64);
        return text is null ? null : DateTimeOffset.TryParse(text, out var parsed)
            ? parsed
            : throw new InvalidDataException($"OneDrive {property} is malformed.");
    }

    private sealed record RawResult(bool Succeeded, string? Body, string? ErrorCode);
}