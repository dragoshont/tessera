using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Mcp.Client;
using Tessera.Plugin.Abstractions;

namespace Tessera.Plugins.GitHub;

public sealed partial class GitHubPlugin : ITesseraCapabilityPlugin, ITesseraModelToolPlugin, ITesseraAccountPlugin, ITesseraMcpPlugin, ITesseraSetupPlugin, ITesseraCatalogPlugin
{
    private static readonly Uri DefaultEndpoint = new("https://api.githubcopilot.com/mcp/");
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
    });
    private static readonly JsonElement ListSchema = Schema(
        new Dictionary<string, object?> { ["repository"] = Type("string") },
        ["repository"]);
    private static readonly JsonElement CreateSchema = Schema(
        new Dictionary<string, object?>
        {
            ["repository"] = Type("string"),
            ["title"] = Type("string"),
            ["body"] = Type("string"),
        },
        ["repository", "title"]);

    public TesseraPluginManifest Manifest { get; } = new(
        "github",
        "1.0.0",
        "GitHub",
        "github",
        [
            Capability(
                "github.issues.list",
                "List GitHub issues",
                "list_issues",
                SideEffectClass.ReadOnly,
                "issues:read",
                VerificationSupport.None),
            Capability(
                "github.issues.create",
                "Create a GitHub issue after exact approval",
                "issue_write",
                SideEffectClass.ExternalCommunication,
                "issues:write",
                VerificationSupport.ProviderState),
        ]);

    public IReadOnlyList<PluginModelToolManifest> ModelTools { get; } =
    [
        new(
            "list_github_issues",
            "github.issues.list",
            "1",
            "List issues in an allowed repository for the explicitly selected GitHub account.",
            ListSchema),
        new(
            "create_github_issue",
            "github.issues.create",
            "1",
            "Prepare an exact GitHub issue for one-use human approval in an allowed repository.",
            CreateSchema),
    ];

    public RequiredMcpServer RequiredMcpServer { get; } = new("github-mcp-server", "1.9.0");

    public IReadOnlyList<RequiredMcpTool> RequiredMcpTools { get; } =
    [
        new("get_me", [], [new("login", "string")]),
        new("list_issues", [new("owner", "string"), new("repo", "string")], [new("issues", "array")]),
        new("issue_write", [new("method", "string"), new("owner", "string"), new("repo", "string"), new("title", "string")], [new("issue", "object")]),
        new("issue_read", [new("method", "string"), new("owner", "string"), new("repo", "string"), new("issue_number", "integer")], [new("issue", "object")]),
    ];

    public PluginSetupDescriptor DescribeSetup(PluginHostConfiguration configuration)
        => new("github", "GitHub", true, true, "/accounts", "account_authorization_required");

    public async ValueTask<McpServerContract> DiscoverMcpAsync(
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Account is null)
            throw new PluginModuleException("account_unavailable");
        var account = context.Account;
        var bundle = !string.IsNullOrWhiteSpace(context.AccountCredential.AccessToken)
            ? context.AccountCredential
            : await context.ResolveCredentialAsync(account.CredentialRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bundle.AccessToken))
            throw new PluginModuleException("account_unavailable");
        var configuration = Configuration(context.Account);
        return await new GitHubMcp(
            context.McpRuntime,
            $"github:{context.Account.AccountId}",
            configuration.Endpoint,
            bundle.AccessToken)
            .DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public PluginModelToolBinding BindModelTool(
        string modelToolName,
        JsonElement arguments,
        ConnectedAccount? account)
    {
        if (account is null) throw new PluginModuleException("account_unavailable");
        var repository = RequiredRepository(arguments, "repository");
        return modelToolName switch
        {
            "list_github_issues" => new(account.AccountId, repository, arguments.Clone()),
            "create_github_issue" => new(account.AccountId, repository, arguments.Clone()),
            _ => throw new PluginModuleException("tool_not_available"),
        };
    }

    public PluginAccountDefinition DefineAccount(string pluginVersion, JsonElement nonSecretConfiguration)
    {
        var configuration = Configuration(nonSecretConfiguration);
        var permissions = configuration.AllowWrites
            ? new[] { "issues:read", "issues:write" }
            : ["issues:read"];
        return new(
            "github",
            permissions,
            Manifest.Capabilities
                .Where(item => item.RequiredPermissions.All(permission => permissions.Contains(permission, StringComparer.Ordinal)))
                .Select(item => new AccountCapabilityBinding("github", pluginVersion, item.CapabilityId, item.Version))
                .ToArray());
    }

    public async ValueTask<PluginAccountValidation> ValidateAccountAsync(
        ConnectedAccount account,
        Tessera.Core.Stores.CredentialBundle credential,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.AccessToken)) return ValidationFailure(true);
        var configuration = Configuration(account);
        var client = new GitHubMcp(context.McpRuntime, $"github:{account.AccountId}", configuration.Endpoint, credential.AccessToken);
        var identity = await client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        if (identity.Outcome != McpInvocationOutcome.Succeeded || identity.StructuredOutput is not { } output)
            return ValidationFailure(identity.ErrorCode == "provider_auth_required");
        var login = RequiredIdentity(output, "login");
        var providerId = OptionalIdentity(output, "id") ?? login;
        foreach (var repository in configuration.AllowedRepositories)
        {
            var proof = await client.ListIssuesAsync(GitHubRepository.Parse(repository), cancellationToken).ConfigureAwait(false);
            if (proof.Outcome != McpInvocationOutcome.Succeeded)
                return ValidationFailure(proof.ErrorCode == "provider_auth_required");
        }
        var permissions = configuration.AllowWrites
            ? new[] { "issues:read", "issues:write" }
            : ["issues:read"];
        return new(
            AccountLifecycle.Connected,
            AccountHealth.Healthy,
            providerId,
            login,
            permissions,
            account.ProviderScopes,
            account.CapabilityBindings.Where(binding =>
                Manifest.Capabilities.Any(capability =>
                    capability.CapabilityId == binding.CapabilityId
                    && capability.RequiredPermissions.All(permission => permissions.Contains(permission, StringComparer.Ordinal)))).ToArray(),
            DateTimeOffset.UtcNow);
    }

    public ValueTask<ICapability> CreateCapabilityAsync(
        string capabilityId,
        string capabilityVersion,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (capabilityVersion != "1" || context.Account is null)
            throw new InvalidOperationException("capability_unavailable");
        if (context.Account.ProviderId != "github"
            || context.Account.PluginId != "github"
            || context.Account.PluginVersion != "1.0.0")
            throw new InvalidOperationException("account_unavailable");

        var account = context.Account;
        var manifest = Manifest.Capabilities.SingleOrDefault(item => item.CapabilityId == capabilityId)
            ?? throw new InvalidOperationException("capability_unavailable");
        return ValueTask.FromResult<ICapability>(new DeferredPluginCapability(
            manifest.ToDescriptor(),
            async token =>
            {
                var bundle = !string.IsNullOrWhiteSpace(context.AccountCredential.AccessToken)
                    ? context.AccountCredential
                    : await context.ResolveCredentialAsync(account.CredentialRef, token).ConfigureAwait(false);
                var credential = bundle.AccessToken;
                if (string.IsNullOrWhiteSpace(credential)
                    || credential.Length > 8192
                    || credential.Any(char.IsControl))
                    throw new InvalidOperationException("account_credential_unavailable");
                var configuration = Configuration(account);
                var client = new GitHubMcp(
                    context.McpRuntime,
                    $"github:{account.AccountId}",
                    configuration.Endpoint,
                    credential);
                return capabilityId switch
                {
                    "github.issues.list" => new ListIssuesCapability(account, configuration.AllowedRepositories, client),
                    "github.issues.create" => new CreateIssueCapability(account, configuration.AllowedRepositories, client),
                    _ => throw new InvalidOperationException("capability_unavailable"),
                };
            }));
    }

    private static PluginCapabilityManifest Capability(
        string id,
        string description,
        string tool,
        SideEffectClass sideEffect,
        string permission,
        VerificationSupport verification) => new(
            id,
            "1",
            description,
            tool,
            ObjectSchema,
            ObjectSchema,
            sideEffect,
            true,
            [permission],
            [SensitivityClass.Public, SensitivityClass.Internal],
            IdempotencySupport.None,
            verification);

    private static GitHubConfiguration Configuration(ConnectedAccount account)
    {
        try
        {
            using var document = JsonDocument.Parse(account.NonSecretConfigJson);
            return Configuration(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("invalid_configuration", exception);
        }
    }

    private static GitHubConfiguration Configuration(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => property.Name is not ("allowedRepositories" or "allowWrites"))
                || !root.TryGetProperty("allowedRepositories", out var allowed)
                || allowed.ValueKind != JsonValueKind.Array
                || allowed.GetArrayLength() is < 1 or > 25)
                throw new InvalidOperationException("invalid_configuration");

            var repositories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in allowed.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || item.GetString() is not { } repository
                    || !RepositoryPattern().IsMatch(repository)
                    || !repositories.Add(repository))
                    throw new InvalidOperationException("invalid_configuration");
            }

            var allowWrites = !root.TryGetProperty("allowWrites", out var writes)
                || writes.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("allowWrites", out writes)
                && writes.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("invalid_configuration");
            return new(DefaultEndpoint, repositories, allowWrites);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("invalid_configuration", exception);
        }
    }

    private static JsonElement Schema(
        IReadOnlyDictionary<string, object?> properties,
        IReadOnlyList<string> required)
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            required,
            additionalProperties = false,
        });

    private static object Type(string type) => new { type };

    private static string RequiredRepository(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } repository
            || !RepositoryPattern().IsMatch(repository))
            throw new PluginModuleException("invalid_tool_arguments");
        return repository;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    private static PluginAccountValidation ValidationFailure(bool authRequired)
        => new(
            authRequired ? AccountLifecycle.AuthRequired : AccountLifecycle.Degraded,
            authRequired ? AccountHealth.AuthRequired : AccountHealth.Degraded,
            null,
            null,
            [],
            [],
            [],
            null);

    private static string RequiredIdentity(JsonElement output, string name)
        => OptionalIdentity(output, name) ?? throw new InvalidOperationException("provider_malformed");

    private static string? OptionalIdentity(JsonElement output, string name)
    {
        if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String when value.GetString() is { Length: > 0 and <= 256 } text => text,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("provider_malformed"),
        };
    }

    private sealed record GitHubConfiguration(Uri Endpoint, IReadOnlySet<string> AllowedRepositories, bool AllowWrites);

    private sealed class ListIssuesCapability(
        ConnectedAccount account,
        IReadOnlySet<string> allowedRepositories,
        GitHubMcp client) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor(
            "github.issues.list",
            "List GitHub issues",
            SideEffectClass.ReadOnly,
            "issues:read",
            VerificationSupport.None);

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            if (!InvocationMatches(invocation, account, allowedRepositories, out var repository))
                return Failure("repository_not_allowed");
            try
            {
                var result = await client.ListIssuesAsync(repository, cancellationToken).ConfigureAwait(false);
                if (result.Outcome != McpInvocationOutcome.Succeeded || result.StructuredOutput is not { } output)
                    return Failure(result.ErrorCode ?? "provider_unavailable");
                return Success(JsonSerializer.SerializeToElement(new { issues = NormalizeIssues(output) }));
            }
            catch (ArgumentException)
            {
                return Failure("provider_malformed");
            }
        }
    }

    private sealed class CreateIssueCapability(
        ConnectedAccount account,
        IReadOnlySet<string> allowedRepositories,
        GitHubMcp client) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = DescriptorFor(
            "github.issues.create",
            "Create a GitHub issue after exact approval",
            SideEffectClass.ExternalCommunication,
            "issues:write",
            VerificationSupport.ProviderState);

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(invocation.AuthorizationId))
                return Failure("authorization_required");
            if (!InvocationMatches(invocation, account, allowedRepositories, out var repository))
                return Failure("repository_not_allowed");

            try
            {
                Only(invocation.Input, "accountId", "repository", "title", "body");
                var title = RequiredText(invocation.Input, "title", 256);
                var body = OptionalText(invocation.Input, "body", 64 * 1024);
                var created = await client.CreateIssueAsync(repository, title, body, cancellationToken).ConfigureAwait(false);
                if (created.Outcome == McpInvocationOutcome.UnknownOutcome)
                    return Unknown(null, null, created.ErrorCode ?? "unknown_outcome");
                if (created.Outcome != McpInvocationOutcome.Succeeded || created.StructuredOutput is not { } createdOutput)
                    return Failure(created.ErrorCode ?? "provider_unavailable");

                var receipt = ParseIssue(createdOutput, requireReceipt: true);
                if (receipt is null)
                    return Unknown(null, null, "provider_malformed");

                var verified = await client.ReadIssueAsync(repository, receipt.Number, cancellationToken).ConfigureAwait(false);
                if (verified.Outcome != McpInvocationOutcome.Succeeded || verified.StructuredOutput is not { } verifiedOutput)
                    return Unknown(receipt.Number, receipt.Receipt, verified.ErrorCode ?? "verification_failed");
                var issue = ParseIssue(verifiedOutput, requireReceipt: false);
                if (issue is null
                    || issue.Number != receipt.Number
                    || !string.Equals(issue.Title, title, StringComparison.Ordinal)
                    || body is not null && !string.Equals(issue.Body, body, StringComparison.Ordinal))
                    return Unknown(receipt.Number, receipt.Receipt, "verification_failed");

                return new(
                    CapabilityOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(new { number = receipt.Number, url = receipt.Receipt }),
                    receipt.Receipt,
                    "provider_verified",
                    null);
            }
            catch (ArgumentException)
            {
                return Failure("invalid_request");
            }
        }
    }

    private static CapabilityDescriptor DescriptorFor(
        string id,
        string description,
        SideEffectClass sideEffect,
        string permission,
        VerificationSupport verification)
        => CapabilityDescriptor.Create(
            id,
            "1",
            description,
            "{}",
            "{}",
            sideEffect,
            [permission],
            [SensitivityClass.Public, SensitivityClass.Internal],
            IdempotencySupport.None,
            verification);

    private static bool InvocationMatches(
        CapabilityInvocation invocation,
        ConnectedAccount account,
        IReadOnlySet<string> allowedRepositories,
        out GitHubRepository repository)
    {
        repository = default;
        if (!string.Equals(invocation.OwnerPrincipalId, account.OwnerPrincipalId, StringComparison.Ordinal)
            || !GitHubRepository.TryParse(invocation.TargetScope, out repository)
            || !allowedRepositories.Contains(invocation.TargetScope))
            return false;
        if (invocation.Input.ValueKind != JsonValueKind.Object)
            return false;
        if (!invocation.Input.TryGetProperty("repository", out var inputRepository)
            || inputRepository.ValueKind != JsonValueKind.String
            || !string.Equals(inputRepository.GetString(), invocation.TargetScope, StringComparison.Ordinal))
            return false;
        if (invocation.Input.TryGetProperty("accountId", out var accountId)
            && (accountId.ValueKind != JsonValueKind.String
                || !string.Equals(accountId.GetString(), account.AccountId, StringComparison.Ordinal)))
            return false;
        return true;
    }

    private static object[] NormalizeIssues(JsonElement output)
    {
        var items = IssueArray(output);
        if (items.GetArrayLength() > 50) throw new ArgumentException("Too many issues.");
        return items.EnumerateArray().Select(item =>
        {
            var issue = IssueObject(item);
            var number = RequiredPositiveInt32(issue, "number");
            var title = RequiredText(issue, "title", 256);
            var state = RequiredText(issue, "state", 32);
            var url = OptionalText(issue, "html_url", 2048) ?? OptionalText(issue, "url", 2048);
            return (object)new { number, title, state, url };
        }).ToArray();
    }

    private static JsonElement IssueArray(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.Array) return output;
        if (output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("issues", out var issues)
            && issues.ValueKind == JsonValueKind.Array)
            return issues;
        throw new ArgumentException("Provider result is malformed.");
    }

    private static GitHubIssue? ParseIssue(JsonElement output, bool requireReceipt)
    {
        try
        {
            var issue = IssueObject(output);
            var number = RequiredPositiveInt32(issue, "number");
            var title = RequiredText(issue, "title", 256);
            var body = OptionalText(issue, "body", 64 * 1024);
            var receipt = OptionalText(issue, "html_url", 2048)
                ?? OptionalText(issue, "url", 2048)
                ?? ProviderId(issue);
            return requireReceipt && receipt is null ? null : new(number, title, body, receipt);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JsonElement IssueObject(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Provider result is malformed.");
        if (output.TryGetProperty("issue", out var issue))
        {
            if (issue.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Provider result is malformed.");
            return issue;
        }
        return output;
    }

    private static string? ProviderId(JsonElement issue)
    {
        if (!issue.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => OptionalText(issue, "id", 128),
            JsonValueKind.Number when id.TryGetInt64(out var value) && value > 0
                => value.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Provider result is malformed."),
        };
    }

    private static int RequiredPositiveInt32(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value)
            || !value.TryGetInt32(out var number)
            || number <= 0)
            throw new ArgumentException("Provider result is malformed.");
        return number;
    }

    private static string RequiredText(JsonElement input, string name, int maximum)
        => OptionalText(input, name, maximum) ?? throw new ArgumentException("Required text is missing.");

    private static string? OptionalText(JsonElement input, string name, int maximum)
    {
        if (!input.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Text is malformed.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > maximum
            || text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Text is malformed.");
        return text;
    }

    private static void Only(JsonElement input, params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        if (input.ValueKind != JsonValueKind.Object
            || input.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw new ArgumentException("Unexpected input.");
    }

    private static CapabilityResult Success(JsonElement output)
        => new(CapabilityOutcome.Succeeded, output, null, null, null);

    private static CapabilityResult Failure(string code)
        => new(CapabilityOutcome.Failed, JsonSerializer.SerializeToElement(new { }), null, null, code);

    private static CapabilityResult Unknown(int? number, string? receipt, string code)
        => new(
            CapabilityOutcome.UnknownOutcome,
            number is null
                ? JsonSerializer.SerializeToElement(new { })
                : JsonSerializer.SerializeToElement(new { number }),
            receipt,
            null,
            code);

    internal readonly record struct GitHubRepository(string Owner, string Name)
    {
        public static GitHubRepository Parse(string value)
            => TryParse(value, out var repository) ? repository : throw new ArgumentException("Invalid repository.", nameof(value));

        public static bool TryParse(string value, out GitHubRepository repository)
        {
            repository = default;
            if (!RepositoryPattern().IsMatch(value)) return false;
            var separator = value.IndexOf('/', StringComparison.Ordinal);
            repository = new(value[..separator], value[(separator + 1)..]);
            return true;
        }
    }

    private sealed record GitHubIssue(int Number, string Title, string? Body, string? Receipt);
}

internal sealed class GitHubMcp(
    IMcpClientRuntime runtime,
    string serverId,
    Uri endpoint,
    string credential)
{
    private readonly McpServerEndpoint _endpoint = new(
        serverId,
        endpoint,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = $"Bearer {credential}",
            ["X-MCP-Tools"] = "get_me,list_issues,issue_write,issue_read",
        });

    public Task<McpServerContract> DiscoverAsync(CancellationToken cancellationToken)
        => runtime.DiscoverAsync(_endpoint, McpCallPolicy.ReadOnly, cancellationToken);

    public Task<McpInvocationResult> GetMeAsync(CancellationToken cancellationToken)
        => runtime.CallAsync(
            _endpoint,
            "get_me",
            new Dictionary<string, object?>(),
            McpCallPolicy.ReadOnly,
            cancellationToken);

    public Task<McpInvocationResult> ListIssuesAsync(
        GitHubPlugin.GitHubRepository repository,
        CancellationToken cancellationToken)
        => runtime.CallAsync(
            _endpoint,
            "list_issues",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = repository.Owner,
                ["repo"] = repository.Name,
                ["state"] = "open",
                ["perPage"] = 50,
            },
            McpCallPolicy.ReadOnly,
            cancellationToken);

    public Task<McpInvocationResult> CreateIssueAsync(
        GitHubPlugin.GitHubRepository repository,
        string title,
        string? body,
        CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["method"] = "create",
            ["owner"] = repository.Owner,
            ["repo"] = repository.Name,
            ["title"] = title,
        };
        if (body is not null) arguments["body"] = body;
        return runtime.CallAsync(
            _endpoint,
            "issue_write",
            arguments,
            new(TimeSpan.FromSeconds(30), 512 * 1024, MutationDispatched: true),
            cancellationToken);
    }

    public Task<McpInvocationResult> ReadIssueAsync(
        GitHubPlugin.GitHubRepository repository,
        int number,
        CancellationToken cancellationToken)
        => runtime.CallAsync(
            _endpoint,
            "issue_read",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["method"] = "get",
                ["owner"] = repository.Owner,
                ["repo"] = repository.Name,
                ["issue_number"] = number,
            },
            McpCallPolicy.ReadOnly,
            cancellationToken);
}