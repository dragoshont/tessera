using System.Text.Json;
using Tessera.Core.Broker;
using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Results;

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
        int metadataMaxBytes = 65_536)
    {
        _pdp = pdp;
        _resolver = resolver;
        _recipes = recipes.ToDictionary(r => r.Target, StringComparer.Ordinal);
        _guard = guard;
        _transport = transport;
        _maxBodyBytes = maxBodyBytes;
        _metadataMaxBytes = metadataMaxBytes;
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

        var url = CombineUrl(recipe.UpstreamBaseUrl, path!);
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

        // A metadata result is capped tighter than a full body, so a list/search
        // can't spill more than ids + snippets (service-access spec §"Output classes").
        var cap = tool.OutputClass == ResultClass.Metadata ? Math.Min(_maxBodyBytes, _metadataMaxBytes) : _maxBodyBytes;
        var body = response.Body.Length > cap ? response.Body[..cap] : response.Body;
        return new ProviderCallResult(ProviderCallStatus.Completed, response.Status, body, $"{tool.Method} {tool.Path} -> {response.Status}", tool.OutputClass);
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
