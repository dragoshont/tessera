using System.Text.Json;
using Tessera.Core.Audit;
using Tessera.Core.Broker;
using Tessera.Core.Egress;
using Tessera.Core.Health;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Results;
using Tessera.Core.Stores;

namespace Tessera.Providers;

/// <summary>What happened on a provider-egress call.</summary>
public enum ProviderCallStatus
{
    /// <summary>The policy denied the request.</summary>
    Denied = 0,

    /// <summary>A write/booking tool needs an explicit human confirmation first.</summary>
    StepUpRequired,

    /// <summary>No usable credential resolved for the identity.</summary>
    NoCredential,

    /// <summary>The tool/target isn't an HTTP-egress recipe, or the host isn't allow-listed.</summary>
    NotAllowed,

    /// <summary>The call arguments are invalid (e.g. a full-body tool called without a handle).</summary>
    BadRequest,

    /// <summary>The upstream call completed (see <see cref="ProviderCallResult.HttpStatus"/>).</summary>
    Completed,

    /// <summary>The transport failed.</summary>
    TransportError,
}

/// <summary>The result of a provider-egress call. Carries the upstream result, never the credential.</summary>
/// <param name="Status">What happened.</param>
/// <param name="HttpStatus">The upstream HTTP status, when a call was made.</param>
/// <param name="Body">The upstream response body, when a call was made.</param>
/// <param name="Detail">A secret-free explanation.</param>
/// <param name="OutputClass">The output class the tool declared (service-access spec); null = unclassified. Tells a downstream surface the retention rule + that the body is untrusted provider content.</param>
public sealed record ProviderCallResult(
    ProviderCallStatus Status,
    int? HttpStatus = null,
    string? Body = null,
    string Detail = "",
    ResultClass? OutputClass = null)
{
    /// <summary>True when the upstream call completed with a 2xx status.</summary>
    public bool Ok => Status == ProviderCallStatus.Completed && HttpStatus is >= 200 and < 300;
}

/// <summary>
/// Performs an HTTP-injectable provider call on behalf of a verified identity
/// (ADR 0014): authorize → resolve the bundle by identity → inject the credential →
/// call the allow-listed endpoint → return the result. The caller never sees the
/// credential. Write/booking tools require an explicit confirmation token.
/// </summary>
public sealed class ProviderEgress
{
    private readonly PolicyDecisionPointAdapter _pdp;
    private readonly CredentialResolver _resolver;
    private readonly Dictionary<string, Recipe> _recipes;
    private readonly SsrfGuard _guard;
    private readonly IHttpTransport _transport;
    private readonly IAuditSink _audit;
    private readonly ICredentialWriter? _writer;
    private readonly IConnectionHealthStore? _health;
    private readonly int _maxBodyBytes;
    private readonly int _metadataMaxBytes;

    /// <summary>Creates the egress over the policy + resolver + recipes + transport.</summary>
    public ProviderEgress(
        PolicyDecisionPointAdapter pdp,
        CredentialResolver resolver,
        IEnumerable<Recipe> recipes,
        SsrfGuard guard,
        IHttpTransport transport,
        int maxBodyBytes = 1_000_000,
        int metadataMaxBytes = 65_536,
        IAuditSink? audit = null,
        ICredentialWriter? writer = null,
        IConnectionHealthStore? health = null)
    {
        _pdp = pdp;
        _resolver = resolver;
        _recipes = recipes.ToDictionary(r => r.Target, StringComparer.Ordinal);
        _guard = guard;
        _transport = transport;
        _audit = audit ?? NullAuditSink.Instance;
        _maxBodyBytes = maxBodyBytes;
        _metadataMaxBytes = metadataMaxBytes;
        _writer = writer;
        _health = health;
    }

