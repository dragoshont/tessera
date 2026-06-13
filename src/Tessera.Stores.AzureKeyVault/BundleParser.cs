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

    /// <summary>Serializes a bundle to the stored JSON shape (the harvester's contract).</summary>
    public static string Serialize(CredentialBundle bundle)
    {
        var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(bundle.AccessToken))
        {
            obj["access_token"] = bundle.AccessToken;
        }

        if (!string.IsNullOrEmpty(bundle.RefreshToken))
        {
            obj["refresh_token"] = bundle.RefreshToken;
        }

        if (bundle.Cookies is { Count: > 0 })
        {
            obj["cookies"] = bundle.Cookies;
        }

        if (bundle.Extra is { Count: > 0 })
        {
            obj["extra"] = bundle.Extra;
        }

        return JsonSerializer.Serialize(obj);
    }

    /// <summary>
    /// Merges the rotated <paramref name="bundle"/> into the existing stored JSON,
    /// overlaying only the modelled fields (access_token, refresh_token, cookies,
    /// extra) and preserving any other fields the harvester stored.
    /// </summary>
    public static string Merge(string? existingJson, CredentialBundle bundle)
    {
        var node = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        node[p.Name] = p.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                node.Clear();
            }
        }

        // Re-serialize: start from the existing fields, overlay the modelled ones.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);

            void WriteModelled()
            {
                if (!string.IsNullOrEmpty(bundle.AccessToken)) { writer.WriteString("access_token", bundle.AccessToken); written.Add("access_token"); }
                if (!string.IsNullOrEmpty(bundle.RefreshToken)) { writer.WriteString("refresh_token", bundle.RefreshToken); written.Add("refresh_token"); }
                if (bundle.Cookies is { Count: > 0 })
                {
                    writer.WritePropertyName("cookies");
                    writer.WriteStartObject();
                    foreach (var (k, v) in bundle.Cookies) { writer.WriteString(k, v); }
                    writer.WriteEndObject();
                    written.Add("cookies");
                }

                if (bundle.Extra is { Count: > 0 })
                {
                    writer.WritePropertyName("extra");
                    writer.WriteStartObject();
                    foreach (var (k, v) in bundle.Extra) { writer.WriteString(k, v); }
                    writer.WriteEndObject();
                    written.Add("extra");
                }
            }

            WriteModelled();

            // Preserve any existing fields the model didn't overwrite.
            foreach (var (name, value) in node)
            {
                if (!written.Contains(name))
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
