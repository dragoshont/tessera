using System.Security.Cryptography;
using System.Text.Json;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Xunit;

namespace Tessera.Plugins.OneDrive.Tests;

public sealed class OneDrivePluginTests
{
    private static IReadOnlyDictionary<string, string> EmptyHeaders { get; } = new Dictionary<string, string>();

    [Fact]
    public void Manifest_exposes_only_three_read_only_account_capabilities()
    {
        var plugin = new OneDrivePlugin();

        Assert.Equal("onedrive", plugin.Manifest.PluginId);
        Assert.Equal("1.0.0", plugin.Manifest.Version);
        Assert.Equal(["onedrive.account.identity", "onedrive.items.get", "onedrive.items.list"],
            plugin.Manifest.Capabilities.Select(item => item.CapabilityId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(plugin.Manifest.Capabilities, capability =>
        {
            Assert.Equal(SideEffectClass.ReadOnly, capability.SideEffectClass);
            Assert.Equal(["onedrive.read"], capability.RequiredPermissions);
            Assert.True(capability.AccountRequired);
            Assert.Equal(IdempotencySupport.None, capability.IdempotencySupport);
        });
        Assert.IsNotAssignableFrom<ITesseraModelToolPlugin>(plugin);
    }

    [Fact]
    public void Package_catalog_manifest_and_clients_remain_in_parity_with_https_launch()
    {
        var root = RepositoryRoot();
        var manifestPath = Path.Combine(root, "plugins/onedrive/manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugins/catalog.json")));
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)));
        var packaged = manifest.RootElement.GetProperty("Capabilities").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetString()).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(new OneDrivePlugin().Manifest.Capabilities.Select(item => item.CapabilityId).Order(StringComparer.Ordinal), packaged);
        Assert.Equal(digest, catalog.RootElement.GetProperty("onedrive@1.0.0").GetString());
        Assert.All(manifest.RootElement.GetProperty("Capabilities").EnumerateArray(), capability =>
            Assert.Equal("native", capability.GetProperty("ExecutorKind").GetString()));

        var webApi = File.ReadAllText(Path.Combine(root, "web/src/api/r2.ts"));
        var webPage = File.ReadAllText(Path.Combine(root, "web/src/pages/R2ProductPages.tsx"));
        var webRuntime = File.ReadAllText(Path.Combine(root, "web/src/app/runtime.ts"));
        Assert.Contains("'/accounts/onedrive/connect'", webApi, StringComparison.Ordinal);
        Assert.Contains("beginOneDriveOAuth(displayName)", webPage, StringComparison.Ordinal);
        Assert.Contains("parsed.protocol !== 'https:'", webRuntime, StringComparison.Ordinal);

        var iosApi = File.ReadAllText(Path.Combine(root, "ios/src/lib/api.ts"));
        var iosAccounts = File.ReadAllText(Path.Combine(root, "ios/src/app/(tabs)/accounts.tsx"));
        Assert.Contains("'/accounts/onedrive/connect'", iosApi, StringComparison.Ordinal);
        Assert.Contains("api.beginOneDriveOAuth('OneDrive')", iosAccounts, StringComparison.Ordinal);
        Assert.Contains("url.protocol !== 'https:'", iosAccounts, StringComparison.Ordinal);
        Assert.Contains("WebBrowser.openBrowserAsync", iosAccounts, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_setup_is_truthful_and_has_no_connect_path()
    {
        var setup = new OneDrivePlugin().DescribeSetup(new(null, _ => null));
        Assert.False(setup.RuntimeConfigured);
        Assert.Null(setup.ConnectPath);
        Assert.Equal("oauth_application_unavailable", setup.DetailCode);
    }

        [Theory]
        [InlineData("https://tessera.example/oauth/other")]
        [InlineData("https://tessera.example/oauth/onedrive/callback?next=other")]
        [InlineData("https://tessera.example/oauth/onedrive/callback#other")]
        public void Setup_rejects_redirects_outside_the_exact_callback_uri(string redirectUri)
    {
        var path = Path.GetTempFileName();
        try
        {
                        File.WriteAllText(path, $$"""
                {
                  "oneDriveOAuth": {
                    "enabled": true,
                    "clientId": "client-id",
                    "clientSecretRef": "microsoft-client-secret",
                                        "redirectUri": "{{redirectUri}}",
                    "scopes": ["openid", "profile", "offline_access", "Files.Read"]
                  }
                }
                """);

            Assert.Throws<InvalidOperationException>(() =>
                new OneDrivePlugin().DescribeSetup(new(path, _ => null)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Adapter_uses_fixed_graph_routes_and_returns_metadata_without_urls_or_content()
    {
        var transport = new QueueTransport(
            Response("""{"id":"drive-1","driveType":"personal","owner":{"user":{"displayName":"Owner"}}}"""),
            Response("""{"value":[{"id":"item-1","name":"Plan.docx","size":42,"file":{"mimeType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document"},"webUrl":"https://example.invalid/leak","@microsoft.graph.downloadUrl":"https://download.invalid/secret"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/root/children?$top=1&$skiptoken=opaque-provider-value"}"""),
            Response("""{"value":[]}"""),
            Response("""{"id":"item-1","name":"Plan.docx","size":42,"file":{"mimeType":"application/octet-stream"}}"""));
        var adapter = new OneDriveRestAdapter(transport);

        var identity = await adapter.ValidateAsync("token");
        var first = await adapter.ListChildrenAsync("token", maximumItems: 1);
        var second = await adapter.ListChildrenAsync("token", cursor: first.Cursor);
        var item = await adapter.GetItemAsync("token", "item-1");

        Assert.True(identity.Succeeded, identity.ErrorCode);
        Assert.Equal("drive-1", identity.Identity?.DriveId);
        Assert.True(first.Succeeded, first.ErrorCode);
        Assert.NotNull(first.Cursor);
        Assert.DoesNotContain("graph.microsoft.com", first.Cursor, StringComparison.Ordinal);
        Assert.Empty(second.Items);
        Assert.True(item.Succeeded, item.ErrorCode);
        Assert.All(transport.Urls, url => Assert.StartsWith("https://graph.microsoft.com/v1.0/me/drive", url, StringComparison.Ordinal));
        Assert.Contains("$skiptoken=opaque-provider-value", transport.Urls[2], StringComparison.Ordinal);
        var output = JsonSerializer.Serialize(item.Item);
        Assert.DoesNotContain("webUrl", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("download", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(transport.Bodies, body => body.Contains("token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adapter_rejects_hostile_next_links_control_metadata_and_oversized_results()
    {
        var hostile = await new OneDriveRestAdapter(new QueueTransport(Response(
            """{"value":[],"@odata.nextLink":"https://evil.example/v1.0/me/drive/root/children?$skiptoken=secret"}""")))
            .ListChildrenAsync("token");
        var sameHostContent = await new OneDriveRestAdapter(new QueueTransport(Response(
            """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/items/item-1/content?$skiptoken=secret"}""")))
            .ListChildrenAsync("token");
        var control = await new OneDriveRestAdapter(new QueueTransport(Response(
            "{\"value\":[{\"id\":\"item-1\",\"name\":\"bad\\u0001name\",\"size\":1,\"file\":{}}]}")))
            .ListChildrenAsync("token");
        var oversized = await new OneDriveRestAdapter(new QueueTransport(Response("{\"value\":[],\"padding\":\"" + new string('x', 128) + "\"}")), 64)
            .ListChildrenAsync("token");

        Assert.Equal("provider_malformed", hostile.ErrorCode);
        Assert.Equal("provider_malformed", sameHostContent.ErrorCode);
        Assert.Equal("provider_malformed", control.ErrorCode);
        Assert.Equal("provider_result_too_large", oversized.ErrorCode);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new OneDriveRestAdapter(new QueueTransport()).ListChildrenAsync("token", maximumItems: 26));
    }

    [Theory]
    [InlineData(401, "provider_auth_required")]
    [InlineData(403, "provider_forbidden")]
    [InlineData(404, "provider_not_found")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "provider_unavailable")]
    public async Task Adapter_maps_stable_provider_errors(int status, string expected)
    {
        var result = await new OneDriveRestAdapter(new QueueTransport(new TransportResponse(status, EmptyHeaders, "{}"))).ValidateAsync("token");
        Assert.Equal(expected, result.ErrorCode);
    }

    [Fact]
    public async Task OAuth_uses_fixed_microsoft_hosts_pkce_single_use_owner_state_and_exact_scopes()
    {
        var custody = await CustodyAsync();
        var transport = new QueueTransport(Response("""{"access_token":"access","refresh_token":"refresh","scope":"openid profile offline_access Files.Read","expires_in":3600}"""));
        var oauth = new OneDriveOAuthService(transport, custody);
        var start = oauth.Begin("owner-a", "My OneDrive", Options());
        var query = Query(start.AuthorizeUrl);

        var completed = await oauth.CompleteAsync(query["state"], "code");
        var replay = await oauth.CompleteAsync(query["state"], "code");

        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", start.AuthorizeUrl.GetLeftPart(UriPartial.Path));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("openid profile offline_access Files.Read", query["scope"]);
        Assert.Equal("owner-a", completed.OwnerPrincipalId);
        Assert.Equal("refresh", completed.Credentials?.RefreshToken);
        Assert.Equal("oauth_state_invalid_or_expired", replay.ErrorCode);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", Assert.Single(transport.Urls));
        Assert.Contains("code_verifier=", Assert.Single(transport.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_rejects_missing_required_scope_invalid_grant_and_rotates_refresh_token()
    {
        var missing = new OneDriveOAuthService(new QueueTransport(Response("""{"access_token":"access","refresh_token":"refresh","scope":"openid profile offline_access"}""")), await CustodyAsync());
        var missingStart = missing.Begin("owner", "Drive", Options());
        Assert.Equal("onedrive_required_scopes_missing", (await missing.CompleteAsync(Query(missingStart.AuthorizeUrl)["state"], "code")).ErrorCode);

        var rejected = new OneDriveOAuthService(new QueueTransport(new TransportResponse(400, EmptyHeaders, "{}")), await CustodyAsync());
        var rejectedStart = rejected.Begin("owner", "Drive", Options());
        Assert.Equal("oauth_grant_rejected", (await rejected.CompleteAsync(Query(rejectedStart.AuthorizeUrl)["state"], "code")).ErrorCode);

        var custody = await CustodyAsync();
        await custody.PutBundleAsync("account", new CredentialBundle("old-access", "old-refresh"));
        var refresh = new OneDriveOAuthService(new QueueTransport(Response("""{"access_token":"new-access","refresh_token":"new-refresh","scope":"openid profile offline_access Files.Read","expires_in":3600}""")), custody);
        var result = await refresh.RefreshIfNeededAsync("account", Options());
        var stored = await custody.GetBundleAsync("account");
        Assert.Equal(OneDriveRefreshStatus.Refreshed, result.Status);
        Assert.Equal("new-access", stored.AccessToken);
        Assert.Equal("new-refresh", stored.RefreshToken);
    }

    [Fact]
    public async Task Validation_rejects_drive_identity_drift_and_capabilities_reject_wrong_account_provider()
    {
        var plugin = new OneDrivePlugin();
        var context = Context(new QueueTransport(Response("""{"id":"different-drive","driveType":"personal"}""")));
        var validation = await plugin.ValidateAccountAsync(Account(), new CredentialBundle(AccessToken: "access"), context);
        Assert.Equal(AccountLifecycle.Degraded, validation.Lifecycle);
        Assert.Equal(AccountHealth.Degraded, validation.Health);

        var other = Account() with { ProviderId = "gmail" };
        var otherContext = new PluginCapabilityContext(other, new CredentialBundle(AccessToken: "access"), new QueueTransport(), new NullMcpRuntime(), (_, _) => throw new NotSupportedException());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await plugin.CreateCapabilityAsync("onedrive.items.get", "1", otherContext));
    }

    [Fact]
    public async Task Capability_rejects_cross_owner_substitution_before_provider_egress()
    {
        var transport = new QueueTransport();
        var capability = await new OneDrivePlugin().CreateCapabilityAsync("onedrive.items.get", "1", Context(transport));
        var invocation = Invocation("onedrive.items.get", "drive:item", new { itemId = "item-1" }) with { OwnerPrincipalId = "owner-b" };

        var result = await capability.InvokeAsync(invocation);

        Assert.Equal(CapabilityOutcome.Failed, result.Outcome);
        Assert.Equal("account_unavailable", result.FailureCode);
        Assert.Empty(transport.Urls);
    }

    [Fact]
    public async Task Item_capability_returns_exact_metadata_and_invalid_cursor_is_stable()
    {
        var plugin = new OneDrivePlugin();
        var itemCapability = await plugin.CreateCapabilityAsync("onedrive.items.get", "1", Context(new QueueTransport(Response(
            """{"id":"item-1","name":"Folder","size":0,"folder":{"childCount":0}}"""))));
        var item = await itemCapability.InvokeAsync(Invocation("onedrive.items.get", "drive:item", new { itemId = "item-1" }));
        Assert.Equal(CapabilityOutcome.Succeeded, item.Outcome);
        Assert.True(item.Output.GetProperty("isFolder").GetBoolean());

        var listCapability = await plugin.CreateCapabilityAsync("onedrive.items.list", "1", Context(new QueueTransport()));
        var invalid = await listCapability.InvokeAsync(Invocation("onedrive.items.list", "drive:children", new { cursor = "not-a-valid-cursor" }));
        Assert.Equal(CapabilityOutcome.Failed, invalid.Outcome);
        Assert.Equal("invalid_cursor", invalid.FailureCode);
    }

    private static TransportResponse Response(string body) => new(200, EmptyHeaders, body);
    private static string RepositoryRoot()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        return File.Exists(Path.Combine(root, "Tessera.slnx")) ? root : throw new DirectoryNotFoundException(root);
    }
    private static OneDriveOAuthOptions Options() => new()
    {
        Enabled = true,
        ClientId = "client-id",
        ClientSecretRef = "microsoft-client-secret",
        RedirectUri = "https://tessera.example/oauth/onedrive/callback",
    };
    private static async Task<InMemoryCredentialStore> CustodyAsync()
    {
        var custody = new InMemoryCredentialStore();
        await custody.PutBundleAsync("microsoft-client-secret", new CredentialBundle(Extra: new Dictionary<string, string> { [OneDriveOAuthService.ClientSecretExtraKey] = "secret-value" }));
        return custody;
    }
    private static PluginCapabilityContext Context(IHttpTransport transport)
        => new(Account(), new CredentialBundle(AccessToken: "access"), transport, new NullMcpRuntime(), (_, _) => throw new NotSupportedException());
    private static ConnectedAccount Account()
    {
        var now = DateTimeOffset.UtcNow;
        return new("owner-a", "onedrive-owner", "onedrive", "onedrive", "1.0.0", "My OneDrive", "drive-1", AccountLifecycle.Connected,
            "credential-ref", AccountHealth.Healthy, now, "{}", ["onedrive.read"], [], now, now, 1)
        { ProviderAccountId = "drive-1", ProviderScopes = ["openid", "profile", "offline_access", "Files.Read"] };
    }
    private static CapabilityInvocation Invocation(string capabilityId, string target, object input)
        => new("owner-a", "test", capabilityId, "1", target, JsonSerializer.SerializeToElement(input), "action-1", null);
    private static Dictionary<string, string> Query(Uri uri)
        => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]), StringComparer.Ordinal);

    private sealed class QueueTransport(params TransportResponse[] responses) : IHttpTransport
    {
        private readonly Queue<TransportResponse> _responses = new(responses);
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];
        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
        {
            Urls.Add(url);
            Bodies.Add(body ?? "");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class NullMcpRuntime : IMcpClientRuntime
    {
        public Task<McpServerContract> DiscoverAsync(McpServerEndpoint endpoint, McpCallPolicy policy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<McpInvocationResult> CallAsync(McpServerEndpoint endpoint, string toolName, IReadOnlyDictionary<string, object?> arguments, McpCallPolicy policy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}