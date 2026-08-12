using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using Tessera.Core.Product;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Tessera.Providers.R2;

namespace Tessera.Broker;

internal sealed record IntegrationCatalogItem(
    string Id,
    string Name,
    string Description,
    string Source,
    string Publisher,
    string Runtime,
    string? RepositoryOrPackage,
    string Version,
    string? License,
    string TrustLevel,
    IReadOnlyList<string> CapabilitiesSummary,
    IReadOnlyList<string> AuthTypes,
    string Sensitivity,
    string InstallationMode,
    string InstallState,
    bool Installed,
    string? InspectUrl);

internal sealed record IntegrationCatalogSourceStatus(
    string Id,
    string Name,
    string State,
    string? ErrorCode);

internal sealed record IntegrationCatalogSearchResult(
    IReadOnlyList<IntegrationCatalogItem> Items,
    IReadOnlyList<IntegrationCatalogSourceStatus> Sources);

internal interface IIntegrationCatalogSource
{
    string Id { get; }
    string Name { get; }
    Task<IReadOnlyList<IntegrationCatalogItem>> SearchAsync(
        string query,
        int limit,
        IReadOnlySet<string> installedPluginIds,
        CancellationToken cancellationToken);
}

internal sealed class IntegrationCatalogService
{
    private readonly IReadOnlyList<IIntegrationCatalogSource> _sources;

    public IntegrationCatalogService(
        R2PluginCatalog? localCatalog,
        IHttpTransport transport,
        IReadOnlyList<ITesseraCatalogPlugin>? catalogPlugins = null)
    {
        var sources = new List<IIntegrationCatalogSource>();
        if (localCatalog is not null) sources.Add(new LocalIntegrationCatalogSource(localCatalog));
        sources.Add(new McpRegistryCatalogSource(transport));
        sources.AddRange((catalogPlugins ?? [])
            .GroupBy(plugin => plugin.CatalogSource.Id, StringComparer.Ordinal)
            .Select(group => new PluginIntegrationCatalogSource(group.First(), transport)));
        _sources = sources.AsReadOnly();
    }

    public IReadOnlyList<IntegrationCatalogSourceStatus> ListSources()
        => _sources
            .Select(source => new IntegrationCatalogSourceStatus(
                source.Id,
                source.Name,
                "READY",
                null))
            .ToArray();

