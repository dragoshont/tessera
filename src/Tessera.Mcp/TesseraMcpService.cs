using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tessera.Core.Broker;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Identity;

namespace Tessera.Mcp;

/// <summary>The identity resolved from a forwarded token, before tool dispatch.</summary>
internal sealed record McpIdentity(CallerIdentity? Caller, EndUserAssertion? User, string Detail)
{
    public bool Authenticated => Caller is not null;
}

/// <summary>
/// The testable core of the MCP surface: resolve the forwarded end-user/automation
/// token into a verified identity, then answer the broker tools. Has no dependency
/// on HTTP or the MCP transport, so it is unit-tested directly with a token string.
/// </summary>
public sealed class TesseraMcpService
{
    private readonly ITokenValidator _validator;
    private readonly BrokerCore _broker;
    private readonly PolicyDecisionPoint _pdp;
    private readonly IReadOnlyList<Recipe> _recipes;
    private readonly TesseraMcpOptions _options;
    private readonly IProviderGateway _providers;
    private readonly ILogger? _logger;

    // Compiled, allocation-free diagnostic (CA1848). Logs only non-sensitive token
    // routing metadata + the reason on an unresolved delegation — never the token.
    private static readonly Action<ILogger, string, bool, string, string, string, Exception?> LogDelegationUnresolved =
        LoggerMessage.Define<string, bool, string, string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogDelegationUnresolved)),
            "delegation unresolved: {Detail} (token_present={Present}, aud={Aud}, iss={Iss}, tid={Tid})");

    /// <summary>Creates the MCP service over the identity + broker pipeline.</summary>
    public TesseraMcpService(
        ITokenValidator validator,
        BrokerCore broker,
        PolicyDecisionPoint pdp,
        IReadOnlyList<Recipe> recipes,
        TesseraMcpOptions options,
        IProviderGateway? providers = null,
        ILogger<TesseraMcpService>? logger = null)
    {
        _validator = validator;
        _broker = broker;
        _pdp = pdp;
        _recipes = recipes;
        _options = options;
        _providers = providers ?? DisabledProviderGateway.Instance;
        _logger = logger;
    }

    /// <summary>Lists the provider tools the current identity may call (dry — no upstream call).</summary>
    public async Task<ListProviderToolsResult> ListProviderToolsAsync(string? token, CancellationToken cancellationToken = default)
    {
        var identity = await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (!identity.Authenticated)
        {
            return new ListProviderToolsResult(false, [], identity.Detail);
        }

        var tools = _providers.ListTools(identity.Caller!, identity.User);
        return new ListProviderToolsResult(true, tools, identity.Detail);
    }

    /// <summary>
    /// Calls a provider tool for the current identity. <paramref name="confirmed"/>
    /// must be true to run a write/booking tool.
    /// </summary>
    public async Task<ProviderCallToolResult> CallProviderAsync(
        string? token,
        string target,
        string tool,
        string? argsJson,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (!identity.Authenticated)
        {
            return new ProviderCallToolResult("denied", null, null, identity.Detail);
        }

        return await _providers
            .CallAsync(identity.Caller!, identity.User, target, tool, argsJson, confirmed, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reports the identity Tessera resolved for the current call.</summary>
    public async Task<WhoAmIResult> WhoAmIAsync(string? token, CancellationToken cancellationToken = default)
    {
        var identity = await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        var user = identity.User;
        return new WhoAmIResult(
            Authenticated: identity.Authenticated,
            Caller: identity.Caller?.Id,
            User: user?.PreferredUsername ?? user?.Subject,
            IsAutomation: identity.Caller is not null && user is null,
            Detail: identity.Detail);
    }

    /// <summary>Lists the recipe targets and whether the current identity is granted (dry check, no store).</summary>
    public async Task<ListTargetsResult> ListTargetsAsync(string? token, CancellationToken cancellationToken = default)
    {
        var identity = await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (!identity.Authenticated)
        {
            return new ListTargetsResult(false, [], identity.Detail);
        }

        var targets = new List<TargetInfo>(_recipes.Count);
        foreach (var recipe in _recipes)
        {
            // Granted if the identity may perform ANY exposed action — NOT just the
            // first one. The first exposed action is frequently read:selftest, which
            // only the selftest caller holds; a chat caller legitimately granted
            // read:appointments/read:slots would otherwise be reported "not granted"
            // here, and the model then refuses to call a target it actually may use.
            var actions = recipe.ExposedActions.Count > 0
                ? (IReadOnlyList<string>)recipe.ExposedActions
                : new[] { "read:*" };
            var granted = actions.Any(a =>
                _pdp.Evaluate(new AccessRequest(identity.Caller!, recipe.Target, a, identity.User)).Allowed);
            targets.Add(new TargetInfo(
                recipe.Target,
                recipe.ExposedActions,
                granted,
                recipe.Egress.ToString().ToLowerInvariant()));
        }

        return new ListTargetsResult(true, targets, identity.Detail);
    }

    /// <summary>
    /// Authorizes a (target, action) for the current identity and reports the
    /// decision + credential status. Read-only: makes no upstream call.
    /// </summary>
    public async Task<CheckAccessResult> CheckAccessAsync(
        string? token,
        string target,
        string action,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveAsync(token, cancellationToken).ConfigureAwait(false);
        if (!identity.Authenticated)
        {
            return new CheckAccessResult("deny", identity.Detail, null, false);
        }

        var request = new AccessRequest(identity.Caller!, target, action, identity.User);
        var result = await _broker.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return new CheckAccessResult(
            result.Decision.Effect.ToString().ToLowerInvariant(),
            result.Decision.Reason,
            result.Credential?.Status.ToString().ToLowerInvariant(),
            result.Ok);
    }

    /// <summary>Validates the forwarded token and builds the (caller, end-user) identity, fail-closed.</summary>
    internal async Task<McpIdentity> ResolveAsync(string? token, CancellationToken cancellationToken)
    {
        var identity = await ResolveInnerAsync(token, cancellationToken).ConfigureAwait(false);

        // Diagnostic for an unresolved delegation. Emits ONLY non-sensitive token
        // metadata (aud/iss/tid) plus the reason — never the token or username — so an
        // operator can tell "no token forwarded" from "wrong audience/issuer/tenant".
        if (_logger is not null && !identity.Authenticated)
        {
            var (aud, iss, tid) = SafeTokenClaims(token);
            LogDelegationUnresolved(_logger, identity.Detail, !string.IsNullOrWhiteSpace(token), aud, iss, tid, null);
        }

        return identity;
    }

    private async Task<McpIdentity> ResolveInnerAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new McpIdentity(null, null, "no forwarded end-user token (delegation fails closed)");
        }

        var result = await _validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new McpIdentity(null, null, result.FailureReason ?? "token rejected");
        }

        if (result.IsAppOnly)
        {
            var automationCaller = result.ToCallerIdentity();
            return automationCaller is null
                ? new McpIdentity(null, null, "app-only token missing appid")
                : new McpIdentity(automationCaller, null, $"automation caller {automationCaller.Id}");
        }

        var endUser = result.ToEndUserAssertion();
        if (endUser is null)
        {
            return new McpIdentity(null, null, "token missing a user identity (no oid/preferred_username)");
        }

        // C2: the shared chat caller is trusted by NetworkPolicy + this verified token.
        var caller = new CallerIdentity(_options.ChatCallerId, VerificationMethod.Network);
        return new McpIdentity(caller, endUser, $"{_options.ChatCallerId} acting for {endUser.PreferredUsername ?? endUser.Subject}");
    }

    /// <summary>
    /// Best-effort, UNVALIDATED read of a JWT's <c>aud</c>/<c>iss</c>/<c>tid</c> for
    /// diagnostics only. Returns non-secret routing metadata; never the token or any
    /// user claim. Returns dashes/question marks when absent or unparseable.
    /// </summary>
    private static (string Aud, string Iss, string Tid) SafeTokenClaims(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ("-", "-", "-");
        }

        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return ("?", "?", "?");
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string Read(string key)
            {
                if (!root.TryGetProperty(key, out var value))
                {
                    return "-";
                }
                return value.ValueKind == JsonValueKind.Array
                    ? string.Join(",", value.EnumerateArray().Select(e => e.GetString()))
                    : value.GetString() ?? "-";
            }

            return (Read("aud"), Read("iss"), Read("tid"));
        }
        catch
        {
            return ("?", "?", "?");
        }
    }
}