    /// <summary>
    /// Calls <paramref name="toolName"/> on <paramref name="target"/> for the
    /// verified caller/end-user. <paramref name="confirmed"/> must be true to run a
    /// step-up (write) tool; the body is provider JSON the tool forwards.
    /// </summary>
    public async Task<ProviderCallResult> CallAsync(
        CallerIdentity caller,
        EndUserAssertion? onBehalfOf,
        string target,
        string toolName,
        string? requestBody,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!_recipes.TryGetValue(target, out var recipe) || recipe.Egress != EgressMode.Http || recipe.UpstreamBaseUrl is null)
        {
            return new ProviderCallResult(ProviderCallStatus.NotAllowed, Detail: $"target '{target}' is not an HTTP-egress recipe");
        }

        var tool = recipe.ExposedTools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
        {
            return new ProviderCallResult(ProviderCallStatus.NotAllowed, Detail: $"tool '{toolName}' not found on '{target}'");
        }

        // Authorize the (caller, end-user, target, action) — fail-closed.
        var request = new AccessRequest(caller, target, tool.Action, onBehalfOf);
        var decision = _pdp.Evaluate(request);
        // Audit the authorization decision (secret-free) for every brokered call, so
        // the egress path is no less observable than a dry check — the credential
        // bundle is resolved only after this point and is never recorded.
        _audit.Record(request, decision, null);
        if (!decision.Allowed && decision.Effect != Effect.StepUp)
        {
            return new ProviderCallResult(ProviderCallStatus.Denied, Detail: decision.Reason);
        }

        // Write/booking tools never run autonomously: require an explicit confirm.
        if ((tool.RequiresConfirmation || decision.Effect == Effect.StepUp) && !confirmed)
        {
            return new ProviderCallResult(
                ProviderCallStatus.StepUpRequired,
                Detail: $"'{toolName}' is a write action and needs explicit confirmation (re-issue with confirm=true after reviewing the request)");
        }

        // Build the upstream path: fill {placeholders} from the args, and require a
        // handle for a full-body/attachment tool — so full content is read by a
        // specific id from a prior search, never as a bulk-readable bare path.
        var (path, pathError) = BuildPath(tool, target, requestBody);
        if (pathError is not null)
        {
            return new ProviderCallResult(ProviderCallStatus.BadRequest, Detail: pathError);
        }

        var (query, queryError) = BuildQuery(tool, requestBody);
        if (queryError is not null)
        {
            return new ProviderCallResult(ProviderCallStatus.BadRequest, Detail: queryError);
        }

        var url = CombineUrl(recipe.UpstreamBaseUrl, path!) + query;
        if (!_guard.IsAllowed(url))
        {
            return new ProviderCallResult(ProviderCallStatus.NotAllowed, Detail: "destination host is not on the SSRF allow-list");
        }

        var bundle = await _resolver.ResolveBundleAsync(request, cancellationToken).ConfigureAwait(false);
        if (bundle is null)
        {
            return new ProviderCallResult(ProviderCallStatus.NoCredential, Detail: "no usable credential for this identity");
        }

        var headers = ProviderHeaders.Build(recipe, bundle);
        if (headers is null)
        {
            return new ProviderCallResult(ProviderCallStatus.NoCredential, Detail: "stored bundle lacks the material to inject");
        }

        // A GET/HEAD never carries a body — the args were consumed into the path
        // (e.g. the handle). A write method forwards the args as the JSON body.
        var sendBody = !string.IsNullOrEmpty(requestBody)
            && !IsBodyless(tool.Method);
        if (sendBody)
        {
            headers["Content-Type"] = "application/json";
        }

