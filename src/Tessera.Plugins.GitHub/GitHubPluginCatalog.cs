using System.Text.Json;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;

namespace Tessera.Plugins.GitHub;

public sealed partial class GitHubPlugin
{
    public PluginCatalogSourceDescriptor CatalogSource { get; } = new(
        "github",
        "GitHub public repositories",
        TimeSpan.FromHours(1));

    public async Task<IReadOnlyList<PluginCatalogItem>> SearchCatalogAsync(
        string query,
        int limit,
        IHttpTransport transport,
        CancellationToken cancellationToken = default)
    {
        var terms = query.Replace('-', ' ').Trim();
        var url = "https://api.github.com/search/repositories"
            + $"?q={Uri.EscapeDataString($"{terms} mcp in:name,description,readme")}&per_page={limit}";
        var response = await transport.SendAsync(
                "GET",
                url,
                new Dictionary<string, string>
                {
                    ["Accept"] = "application/vnd.github+json",
                    ["X-GitHub-Api-Version"] = "2022-11-28",
                    ["User-Agent"] = "Tessera/2.0 integration-catalog",
                },
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Status is < 200 or >= 300)
            throw new HttpRequestException("GitHub catalog unavailable.");
        using var document = JsonDocument.Parse(response.Body);
        if (!document.RootElement.TryGetProperty("items", out var repositories)
            || repositories.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub catalog response is invalid.");
        return repositories.EnumerateArray()
            .Take(limit)
            .Select(ParseCatalogItem)
            .ToArray();
    }

    private static PluginCatalogItem ParseCatalogItem(JsonElement value)
    {
        var id = RequiredCatalogString(value, "full_name", 200);
        var name = RequiredCatalogString(value, "name", 100);
        var description = OptionalCatalogString(value, "description", 500)
            ?? "Public GitHub repository matching this MCP integration search.";
        var repository = RequiredCatalogString(value, "html_url", 500);
        var publisher = value.TryGetProperty("owner", out var owner)
            && owner.ValueKind == JsonValueKind.Object
            ? RequiredCatalogString(owner, "login", 100)
            : id.Split('/', 2)[0];
        var license = value.TryGetProperty("license", out var licenseValue)
            && licenseValue.ValueKind == JsonValueKind.Object
            ? OptionalCatalogString(licenseValue, "spdx_id", 64)
            : null;
        return new(
            $"github:{id}",
            name.Replace('-', ' '),
            description,
            publisher,
            "MCP candidate",
            repository,
            OptionalCatalogString(value, "default_branch", 255) ?? "unknown",
            string.Equals(license, "NOASSERTION", StringComparison.Ordinal) ? null : license,
            "UNTRUSTED",
            [description],
            [],
            "SERVER_REVIEW_REQUIRED",
            "REVIEW_REQUIRED",
            false,
            repository);
    }

    private static string RequiredCatalogString(JsonElement value, string property, int maximumLength)
        => OptionalCatalogString(value, property, maximumLength)
            ?? throw new InvalidDataException($"GitHub field '{property}' is missing.");

    private static string? OptionalCatalogString(JsonElement value, string property, int maximumLength)
    {
        if (!value.TryGetProperty(property, out var item)
            || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (item.ValueKind != JsonValueKind.String
            || item.GetString() is not { } text
            || text.Length == 0
            || text.Length > maximumLength
            || text.Any(char.IsControl))
            throw new InvalidDataException($"GitHub field '{property}' is invalid.");
        return text;
    }
}