using Microsoft.Extensions.Logging;
using Tessera.Core.Broker;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Identity;
using Tessera.Mcp;

namespace Tessera.Broker;

/// <summary>
/// The verified identity resolved for a <c>/v1/broker</c> caller: the workload
/// (WHO) and, for a multi-user caller, the forwarded end-user (FOR WHOM).
/// </summary>
/// <param name="Caller">The verified caller identity, or <c>null</c> when authentication failed.</param>
/// <param name="OnBehalfOf">The forwarded, verified end-user (Mode U), or <c>null</c> (Mode P).</param>
/// <param name="Detail">A secret-free explanation of the outcome.</param>
public sealed record CallerResolution(CallerIdentity? Caller, EndUserAssertion? OnBehalfOf, string Detail)
{
    /// <summary>True once a verified caller identity was established.</summary>
    public bool Authenticated => Caller is not null;

    /// <summary>A resolution that failed authentication, carrying a secret-free reason.</summary>
    public static CallerResolution Fail(string detail) => new(null, null, detail);
}

/// <summary>
/// The testable core of the caller authentication plane (ADR 0021): authenticate a
/// non-human caller from its app-only token (plus an optional forwarded end-user
/// token), then dispatch into the existing broker spine.
/// </summary>
/// <remarks>
/// The MCP surface resolves a single, hardcoded chat caller
/// (<c>ChatCallerId</c>, <see cref="VerificationMethod.Network"/>); this resolves a
/// <em>distinct verified caller per app id</em> (<see cref="VerificationMethod.OidcJwt"/>),
/// so a grant like <c>caller: media-mcp may use:* on sonarr</c> is enforceable. It
/// reuses the already-tested app-only token path
/// (<see cref="TesseraTokenResult.ToCallerIdentity"/>) and the same
/// <see cref="IProviderGateway"/>/<see cref="BrokerCore"/> the chat path uses — it
/// adds no new crypto and changes no policy. HTTP-free, so it is unit-tested
/// directly with token strings.
/// </remarks>
public sealed class CallerBrokerService
{
    private readonly ITokenValidator _validator;
    private readonly BrokerCore _broker;
    private readonly IProviderGateway _providers;
    private readonly ILogger? _logger;

    // Compiled, allocation-free diagnostic (CA1848). Logs only the secret-free reason
    // + whether a caller token was present — never the token or any claim.
    private static readonly Action<ILogger, string, bool, Exception?> LogCallerUnauthenticated =
        LoggerMessage.Define<string, bool>(
            LogLevel.Warning,
            new EventId(1, nameof(LogCallerUnauthenticated)),
            "caller plane unauthenticated: {Detail} (caller_token_present={Present})");

    /// <summary>Creates the caller service over the validator + broker spine + provider gateway.</summary>
    public CallerBrokerService(
        ITokenValidator validator,
        BrokerCore broker,
        IProviderGateway providers,
        ILogger<CallerBrokerService>? logger = null)
    {
        _validator = validator;
        _broker = broker;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Authenticates a caller from its app-only token, plus an OPTIONAL forwarded
    /// end-user token (Mode U). Fail-closed: a missing / invalid / non-app-only
    /// caller token is refused, and an end-user token that is itself app-only is
    /// refused (a human is a subject, not a caller).
    /// </summary>
    public async Task<CallerResolution> AuthenticateAsync(
        string? callerToken, string? onBehalfOfToken, CancellationToken cancellationToken = default)
    {
        var resolution = await AuthenticateInnerAsync(callerToken, onBehalfOfToken, cancellationToken).ConfigureAwait(false);
        if (_logger is not null && !resolution.Authenticated)
        {
            LogCallerUnauthenticated(_logger, resolution.Detail, !string.IsNullOrWhiteSpace(callerToken), null);
        }

        return resolution;
    }

    private async Task<CallerResolution> AuthenticateInnerAsync(
        string? callerToken, string? onBehalfOfToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callerToken))
        {
            return CallerResolution.Fail("no caller token (present an app-only bearer token in Authorization)");
        }