    public async Task<IntegrationCatalogSearchResult> SearchAsync(
        string query,
        int limit,
        IReadOnlySet<string> installedPluginIds,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        var items = new List<IntegrationCatalogItem>();
        var statuses = new List<IntegrationCatalogSourceStatus>();
        foreach (var source in _sources)
        {
            try
            {
                var values = await source.SearchAsync(
                        normalized,
                        Math.Min(limit, 20),
                        installedPluginIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                items.AddRange(values);
                statuses.Add(new(source.Id, source.Name, "READY", null));
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or TaskCanceledException
                    or JsonException
                    or TransportResponseTooLargeException
                    or InvalidDataException)
            {
                statuses.Add(new(source.Id, source.Name, "DEGRADED", "source_unavailable"));
            }
        }

        var ordered = items
            .GroupBy(item => $"{item.Source}\n{item.Id}\n{item.Version}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => Rank(item, normalized))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        return new(ordered, statuses.ToArray());
    }

    private static string NormalizeQuery(string query)
    {
        var value = query.Trim();
        if (value.Length is < 2 or > 100 || value.Any(char.IsControl))
            throw new ArgumentException("invalid_query", nameof(query));
        return value;
    }

    private static int Rank(IntegrationCatalogItem item, string query)
    {
        if (item.Installed) return 0;
        if (string.Equals(item.Name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Id, query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (item.Source == "local") return 2;
        if (item.TrustLevel == "VERIFIED_METADATA") return 3;
        return 4;
    }
}

internal sealed class LocalIntegrationCatalogSource(R2PluginCatalog catalog)
    : IIntegrationCatalogSource
{
    public string Id => "local";
    public string Name => "Installed and local";

    public Task<IReadOnlyList<IntegrationCatalogItem>> SearchAsync(
        string query,
        int limit,
        IReadOnlySet<string> installedPluginIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = catalog.ListPackages()
            .Where(package => Matches(package.Manifest, query))
            .Take(limit)
            .Select(package =>
            {
                var accountRequired = package.Manifest.Capabilities.Any(capability => capability.AccountRequired);
                var installed = installedPluginIds.Contains(package.Manifest.Id);
                return new IntegrationCatalogItem(
                    package.Manifest.Id,
                    package.Manifest.Name,
                    string.Join(" ", package.Manifest.Capabilities.Select(capability => capability.Description)),
                    Id,
                    package.Manifest.Publisher,
                    package.Manifest.Capabilities.Any(capability => capability.ExecutorKind == "mcp")
                        ? "MCP"
                        : "Tessera plugin",
                    null,
                    package.Manifest.Version,
                    null,
                    "BUILT_IN",
                    package.Manifest.Capabilities.Select(capability => capability.Description).Take(4).ToArray(),
                    accountRequired ? ["Account authorization"] : [],
                    Sensitivity(package.Manifest.Name, string.Join(" ", package.Manifest.Capabilities.Select(capability => capability.Description))),
                    "SERVER_INSTALLED",
                    installed ? "INSTALLED" : "AVAILABLE",
                    installed,
                    null);
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<IntegrationCatalogItem>>(items);
    }

    private static bool Matches(PluginManifest manifest, string query)
        => manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || manifest.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || manifest.Capabilities.Any(capability =>
                capability.Description.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string Sensitivity(string name, string description)
        => CatalogSensitivity.Classify($"{name} {description}");
}

internal sealed class McpRegistryCatalogSource(IHttpTransport transport)
    : IIntegrationCatalogSource
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public string Id => "mcp-registry";
    public string Name => "Official MCP Registry";

    public async Task<IReadOnlyList<IntegrationCatalogItem>> SearchAsync(
        string query,
        int limit,
        IReadOnlySet<string> installedPluginIds,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{query.ToLowerInvariant()}\n{limit}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Items;
        var url = "https://registry.modelcontextprotocol.io/v0.1/servers"
            + $"?search={Uri.EscapeDataString(query)}&limit={limit}&version=latest";
        var response = await transport.SendAsync(
                "GET",
                url,
                new Dictionary<string, string>
                {
                    ["Accept"] = "application/json",
                    ["User-Agent"] = "Tessera/2.0 integration-catalog",
                },
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Status is < 200 or >= 300)
            throw new HttpRequestException("MCP Registry unavailable.");
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        if (!root.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MCP Registry response is invalid.");
        var items = servers.EnumerateArray()
            .Take(limit)
            .Select(Parse)
            .ToArray();
        _cache[cacheKey] = new(DateTimeOffset.UtcNow.Add(CacheDuration), items);
        return items;
    }

    private static IntegrationCatalogItem Parse(JsonElement value)
    {
        var server = value.GetProperty("server");
        var id = RequiredString(server, "name", 200);
        var name = OptionalString(server, "title", 100)
            ?? id.Split('/', 2).Last().Replace('-', ' ');
        var description = RequiredString(server, "description", 500);
        var version = RequiredString(server, "version", 255);
        var repository = server.TryGetProperty("repository", out var repositoryValue)
            && repositoryValue.ValueKind == JsonValueKind.Object
            ? OptionalString(repositoryValue, "url", 500)
            : null;
        var website = OptionalString(server, "websiteUrl", 500);
        var packages = server.TryGetProperty("packages", out var packagesValue)
            && packagesValue.ValueKind == JsonValueKind.Array
            ? packagesValue.EnumerateArray().ToArray()
            : [];
        var remotes = server.TryGetProperty("remotes", out var remotesValue)
            && remotesValue.ValueKind == JsonValueKind.Array
            ? remotesValue.EnumerateArray().ToArray()
            : [];
        var runtimes = packages
            .Select(package => OptionalString(package, "registryType", 32))
            .Concat(remotes.Select(remote => OptionalString(remote, "type", 32)))
            .Where(runtime => runtime is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packageIdentity = packages
            .Select(package => OptionalString(package, "identifier", 500))
            .FirstOrDefault(identity => identity is not null);
        var publisher = id.Split('/', 2)[0];
        var status = value.TryGetProperty("_meta", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("io.modelcontextprotocol.registry/official", out var official)
            && official.ValueKind == JsonValueKind.Object
            ? OptionalString(official, "status", 32)
            : null;
        return new(
            id,
            name,
            description,
            "mcp-registry",
            publisher,
            runtimes.Length == 0 ? "MCP" : string.Join(", ", runtimes),
            repository ?? packageIdentity,
            version,
            null,
            status == "active" && repository is not null ? "VERIFIED_METADATA" : "UNTRUSTED",
            [description],
            SecretInputs(packages, remotes) ? ["External credentials"] : [],
            CatalogSensitivity.Classify($"{name} {description}"),
            "SERVER_REVIEW_REQUIRED",
            "REVIEW_REQUIRED",
            false,
            repository ?? website);
    }

    private static bool SecretInputs(
        IReadOnlyList<JsonElement> packages,
        IReadOnlyList<JsonElement> remotes)
        => packages.Concat(remotes).Any(ContainsSecretInput);

    private static bool ContainsSecretInput(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property =>
                string.Equals(property.Name, "isSecret", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.True
                || ContainsSecretInput(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Any(ContainsSecretInput),
            _ => false,
        };

    private static string RequiredString(JsonElement value, string property, int maximumLength)
        => OptionalString(value, property, maximumLength)
            ?? throw new InvalidDataException($"MCP Registry field '{property}' is missing.");

    private static string? OptionalString(JsonElement value, string property, int maximumLength)
    {
        if (!value.TryGetProperty(property, out var item)
            || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (item.ValueKind != JsonValueKind.String
            || item.GetString() is not { } text
            || text.Length == 0
            || text.Length > maximumLength
            || text.Any(char.IsControl))
            throw new InvalidDataException($"MCP Registry field '{property}' is invalid.");
        return text;
    }

    private sealed record CacheEntry(
        DateTimeOffset ExpiresAt,
        IReadOnlyList<IntegrationCatalogItem> Items);
}

internal sealed class PluginIntegrationCatalogSource(
    ITesseraCatalogPlugin plugin,
    IHttpTransport transport)
    : IIntegrationCatalogSource
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public string Id => plugin.CatalogSource.Id;
    public string Name => plugin.CatalogSource.DisplayName;

    public async Task<IReadOnlyList<IntegrationCatalogItem>> SearchAsync(
        string query,
        int limit,
        IReadOnlySet<string> installedPluginIds,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{query.ToLowerInvariant()}\n{limit}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Items;
        var values = await plugin.SearchCatalogAsync(query, limit, transport, cancellationToken)
            .ConfigureAwait(false);
        var items = values
            .Select(value => new IntegrationCatalogItem(
                value.Id,
                value.Name,
                value.Description,
                Id,
                value.Publisher,
                value.Runtime,
                value.RepositoryOrPackage,
                value.Version,
                value.License,
                "UNTRUSTED",
                value.CapabilitiesSummary,
                value.AuthTypes,
                CatalogSensitivity.Classify($"{value.Name} {value.Description}"),
                "SERVER_REVIEW_REQUIRED",
                "REVIEW_REQUIRED",
                false,
                SafeInspectUrl(value.InspectUrl)))
            .ToArray();
        _cache[cacheKey] = new(DateTimeOffset.UtcNow.Add(plugin.CatalogSource.CacheDuration), items);
        return items;
    }

    private sealed record CacheEntry(
        DateTimeOffset ExpiresAt,
        IReadOnlyList<IntegrationCatalogItem> Items);

    private static string? SafeInspectUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out _)) return null;
        return uri.AbsoluteUri;
    }
}

internal static class CatalogSensitivity
{
    public static string Classify(string value)
    {
        var text = value.ToLowerInvariant();
        if (text.Contains("health", StringComparison.Ordinal)
            || text.Contains("medical", StringComparison.Ordinal)
            || text.Contains("patient", StringComparison.Ordinal)) return "HEALTH_DATA";
        if (text.Contains("mail", StringComparison.Ordinal)
            || text.Contains("calendar", StringComparison.Ordinal)) return "PERSONAL_DATA";
        return "STANDARD";
    }
}
