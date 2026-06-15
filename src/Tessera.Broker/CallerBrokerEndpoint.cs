using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tessera.Identity;

namespace Tessera.Broker;

/// <summary>
/// The non-human caller surface (ADR 0021): <c>POST /v1/broker</c>. A workload (a
/// domain MCP, a CLI, a workflow) presents its own app-only bearer token in
/// <c>Authorization</c> and, for a multi-user caller, a forwarded end-user token in
/// <c>X-Tessera-On-Behalf-Of</c>. The endpoint authenticates the caller into a
/// distinct verified identity, then dispatches into the existing broker spine
/// (PDP → resolve → egress). Two independent fail-closed gates: it stays 503 until a
/// caller authenticator is configured (<c>identity.mode=oidc</c> + audience), and it
/// reaches no upstream until <c>egress.enabled</c> (the gateway is disabled until then).
/// </summary>
internal static class CallerBrokerEndpoint
{
    private const string OnBehalfOfHeader = "X-Tessera-On-Behalf-Of";

    public static void MapCallerBroker(this WebApplication app)
    {
        app.MapPost("/v1/broker", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx, CallerBrokerService svc, ITokenValidator validator, CancellationToken cancellationToken)
    {
        // Gate 1: a caller authenticator must be configured, else fail closed (the
        // same posture the endpoint shipped with before the plane existed).
        if (!validator.DelegationEnabled)
        {
            return Results.Json(
                new { error = "broker endpoint is fail-closed: no caller authenticator configured (set identity.mode=oidc + an audience). The chat consumer uses the MCP surface at /mcp." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        BrokerCallRequest? body;
        try
        {
            body = await ctx.Request.ReadFromJsonAsync<BrokerCallRequest>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Target))
        {
            return Results.Json(new { error = "body must include 'target'" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var callerToken = ReadBearer(ctx.Request.Headers.Authorization.ToString());
        var onBehalfOf = ctx.Request.Headers[OnBehalfOfHeader].ToString();
        onBehalfOf = string.IsNullOrWhiteSpace(onBehalfOf) ? null : onBehalfOf;

        var identity = await svc.AuthenticateAsync(callerToken, onBehalfOf, cancellationToken).ConfigureAwait(false);
        if (!identity.Authenticated)
        {
            return Results.Json(
                new { error = "caller not authenticated", detail = identity.Detail },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var op = string.IsNullOrWhiteSpace(body.Op) ? "call" : body.Op.ToLowerInvariant();
        switch (op)
        {
            case "list-tools":
                return Results.Json(svc.ListTools(identity));

            case "check":
                if (string.IsNullOrWhiteSpace(body.Action))
                {
                    return Results.Json(new { error = "op 'check' requires 'action'" }, statusCode: StatusCodes.Status400BadRequest);
                }

                return Results.Json(await svc.CheckAsync(identity, body.Target, body.Action, cancellationToken).ConfigureAwait(false));

            case "call":
                if (string.IsNullOrWhiteSpace(body.Tool))
                {
                    return Results.Json(new { error = "op 'call' requires 'tool'" }, statusCode: StatusCodes.Status400BadRequest);
                }

                var argsJson = body.Args is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } a
                    ? a.GetRawText()
                    : null;
                var result = await svc
                    .CallAsync(identity, body.Target, body.Tool, argsJson, body.Confirm, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(result, statusCode: StatusForCall(result.Status));

            default:
                return Results.Json(
                    new { error = $"unknown op '{op}' (expected call|list-tools|check)" },
                    statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Maps a provider-call status to an HTTP code (fail-closed: an unknown status is a 502).</summary>
    private static int StatusForCall(string status) => status switch
    {
        "completed" => StatusCodes.Status200OK,
        "stepup" => StatusCodes.Status409Conflict,
        "denied" => StatusCodes.Status403Forbidden,
        "notallowed" => StatusCodes.Status403Forbidden,
        "nocredential" => StatusCodes.Status424FailedDependency,
        "badrequest" => StatusCodes.Status400BadRequest,
        "unauthenticated" => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status502BadGateway,
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

/// <summary>The <c>POST /v1/broker</c> request body.</summary>
/// <param name="Op">The operation: <c>call</c> (default) / <c>list-tools</c> / <c>check</c>.</param>
/// <param name="Target">The provider/target (required).</param>
/// <param name="Tool">The tool name (required for <c>call</c>).</param>
/// <param name="Action">The action verb (required for <c>check</c>).</param>
/// <param name="Args">The call arguments as a JSON object (filled into the tool's path/body).</param>
/// <param name="Confirm">True to run a write/booking tool (step-up).</param>
public sealed record BrokerCallRequest(
    string? Op,
    string? Target,
    string? Tool,
    string? Action,
    JsonElement? Args,
    bool Confirm);
