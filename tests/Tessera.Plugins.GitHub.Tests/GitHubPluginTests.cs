using System.Security.Cryptography;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Plugin.Abstractions;
using Tessera.Plugins.GitHub;
using Tessera.Providers;
using Xunit;

namespace Tessera.Plugins.GitHub.Tests;

public sealed class GitHubPluginTests
{
    [Fact]
    public void Manifest_classifies_provider_tools_without_trusting_discovery_metadata()
    {
        var plugin = new GitHubPlugin();

        var list = Assert.Single(plugin.Manifest.Capabilities, item => item.CapabilityId == "github.issues.list");
        Assert.Equal("list_issues", list.ExternalToolName);
        Assert.Equal(SideEffectClass.ReadOnly, list.SideEffectClass);
        var create = Assert.Single(plugin.Manifest.Capabilities, item => item.CapabilityId == "github.issues.create");
        Assert.Equal("issue_write", create.ExternalToolName);
        Assert.Equal(SideEffectClass.ExternalCommunication, create.SideEffectClass);
        Assert.Equal(VerificationSupport.ProviderState, create.VerificationSupport);
    }

    [Fact]
    public async Task List_enforces_allowlist_and_normalizes_official_provider_output()
    {
        var runtime = new RecordingRuntime((tool, _) => tool == "list_issues"
            ? Success(JsonSerializer.SerializeToElement(new
            {
                issues = new[]
                {
                    new { number = 17, title = "Bounded issue", state = "open", html_url = "https://github.com/owner/repo/issues/17", ignored = "provider metadata" },
                },
            }))
            : throw new InvalidOperationException(tool));
        var capability = await CreateCapabilityAsync("github.issues.list", runtime);

        var denied = await capability.InvokeAsync(Invocation(
            "github.issues.list",
            "other/repo",
            new { repository = "other/repo" }));
        var result = await capability.InvokeAsync(Invocation(
            "github.issues.list",
            "owner/repo",
            new { repository = "owner/repo" }));

        Assert.Equal(CapabilityOutcome.Failed, denied.Outcome);
        Assert.Equal("repository_not_allowed", denied.FailureCode);
        Assert.Equal(CapabilityOutcome.Succeeded, result.Outcome);
        var issue = Assert.Single(result.Output.GetProperty("issues").EnumerateArray());
        Assert.Equal(17, issue.GetProperty("number").GetInt32());
        Assert.Equal("Bounded issue", issue.GetProperty("title").GetString());
        Assert.False(issue.TryGetProperty("ignored", out _));
        var call = Assert.Single(runtime.Calls);
        Assert.Equal("list_issues", call.Tool);
        Assert.Equal("owner", call.Arguments["owner"]);
        Assert.Equal("repo", call.Arguments["repo"]);
        Assert.Equal(new Uri("https://api.githubcopilot.com/mcp/"), call.Endpoint.Endpoint);
        Assert.Equal("Bearer test-credential", call.Endpoint.Headers!["Authorization"]);
        Assert.Equal("get_me,list_issues,issue_write,issue_read", call.Endpoint.Headers["X-MCP-Tools"]);
        Assert.DoesNotContain("test-credential", result.Output.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_rejects_owner_and_account_substitution_before_provider_call()
    {
        var runtime = new RecordingRuntime((_, _) => throw new InvalidOperationException("must not call"));
        var capability = await CreateCapabilityAsync("github.issues.list", runtime);

        var wrongOwner = await capability.InvokeAsync(Invocation(
            "github.issues.list",
            "owner/repo",
            new { repository = "owner/repo" },
            owner: "attacker"));
        var wrongAccount = await capability.InvokeAsync(Invocation(
            "github.issues.list",
            "owner/repo",
            new { accountId = "github-other", repository = "owner/repo" }));

        Assert.Equal("repository_not_allowed", wrongOwner.FailureCode);
        Assert.Equal("repository_not_allowed", wrongAccount.FailureCode);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void Registry_binds_only_the_explicit_projected_account()
    {
        var plugin = new GitHubPlugin();
        var assemblyPath = typeof(GitHubPlugin).Assembly.Location;
        var registry = PluginModuleDiscovery.Discover(
            Path.GetDirectoryName(assemblyPath)!,
            [new(
                "github",
                "1.0.0",
                Path.GetFileName(assemblyPath),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
                PluginTrustState.BUILT_IN,
                plugin.Manifest.Capabilities)]);
        var accounts = new[] { Account("github-a"), Account("github-b") };
        var projected = Assert.Single(
            registry.ProjectModelTools(accounts, new HashSet<(string, string)> { ("github.issues.list", "1") }));

        var binding = registry.BindModelTool(
            projected,
            JsonSerializer.SerializeToElement(new { accountId = "github-b", repository = "owner/repo" }));

        Assert.Equal("github-b", binding.AccountId);
        Assert.Equal("owner/repo", binding.TargetScope);
        var exception = Assert.Throws<PluginModuleException>(() => registry.BindModelTool(
            projected,
            JsonSerializer.SerializeToElement(new { accountId = "github-attacker", repository = "owner/repo" })));
        Assert.Equal("account_ambiguous", exception.ErrorCode);
    }

    [Fact]
    public async Task Create_requires_approval_then_verifies_receipt_with_issue_read()
    {
        var runtime = new RecordingRuntime((tool, _) => tool switch
        {
            "issue_write" => Success(JsonSerializer.SerializeToElement(new
            {
                issue = new { id = 9001, number = 42, title = "Approved title", body = "Approved body", html_url = "https://github.com/owner/repo/issues/42" },
            })),
            "issue_read" => Success(JsonSerializer.SerializeToElement(new
            {
                number = 42, title = "Approved title", body = "Approved body", state = "open", html_url = "https://github.com/owner/repo/issues/42",
            })),
            _ => throw new InvalidOperationException(tool),
        });
        var capability = await CreateCapabilityAsync("github.issues.create", runtime);
        var input = new { repository = "owner/repo", title = "Approved title", body = "Approved body" };

        var pending = await capability.InvokeAsync(Invocation("github.issues.create", "owner/repo", input));
        var result = await capability.InvokeAsync(Invocation(
            "github.issues.create",
            "owner/repo",
            input,
            authorizationId: "action-1"));

        Assert.Equal(CapabilityOutcome.Failed, pending.Outcome);
        Assert.Equal("authorization_required", pending.FailureCode);
        Assert.Equal(CapabilityOutcome.Succeeded, result.Outcome);
        Assert.Equal("https://github.com/owner/repo/issues/42", result.ProviderReceipt);
        Assert.Equal("provider_verified", result.VerificationMetadata);
        Assert.Equal(["issue_write", "issue_read"], runtime.Calls.Select(call => call.Tool));
        var write = runtime.Calls[0];
        Assert.True(write.Policy.MutationDispatched);
        Assert.Equal("create", write.Arguments["method"]);
        var read = runtime.Calls[1];
        Assert.False(read.Policy.MutationDispatched);
        Assert.Equal("get", read.Arguments["method"]);
        Assert.Equal(42, read.Arguments["issue_number"]);
    }

    [Theory]
    [InlineData(McpInvocationOutcome.UnknownOutcome, null, "unknown_outcome")]
    [InlineData(McpInvocationOutcome.Succeeded, "{}", "provider_malformed")]
    public async Task Create_preserves_unknown_outcomes_without_blind_retry(
        McpInvocationOutcome outcome,
        string? output,
        string expectedFailure)
    {
        var runtime = new RecordingRuntime((_, _) => new(
            outcome,
            output is null ? null : JsonDocument.Parse(output).RootElement.Clone(),
            outcome == McpInvocationOutcome.UnknownOutcome ? "unknown_outcome" : null));
        var capability = await CreateCapabilityAsync("github.issues.create", runtime);

        var result = await capability.InvokeAsync(Invocation(
            "github.issues.create",
            "owner/repo",
            new { repository = "owner/repo", title = "Approved title" },
            authorizationId: "action-1"));

        Assert.Equal(CapabilityOutcome.UnknownOutcome, result.Outcome);
        Assert.Equal(expectedFailure, result.FailureCode);
        Assert.Single(runtime.Calls);
        Assert.Equal("issue_write", runtime.Calls[0].Tool);
    }

    [Fact]
    public async Task Create_returns_unknown_when_readback_is_malformed_or_mismatched()
    {
        var runtime = new RecordingRuntime((tool, _) => tool switch
        {
            "issue_write" => Success(JsonSerializer.SerializeToElement(new
            {
                number = 42, title = "Approved title", html_url = "https://github.com/owner/repo/issues/42",
            })),
            "issue_read" => Success(JsonSerializer.SerializeToElement(new
            {
                number = 43, title = "Different issue", html_url = "https://github.com/owner/repo/issues/43",
            })),
            _ => throw new InvalidOperationException(tool),
        });
        var capability = await CreateCapabilityAsync("github.issues.create", runtime);

        var result = await capability.InvokeAsync(Invocation(
            "github.issues.create",
            "owner/repo",
            new { repository = "owner/repo", title = "Approved title" },
            authorizationId: "action-1"));

        Assert.Equal(CapabilityOutcome.UnknownOutcome, result.Outcome);
        Assert.Equal("verification_failed", result.FailureCode);
        Assert.Equal(2, runtime.Calls.Count);
    }

    private static async Task<ICapability> CreateCapabilityAsync(
        string capabilityId,
        RecordingRuntime runtime)
        => await new GitHubPlugin().CreateCapabilityAsync(
            capabilityId,
            "1",
            new(
                Account("github-a"),
                new CredentialBundle(AccessToken: "test-credential"),
                new NullTransport(),
                runtime,
                (_, _) => throw new InvalidOperationException("No secondary credential expected.")));

    private static ConnectedAccount Account(string accountId)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            "owner",
            accountId,
            "github",
            "github",
            "1.0.0",
            "GitHub",
            null,
            AccountLifecycle.Connected,
            ConnectedAccountCredentialRef.Create("owner", accountId),
            AccountHealth.Healthy,
            now,
            "{\"allowedRepositories\":[\"owner/repo\"]}",
            ["issues:read", "issues:write"],
            [
                new("github", "1.0.0", "github.issues.list", "1"),
                new("github", "1.0.0", "github.issues.create", "1"),
            ],
            now,
            now,
            1);
    }

    private static CapabilityInvocation Invocation(
        string capabilityId,
        string target,
        object input,
        string? authorizationId = null,
        string owner = "owner") => new(
            owner,
            "test",
            capabilityId,
            "1",
            target,
            JsonSerializer.SerializeToElement(input),
            authorizationId,
            "idempotency-key");

    private static McpInvocationResult Success(JsonElement output)
        => new(McpInvocationOutcome.Succeeded, output, null);

    private sealed class RecordingRuntime(
        Func<string, IReadOnlyDictionary<string, object?>, McpInvocationResult> handler) : IMcpClientRuntime
    {
        public List<Call> Calls { get; } = [];

        public Task<McpServerContract> DiscoverAsync(
            McpServerEndpoint endpoint,
            McpCallPolicy policy,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpServerContract(
                endpoint.ServerId,
                "github-mcp-server",
                "1.9.0",
                [
                    Tool("get_me"),
                    Tool("list_issues", "owner", "repo"),
                    Tool("issue_write", "method", "owner", "repo", "title"),
                    Tool("issue_read", "method", "owner", "repo", "issue_number"),
                ]));

        public Task<McpInvocationResult> CallAsync(
            McpServerEndpoint endpoint,
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            McpCallPolicy policy,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(endpoint, toolName, arguments, policy));
            return Task.FromResult(handler(toolName, arguments));
        }

        private static McpToolContract Tool(string name, params string[] properties)
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = properties.ToDictionary(
                    value => value,
                    value => (object)new { type = value == "issue_number" ? "integer" : "string" }),
                required = properties,
                additionalProperties = false,
            });
            var outputProperties = name switch
            {
                "get_me" => new Dictionary<string, object?> { ["login"] = new { type = "string" } },
                "list_issues" => new Dictionary<string, object?> { ["issues"] = new { type = "array" } },
                "issue_write" or "issue_read" => new Dictionary<string, object?> { ["issue"] = new { type = "object" } },
                _ => [],
            };
            var output = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = outputProperties,
                required = outputProperties.Keys.ToArray(),
                additionalProperties = false,
            });
            return new(name, input, output);
        }
    }

    private sealed record Call(
        McpServerEndpoint Endpoint,
        string Tool,
        IReadOnlyDictionary<string, object?> Arguments,
        McpCallPolicy Policy);

    private sealed class NullTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            string? body,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}