using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tessera.Broker.Egress;
using Tessera.Core.Audit;
using Tessera.Core.Broker;
using Tessera.Core.Configuration;
using Tessera.Core.Egress;
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
    private const string WriteSummaryHeader = "X-Tessera-Write-Summary";

    // The held-write body is read in full (to hash + replay it), so it is capped: a CalDAV
    // object is small; anything larger is refused rather than buffered.
    private const int MaxConfirmBodyBytes = 256 * 1024;
    private const int MaxSummaryChars = 200;
    private const int MaxBodyExcerptChars = 2000;

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
        IWriteChallengeStore challenges,
        IAuditSink audit,
        TesseraConfig config,
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
            {
                // Out-of-band write confirmation (ADR 0023, resolving HL-18). The forgeable
                // X-Tessera-Confirm header is GONE: a manage: write completes only once a human
                // approved THIS exact request OUT-OF-BAND in the portal — a channel the calling
                // agent cannot drive. The approval is bound to the verified onBehalfOf (never a
                // caller-set header) and to the request's content hash, and is consumed exactly
                // once, so a compromised/prompt-injected caller can neither self-approve nor swap
                // the approved write for a different one.
                if (identity.OnBehalfOf is not { } endUser)
                {
                    return Results.Json(
                        new { error = "a write requires a forwarded end-user identity to approve it" },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var principal = endUser.PreferredUsername ?? endUser.Subject;
                if (string.IsNullOrWhiteSpace(principal))
                {
                    return Results.Json(
                        new { error = "the forwarded end-user has no usable principal to bind an approval to" },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                // Buffer the body once so it can be hashed (the approval binding) and still
                // forwarded verbatim on completion. A CalDAV object is small; over the cap is refused.
                ctx.Request.EnableBuffering();
                var body = await ReadBodyCappedAsync(ctx.Request, MaxConfirmBodyBytes, cancellationToken).ConfigureAwait(false);
                if (body is null)
                {
                    return Results.Json(
                        new { error = $"write body exceeds the {MaxConfirmBodyBytes}-byte confirmation cap" },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                ctx.Request.Body.Position = 0;
                // Bind the approval to the EXACT resource: the validated upstream URL (the object
                // being mutated) + the WebDAV control headers (Destination/Overwrite/Depth), so a
                // compromised caller cannot ride an approval to a different object/host, re-point a
                // MOVE/COPY, or widen a collection op on the re-request — the inbound
                // /v1/egress/{target} route is constant and names nothing.
                var davControl = DavControl(ctx.Request.Headers);
                var contentHash = WriteChallengeHash.Compute(ctx.Request.Method, upstream, davControl, body);
                var now = DateTimeOffset.UtcNow;

                // Already approved out-of-band for this exact write? Consume (single-use), audit, forward.
                var approved = challenges.TryConsumeApproved(principal, target, contentHash, now);
                if (approved is not null)
                {
                    audit.Record(request, Decision.Allow($"write approved out-of-band (challenge {approved.Id})"), null);
                    break; // fall through to bundle resolve + forward
                }

                // Otherwise hold the write for the human (idempotent by content) and return the
                // challenge — the write is NOT forwarded until it is approved + re-requested.
                var summary = Truncate(ctx.Request.Headers[WriteSummaryHeader].ToString(), MaxSummaryChars);
                var pending = challenges.IssueOrGet(
                    new PendingWrite
                    {
                        Id = NewChallengeId(),
                        Caller = identity.Caller!,
                        OnBehalfOf = endUser,
                        Principal = principal,
                        Target = target,
                        Action = action,
                        Method = ctx.Request.Method.ToUpperInvariant(),
                        PathAndQuery = upstream.PathAndQuery,
                        ContentHash = contentHash,
                        UpstreamHost = upstream.Host,
                        Summary = string.IsNullOrWhiteSpace(summary)
                            ? $"{ctx.Request.Method} {upstream.Host}{upstream.PathAndQuery}"
                            : summary,
                        BodyExcerpt = Truncate(Encoding.UTF8.GetString(body), MaxBodyExcerptChars),
                        CreatedAt = now,
                        ExpiresAt = now.AddSeconds(Math.Max(1, config.Egress.ChallengeTtlSeconds)),
                    },
                    now);

                return Results.Json(
                    new
                    {
                        error = "approval required",
                        detail = "this write is held for your out-of-band approval in the Tessera portal",
                        challenge = pending.Id,
                        summary = pending.Summary,
                        approveAt = "/portal",
                        expiresAt = pending.ExpiresAt,
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

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

    /// <summary>Reads the request body fully into memory, or returns null if it exceeds
    /// <paramref name="cap"/> bytes (a write object is small; an oversize body is refused, not buffered).</summary>
    private static async Task<byte[]?> ReadBodyCappedAsync(HttpRequest request, int cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > cap)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    /// <summary>A 128-bit unguessable, URL-safe challenge id (lower-hex).</summary>
    private static string NewChallengeId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Trims + caps a free-text field to <paramref name="max"/> chars (never null).</summary>
    private static string Truncate(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    // WebDAV control headers that change WHICH resource a write hits or HOW MUCH it affects
    // (a MOVE/COPY target, an overwrite flag, a collection depth). The forwarder relays these
    // verbatim, so they are bound into the approval: a re-request cannot re-point or widen an
    // approved write. Hashing exactly the relayed control set (vs a denylist) keeps the binding
    // honest as the header set evolves.
    private static readonly string[] DavControlHeaders = ["Destination", "Overwrite", "Depth"];

    /// <summary>The canonical newline-joined values of the WebDAV control headers, for the
    /// approval content hash (case-insensitive lookup; absent header = empty line).</summary>
    private static string DavControl(IHeaderDictionary headers) =>
        string.Join('\n', DavControlHeaders.Select(h => headers[h].ToString()));

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