        TransportResponse response;
        try
        {
            response = await _transport.SendAsync(tool.Method, url, headers, sendBody ? requestBody : null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ProviderCallResult(ProviderCallStatus.TransportError, Detail: ex.Message);
        }

        // Sliding-session write-back (ADR 0014/0015): when the recipe owns it and the
        // upstream rotated the session on this read (a Set-Cookie on a 2xx), capture the
        // rotated cookies and merge them back to the store so the next read uses the live
        // session. Without this, reads stop extending a sliding session and it dies
        // between the owner's keep-warm passes. Best-effort and out-of-band: a non-2xx is
        // ignored (so a logout/clearing cookie can't wipe the session), and a write-back
        // failure never fails the read — the next rotation, or the owner's re-login,
        // recovers.
        if (recipe.AbsorbSetCookie
            && _writer is not null
            && response.Status is >= 200 and < 300
            && CookieWriteBack.BuildRotation(recipe, bundle, response.Headers) is { } rotated
            && _resolver.BindingFor(request) is { } binding)
        {
            try
            {
                await _writer.PutBundleAsync(binding.Credential, rotated, cancellationToken).ConfigureAwait(false);
            }
            catch (StoreException)
            {
                // The live session simply isn't persisted this round — never fail the read.
            }
        }

        // A metadata result is capped tighter than a full body, so a list/search
        // can't spill more than ids + snippets (service-access spec §"Output classes").
        var cap = tool.OutputClass == ResultClass.Metadata ? Math.Min(_maxBodyBytes, _metadataMaxBytes) : _maxBodyBytes;
        var body = response.Body.Length > cap ? response.Body[..cap] : response.Body;

        // Record the use-based liveness verdict (ADR 0025 / SDD-01 P4) from this real
        // call's outcome: a 2xx confirms the session alive; a 401/403 marks it dead. Any
        // other status (5xx, 429) is not a liveness signal and leaves the verdict
        // unchanged. A transport error returned earlier, so only a real HTTP response
        // reaches here. Best-effort + out-of-band: a metadata write never affects the result.
        await RecordLivenessAsync(request, response.Status, cancellationToken).ConfigureAwait(false);

        return new ProviderCallResult(ProviderCallStatus.Completed, response.Status, body, $"{tool.Method} {tool.Path} -> {response.Status}", tool.OutputClass);
    }

