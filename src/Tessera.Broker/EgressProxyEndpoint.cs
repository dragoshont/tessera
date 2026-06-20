using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tessera.Broker.Egress;
using Tessera.Core.Broker;
using Tessera.Core.Model;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Identity;
using Yarp.ReverseProxy.Forwarder;

namespace Tessera.Broker;

/// <summary>
/// The raw reverse-proxy egress front door (ADR 0022): <c>ANY /v1/egress/{target}</c>.
/// A credential-free domain MCP that speaks a protocol the recipe-tool map can't
/// express — CalDAV/CardDAV, with unbounded object URLs and <c>PROPFIND</c>/
/// <c>REPORT</c>/<c>MKCALENDAR</c> verbs — forwards its raw request here. Tessera
/// authenticates the caller (and the forwarded end-user), authorizes the method as
/// an action, injects the stored credential (HTTP Basic for iCloud), strips the
/// caller's identity, and reverse-proxies the request verbatim to an allow-listed,
/// IP-pinned upstream. The caller never sees the password; the upstream never sees
/// the caller's token.
/// </summary>
/// <remarks>
/// Two independent fail-closed gates (the same posture as <c>/v1/broker</c>): it
/// stays 503 until a caller authenticator is configured (<c>identity.mode=oidc</c> +
/// audience), and it reaches no upstream until <c>egress.enabled</c>. The end-user
/// (<c>onBehalfOf</c>) is derived <em>only</em> from the cryptographically-verified
/// forwarded token, never from a caller-controlled header — so a prompt-injected MCP
/// acting for user A cannot address user B's account (confused-deputy; MCP authz
/// spec). The upstream host travels in <c>X-Tessera-Upstream</c> and is validated
/// against the SSRF allow-list per hop, so RFC 6764 partition redirects
/// (<c>pNN-caldav.icloud.com</c>) work without Tessera ever chasing a redirect off
/// the allow-list.
/// </remarks>
internal static class EgressProxyEndpoint
{
    private const string OnBehalfOfHeader = "X-Tessera-On-Behalf-Of";
    private const string UpstreamHeader = "X-Tessera-Upstream";
    private const string ConfirmHeader = "X-Tessera-Confirm";

