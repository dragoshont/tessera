using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tessera.Core.Product;

public static class ProductContentValidation
{
    private static readonly string[] SecretMarkers =
    [
        "authorization: bearer",
        "password=",
        "password:",
        "api_key=",
        "api-key=",
        "apikey=",
        "access_token=",
        "refresh_token=",
        "client_secret=",
        "private_key",
    ];
    private static readonly Regex[] CredentialPatterns =
    [
        new(@"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{20,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    public static string Text(string value, string parameterName, int maximumCharacters = 16_384)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumCharacters || Encoding.UTF8.GetByteCount(trimmed) > maximumCharacters * 4)
            throw new ArgumentException("Product text exceeds the permitted bound.", parameterName);
        if (trimmed.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("Product text contains unsupported control characters.", parameterName);
        var lower = trimmed.ToLowerInvariant();
        if (SecretMarkers.Any(lower.Contains)
            || CredentialPatterns.Any(pattern => pattern.IsMatch(trimmed))
            || (lower.Contains("bearer ", StringComparison.Ordinal)
                && trimmed.Length - lower.IndexOf("bearer ", StringComparison.Ordinal) > 24))
            throw new ArgumentException("Product content appears to contain credential material.", parameterName);
        if (trimmed[0] is '{' or '[')
        {
            try { using var document=JsonDocument.Parse(trimmed);ValidateElement(document.RootElement,parameterName,0); }
            catch(JsonException) { }
        }
        return trimmed;
    }

    public static JsonElement Json(JsonElement value, string parameterName, int maximumBytes = 32 * 1024)
    {
        var raw = value.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > maximumBytes)
            throw new ArgumentException("Structured product content exceeds the permitted bound.", parameterName);
        ValidateElement(value, parameterName, 0);
        Text(raw, parameterName, maximumBytes);
        return value.Clone();
    }

    private static void ValidateElement(JsonElement value, string parameterName, int depth)
    {
        if (depth > 12) throw new ArgumentException("Structured product content is too deeply nested.", parameterName);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Length > 128) throw new ArgumentException("Structured product content has too many properties.", parameterName);
                foreach (var property in properties)
                {
                    Text(property.Name, parameterName, 128);
                    if (IsSecretProperty(property.Name))
                        throw new ArgumentException("Structured product content contains a credential-like property.", parameterName);
                    ValidateElement(property.Value, parameterName, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                var items = value.EnumerateArray().ToArray();
                if (items.Length > 256) throw new ArgumentException("Structured product content has too many array items.", parameterName);
                foreach (var item in items) ValidateElement(item, parameterName, depth + 1);
                break;
            case JsonValueKind.String:
                Text(value.GetString() ?? string.Empty, parameterName, 16_384);
                break;
        }
    }

    private static bool IsSecretProperty(string name)
    {
        var normalized=new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is "password" or "passwd" or "authorization" or "credential" or "credentials"
            or "apikey" or "accesskey" or "secretkey" or "clientsecret" or "privatekey"
            or "token" or "secret" or "authtoken" or "bearertoken" or "oauthtoken"
            or "personalaccesstoken" or "secrettoken" or "pat"
            or "accesstoken" or "refreshtoken" or "idtoken";
    }
}
