using System.Text.Json;
using Tessera.Core.Stores;

namespace Tessera.Stores.AzureKeyVault;

/// <summary>
/// Parses a stored Key Vault secret value (the harvester's JSON bundle) into a
/// <see cref="CredentialBundle"/>. Tolerant: unknown shapes degrade to empty
/// rather than throwing, and only string material is lifted out.
/// </summary>
internal static class BundleParser
{
    public static CredentialBundle Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CredentialBundle.Empty;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return CredentialBundle.Empty;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CredentialBundle.Empty;
            }

            return new CredentialBundle(
                AccessToken: GetString(root, "access_token"),
                RefreshToken: GetString(root, "refresh_token"),
                Cookies: GetStringMap(root, "cookies"),
                Extra: GetStringMap(root, "extra"));
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static Dictionary<string, string>? GetStringMap(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in prop.EnumerateObject())
        {
            map[item.Name] = item.Value.ValueKind == JsonValueKind.String
                ? item.Value.GetString() ?? ""
                : item.Value.GetRawText();
        }

        return map.Count > 0 ? map : null;
    }
}