    /// <summary>
    /// Folds a real call's HTTP outcome into the connection's use-based liveness verdict
    /// (ADR 0025 / SDD-01 P4). A 2xx ⇒ confirmed alive; a 401/403 ⇒ dead; anything else is
    /// not a verdict. Fail-safe: no store, no binding, or a write error is swallowed so the
    /// brokered call's result is never affected.
    /// <para>
    /// <b>Soft-200 caveat (judge C1, tracked for SDD-05).</b> This trusts the upstream's
    /// status line: a provider that answers <c>200</c> + a login page for an <em>expired</em>
    /// session (the literal ADR 0025 incident class) would be recorded alive. It cannot
    /// misfire in the default posture (egress is off; an RM-style session is not yet
    /// Tessera-rotated), but a recipe-level success assertion (e.g. an expected
    /// content marker) must gate this before the v0.6.0 cutover makes such a provider live.
    /// </para>
    /// </summary>
    private async Task RecordLivenessAsync(AccessRequest request, int httpStatus, CancellationToken cancellationToken)
    {
        if (_health is null)
        {
            return;
        }

        bool? alive = httpStatus is >= 200 and < 300 ? true
            : httpStatus is 401 or 403 ? false
            : null;
        if (alive is null || _resolver.BindingFor(request) is not { } binding || binding.Principal is null)
        {
            return;
        }

        try
        {
            await _health.RecordOutcomeAsync(
                $"{binding.Target}:{binding.Principal}",
                alive.Value,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail a brokered call because its liveness metadata couldn't be written.
        }
    }

    /// <summary>GET/HEAD calls never carry a request body.</summary>
    private static bool IsBodyless(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fills <c>{placeholder}</c> segments in the tool's path from the call args. A
    /// <c>{handle}</c> segment is filled from a target-scoped <see cref="ResultHandle"/>
    /// (rejecting a malformed or cross-provider handle); other placeholders take a
    /// scalar arg of the same name. Every value is URL-encoded (no path injection).
    /// A full-body/attachment tool with no placeholder is rejected — it must read by
    /// a handle, never as a bulk path.
    /// </summary>
    private static (string? Path, string? Error) BuildPath(RecipeTool tool, string target, string? argsJson)
    {
        var placeholders = Placeholders(tool.Path);

        if (tool.RequiresHandle && placeholders.Count == 0)
        {
            return (null, $"'{tool.Name}' returns full content and must be called by a handle, but its path defines no placeholder");
        }

        if (placeholders.Count == 0)
        {
            return (tool.Path, null);
        }

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "arguments must be a JSON object to fill the path placeholders");
            }
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, "arguments are not valid JSON");
        }

        var path = tool.Path;
        foreach (var name in placeholders)
        {
            string value;
            if (string.Equals(name, "handle", StringComparison.Ordinal))
            {
                if (!args.TryGetProperty("handle", out var h) || h.ValueKind != JsonValueKind.String)
                {
                    return (null, "missing required 'handle' argument (read by a handle returned from a prior search)");
                }
                var parsed = ResultHandle.Parse(h.GetString(), target);
                if (parsed is null)
                {
                    return (null, "'handle' is malformed or belongs to a different provider");
                }
                value = parsed.Value;
            }
            else if (!TryGetScalarArg(args, name, out value))
            {
                return (null, $"missing required '{name}' argument for the path");
            }

            path = path.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        return (path, null);
    }

    /// <summary>
    /// Builds the upstream query string from the tool's allow-listed query params
    /// (service-access spec): only names the recipe declared in
    /// <see cref="RecipeTool.AllowedQuery"/> are forwarded, and only when present in
    /// the args as a scalar — so an agent can't smuggle an arbitrary query parameter
    /// onto the upstream call. Names and values are URL-encoded. Returns the leading
    /// <c>?</c> + joined pairs, or an empty string when there is nothing to forward.
    /// </summary>
    private static (string Query, string? Error) BuildQuery(RecipeTool tool, string? argsJson)
    {
        if (tool.AllowedQuery.Count == 0 || string.IsNullOrWhiteSpace(argsJson))
        {
            return ("", null);
        }

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ("", "arguments must be a JSON object to fill the query parameters");
            }
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ("", "arguments are not valid JSON");
        }

        var parts = new List<string>(tool.AllowedQuery.Count);
        foreach (var name in tool.AllowedQuery)
        {
            if (!args.TryGetProperty(name, out var el))
            {
                continue;
            }

            var value = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null, // arrays/objects/null are not forwarded as query params
            };
            if (value is not null)
            {
                parts.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count == 0 ? ("", null) : ("?" + string.Join("&", parts), null);
    }

    /// <summary>Extracts the distinct <c>{name}</c> placeholder names from a path, in order.</summary>
    private static List<string> Placeholders(string path)
    {
        var names = new List<string>();
        var i = 0;
        while (i < path.Length)
        {
            var open = path.IndexOf('{', i);
            if (open < 0)
            {
                break;
            }
            var close = path.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }
            var name = path[(open + 1)..close];
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
            i = close + 1;
        }
        return names;
    }

    /// <summary>Reads a string/number arg as a non-empty string; false when absent or not a scalar.</summary>
    private static bool TryGetScalarArg(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el))
        {
            return false;
        }
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                value = el.GetString() ?? "";
                return value.Length > 0;
            case JsonValueKind.Number:
                value = el.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private static string CombineUrl(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}

/// <summary>
/// A tiny seam over the Core PDP so <see cref="ProviderEgress"/> doesn't need the
/// concrete type (keeps the dependency on the model only).
/// </summary>
public sealed class PolicyDecisionPointAdapter
{
    private readonly Func<AccessRequest, Decision> _evaluate;

    /// <summary>Wraps an evaluate function (the Core PDP's <c>Evaluate</c>).</summary>
    public PolicyDecisionPointAdapter(Func<AccessRequest, Decision> evaluate) => _evaluate = evaluate;

    /// <summary>Evaluates a request.</summary>
    public Decision Evaluate(AccessRequest request) => _evaluate(request);
}

internal static class ProviderJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