        var callerResult = await _validator.ValidateAsync(callerToken, cancellationToken).ConfigureAwait(false);
        if (!callerResult.Succeeded)
        {
            return CallerResolution.Fail(callerResult.FailureReason ?? "caller token rejected");
        }

        if (!callerResult.IsAppOnly)
        {
            return CallerResolution.Fail(
                "caller token is a user token; the caller plane requires an app-only (client-credentials) token — forward a human as X-Tessera-On-Behalf-Of, not as the caller");
        }

        var caller = callerResult.ToCallerIdentity();
        if (caller is null)
        {
            return CallerResolution.Fail("app-only token is missing an app id (appid/azp)");
        }

        if (string.IsNullOrWhiteSpace(onBehalfOfToken))
        {
            // Mode P (per-account): the caller acts under its own service grant; no end-user.
            return new CallerResolution(caller, null, caller.Id);
        }

        // Mode U (multi-user): a forwarded end-user token rides along; it must be a
        // user token, and the PDP independently requires it to be verified.
        var userResult = await _validator.ValidateAsync(onBehalfOfToken, cancellationToken).ConfigureAwait(false);
        if (!userResult.Succeeded)
        {
            return CallerResolution.Fail($"on-behalf-of token rejected: {userResult.FailureReason ?? "invalid"}");
        }

        if (userResult.IsAppOnly)
        {
            return CallerResolution.Fail("on-behalf-of token is app-only; it must be an end-user token");
        }

        var user = userResult.ToEndUserAssertion();
        if (user is null)
        {
            return CallerResolution.Fail("on-behalf-of token is missing a user identity (no oid/preferred_username)");
        }

        return new CallerResolution(caller, user, $"{caller.Id} acting for {user.PreferredUsername ?? user.Subject}");
    }

    /// <summary>Lists the provider tools the resolved caller may call (dry — no upstream call).</summary>
    public ListProviderToolsResult ListTools(CallerResolution identity)
    {
        if (!identity.Authenticated)
        {
            return new ListProviderToolsResult(false, [], identity.Detail);
        }

        var tools = _providers.ListTools(identity.Caller!, identity.OnBehalfOf);
        return new ListProviderToolsResult(true, tools, identity.Detail);
    }

    /// <summary>
    /// Authorizes a (target, action) for the resolved caller and reports the decision
    /// + credential status. Read-only (makes no upstream call) and audited via the
    /// broker spine.
    /// </summary>
    public async Task<CheckAccessResult> CheckAsync(
        CallerResolution identity, string target, string action, CancellationToken cancellationToken = default)
    {
        if (!identity.Authenticated)
        {
            return new CheckAccessResult("deny", identity.Detail, null, false);
        }

        var request = new AccessRequest(identity.Caller!, target, action, identity.OnBehalfOf);
        var result = await _broker.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return new CheckAccessResult(
            result.Decision.Effect.ToString().ToLowerInvariant(),
            result.Decision.Reason,
            result.Credential?.Status.ToString().ToLowerInvariant(),
            result.Ok);
    }

    /// <summary>
    /// Performs a provider call for the resolved caller. <paramref name="confirmed"/>
    /// must be true to run a write/booking tool. The egress layer
    /// authorization-audits every call (ADR 0021); the credential is never returned.
    /// </summary>
    public async Task<ProviderCallToolResult> CallAsync(
        CallerResolution identity,
        string target,
        string tool,
        string? argsJson,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!identity.Authenticated)
        {
            return new ProviderCallToolResult("unauthenticated", null, null, identity.Detail);
        }

        return await _providers
            .CallAsync(identity.Caller!, identity.OnBehalfOf, target, tool, argsJson, confirmed, cancellationToken)
            .ConfigureAwait(false);
    }
}
