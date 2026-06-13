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

    /// <summary>Creates the MCP service over the identity + broker pipeline.</summary>
    public TesseraMcpService(
        ITokenValidator validator,
        BrokerCore broker,
        PolicyDecisionPoint pdp,
        IReadOnlyList<Recipe> recipes,
        TesseraMcpOptions options)
    {
        _validator = validator;
        _broker = broker;
        _pdp = pdp;
        _recipes = recipes;
        _options = options;
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
            var action = recipe.ExposedActions.Count > 0 ? recipe.ExposedActions[0] : "read:*";
            var request = new AccessRequest(identity.Caller!, recipe.Target, action, identity.User);
            var granted = _pdp.Evaluate(request).Allowed;
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
}