    public static void MapEgressProxy(this WebApplication app)
    {
        // Map (not MapGet/MapPost) matches EVERY HTTP method, including the WebDAV verbs
        // (PROPFIND/REPORT/PROPPATCH/MKCALENDAR/MKCOL/MOVE/COPY) CalDAV/CardDAV require.
        app.Map("/v1/egress/{target}", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        string target,
        ITokenValidator validator,
        CallerBrokerService callers,
        BrokerCore broker,
        CredentialResolver resolver,
        IReadOnlyList<Recipe> recipes,
        InjectionEgress egress,
        CancellationToken cancellationToken)
    {
        // Gate 1: a caller authenticator must be configured (fail-closed, like /v1/broker).
        if (!validator.DelegationEnabled)
        {
            return Results.Json(
                new { error = "egress proxy is fail-closed: no caller authenticator configured (set identity.mode=oidc + an audience)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Gate 2: egress must be enabled — deploying the broker never opens an upstream path.
        if (!egress.Enabled)
        {
            return Results.Json(
                new { error = "egress is disabled (set egress.enabled and allow-list the upstream host)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Authenticate the caller (+ optional forwarded end-user) FIRST, before any
        // target/input check — so an unauthenticated caller can't enumerate which proxy
        // targets exist or probe the input contract. onBehalfOf is derived ONLY from the
        // verified token inside AuthenticateAsync — never from a header the caller
        // controls (confused-deputy defense).
        var callerToken = ReadBearer(ctx.Request.Headers.Authorization.ToString());
        var onBehalfOf = ctx.Request.Headers[OnBehalfOfHeader].ToString();
        onBehalfOf = string.IsNullOrWhiteSpace(onBehalfOf) ? null : onBehalfOf;
        var identity = await callers.AuthenticateAsync(callerToken, onBehalfOf, cancellationToken).ConfigureAwait(false);
        if (!identity.Authenticated)
        {
            return Results.Json(
                new { error = "caller not authenticated", detail = identity.Detail },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // The target must be a declared PROXY recipe (a recipe-tool target is reached
        // via /v1/broker, not here).
        var recipe = recipes.FirstOrDefault(r =>
            string.Equals(r.Target, target, StringComparison.Ordinal) && r.Egress == EgressMode.Proxy);
        if (recipe is null)
        {
            return Results.Json(new { error = $"no proxy target '{target}'" }, statusCode: StatusCodes.Status404NotFound);
        }

        // The upstream URL travels in a header so the caller drives the CalDAV path
        // without it being smuggled through the route; the host is validated below.
        var rawUpstream = ctx.Request.Headers[UpstreamHeader].ToString();
        if (!Uri.TryCreate(rawUpstream, UriKind.Absolute, out var upstream))
        {
            return Results.Json(
                new { error = $"missing or invalid {UpstreamHeader} header (an absolute URL is required)" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Host allow-list (SSRF) — after auth, so the allow-list isn't probeable unauthenticated.
        if (!egress.IsUpstreamAllowed(upstream))
        {
            return Results.Json(
                new { error = $"upstream host '{upstream.Host}' is not allow-listed" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Public SaaS speaks HTTPS on the default port only. Reject a caller trying to
        // reach a non-standard port on an allow-listed host (OWASP SSRF: restrict the
        // port, not just the host) — the connect-time guard already blocks private IPs,
        // this closes a blind port-probe / non-443 service on an allow-listed host.
        if (!upstream.IsDefaultPort)
        {
            return Results.Json(
                new { error = $"upstream port {upstream.Port} is not permitted (default port only)" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Map the HTTP method to an action plane: observe (read) vs the control/manage
        // plane (writes). An unknown method is refused (fail-closed).
        var action = MapMethodToAction(ctx.Request.Method);
        if (action is null)
        {
            return Results.Json(
                new { error = $"method '{ctx.Request.Method}' is not permitted on the egress proxy" },
                statusCode: StatusCodes.Status405MethodNotAllowed);
        }

        // Authorize (PDP) and audit the hop, secret-free, via the broker spine. The
        // manage plane (writes) is default-deny + step-up (ADR 0019).
        var request = new AccessRequest(identity.Caller!, target, action, identity.OnBehalfOf);
        var decision = (await broker.HandleAsync(request, cancellationToken).ConfigureAwait(false)).Decision;
        switch (decision.Effect)
        {
            case Effect.Deny:
                return Results.Json(
                    new { error = "denied", detail = decision.Reason },
                    statusCode: StatusCodes.Status403Forbidden);

            case Effect.StepUp:
                // Confirmation gate for a write. NOTE (HL-18): X-Tessera-Confirm guards
                // against an ACCIDENTAL unconfirmed write — it is NOT a security control
                // against a compromised/prompt-injected caller, which can set the header
                // itself (the caller IS the agent we distrust, ADR 0022 §5/§6). Before any
                // manage: write grant ships (Phase 3), this MUST become a Tessera-issued,
                // single-use challenge delivered to the human OUT-OF-BAND. In Phase 0/1 no
                // write grant exists, so the PDP denies writes outright and this path is
                // unreachable — the gate is real only for a non-injected caller's accident.
                var confirmed = string.Equals(
                    ctx.Request.Headers[ConfirmHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);
                if (!confirmed)
                {
                    return Results.Json(
                        new { error = "step-up required", detail = decision.Reason, confirmWith = ConfirmHeader },
                        statusCode: StatusCodes.Status409Conflict);
                }

                break;

            case Effect.Allow:
            default:
                break;
        }

        // Resolve the actual bundle for injection — the bytes stay inside the resolver
        // and the egress; they are never returned, logged, or audited.
        var bundle = await resolver.ResolveBundleAsync(request, cancellationToken).ConfigureAwait(false);
        if (bundle is null)
        {
            return Results.Json(
                new { error = "no usable credential is bound for this target and user" },
                statusCode: StatusCodes.Status424FailedDependency);
        }

        // Forward: inject the credential, strip identity, pin the destination + IP. YARP
        // streams the upstream response straight into ctx.Response.
        var outcome = await egress.ForwardAsync(ctx, upstream, recipe, bundle).ConfigureAwait(false);
        if (outcome.Disposition != EgressDisposition.Forwarded)
        {
            return MapDisposition(outcome.Disposition);
        }

        if (outcome.Error is { } error && error != ForwarderError.None && !ctx.Response.HasStarted)
        {
            return Results.Json(
                new { error = "upstream forward failed", detail = error.ToString() },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Empty; // the response was written by the forwarder
    }

    /// <summary>
    /// Maps an HTTP/WebDAV method to an action plane verb: safe/observational methods
    /// are <c>read:dav</c>; state-changing methods are the manage plane (<c>manage:dav</c>,
    /// default-deny + step-up). An unrecognised method returns null (refused).
    /// </summary>
    private static string? MapMethodToAction(string method) => method.ToUpperInvariant() switch
    {
        "GET" or "HEAD" or "OPTIONS" or "PROPFIND" or "REPORT" => "read:dav",
        "PUT" or "POST" or "PATCH" or "DELETE" or "PROPPATCH"
            or "MKCALENDAR" or "MKCOL" or "MOVE" or "COPY"
            or "LOCK" or "UNLOCK" or "ACL" => "manage:dav",
        _ => null,
    };

    /// <summary>Maps a non-forwarded egress disposition to a fail-closed HTTP status.</summary>
    private static IResult MapDisposition(EgressDisposition disposition) => disposition switch
    {
        EgressDisposition.Disabled =>
            Results.Json(new { error = "egress disabled" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        EgressDisposition.HostNotAllowed =>
            Results.Json(new { error = "upstream host not allow-listed" }, statusCode: StatusCodes.Status403Forbidden),
        EgressDisposition.NoCredential =>
            Results.Json(new { error = "no usable credential" }, statusCode: StatusCodes.Status424FailedDependency),
        EgressDisposition.NotHttpEgress =>
            Results.Json(new { error = "target is not a proxy egress" }, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new { error = "egress failed" }, statusCode: StatusCodes.Status502BadGateway),
    };

    /// <summary>Extracts the bearer value from an <c>Authorization</c> header, or null.</summary>
    private static string? ReadBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }
}
