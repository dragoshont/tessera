using System.Text.Json;
using Tessera.Core.Broker;
using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;

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
public sealed record ProviderCallResult(
    ProviderCallStatus Status,
    int? HttpStatus = null,
    string? Body = null,
    string Detail = "")
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

    /// <summary>Creates the egress over the policy + resolver + recipes + transport.</summary>
    public ProviderEgress(
        PolicyDecisionPointAdapter pdp,
        CredentialResolver resolver,
        IEnumerable<Recipe> recipes,
        SsrfGuard guard,
        IHttpTransport transport,
        int maxBodyBytes = 1_000_000)
    {
        _pdp = pdp;
        _resolver = resolver;
        _recipes = recipes.ToDictionary(r => r.Target, StringComparer.Ordinal);
        _guard = guard;
        _transport = transport;
        _maxBodyBytes = maxBodyBytes;
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

        var url = CombineUrl(recipe.UpstreamBaseUrl, tool.Path);
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

        if (!string.IsNullOrEmpty(requestBody))
        {
            headers["Content-Type"] = "application/json";
        }

        TransportResponse response;
        try
        {
            response = await _transport.SendAsync(tool.Method, url, headers, requestBody, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ProviderCallResult(ProviderCallStatus.TransportError, Detail: ex.Message);
        }

        var body = response.Body.Length > _maxBodyBytes ? response.Body[.._maxBodyBytes] : response.Body;
        return new ProviderCallResult(ProviderCallStatus.Completed, response.Status, body, $"{tool.Method} {tool.Path} -> {response.Status}");
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
