using Tessera.Broker.Egress;
using Tessera.Core.Configuration;
using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Tessera.Mcp;
using Tessera.Providers;

namespace Tessera.Broker;

/// <summary>
/// The broker-host implementation of <see cref="IProviderGateway"/>: it owns the
/// real transport + the <see cref="ProviderEgress"/>, so the MCP surface can list
/// and call provider tools without depending on the web/egress layer. Disabled
/// when egress is off (every call refused).
/// </summary>
public sealed class BrokerProviderGateway : IProviderGateway
{
    private readonly ProviderEgress _egress;
    private readonly PolicyDecisionPoint _pdp;
    private readonly IReadOnlyList<Recipe> _recipes;

    /// <summary>Creates the gateway over the egress + policy + recipes.</summary>
    public BrokerProviderGateway(ProviderEgress egress, PolicyDecisionPoint pdp, IReadOnlyList<Recipe> recipes)
    {
        _egress = egress;
        _pdp = pdp;
        _recipes = recipes;
    }

    /// <summary>Builds the gateway from config, or returns the disabled gateway when egress is off.</summary>
    public static IProviderGateway Build(
        TesseraConfig config,
        PolicyDecisionPoint pdp,
        CredentialResolver resolver,
        IReadOnlyList<Recipe> recipes,
        IHttpTransport transport,
        Tessera.Core.Audit.IAuditSink? audit = null,
        ICredentialWriter? writer = null,
        Tessera.Core.Health.IConnectionHealthStore? health = null,
        Tessera.Core.Rotation.ISingleWriterLease? lease = null)
    {
        if (!config.Egress.Enabled)
        {
            return DisabledProviderGateway.Instance;
        }

        var guard = new SsrfGuard(config.Egress.AllowedHosts, config.Egress.AllowPlainHttp);

        // Read-through-on-401 (SDD-05 / ADR 0026): only wired when the operator opts in AND a
        // writable store + a single-writer lease are present (the refresh is a rotation
        // write). Off by default — it acts on the live call path.
        SessionRefresher? refresher = null;
        if (config.Egress.ReadThroughOn401 && writer is not null && lease is not null)
        {
            refresher = new SessionRefresher(transport, writer, guard);
        }

        var egress = new ProviderEgress(
            new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, recipes, guard, transport,
            audit: audit, writer: writer, health: health,
            refresher: refresher, lease: lease, readThroughOn401: config.Egress.ReadThroughOn401);
        return new BrokerProviderGateway(egress, pdp, recipes);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProviderToolInfo> ListTools(CallerIdentity caller, EndUserAssertion? onBehalfOf)
    {
        var result = new List<ProviderToolInfo>();
        foreach (var recipe in _recipes)
        {
            if (recipe.Egress != EgressMode.Http)
            {
                continue;
            }

            foreach (var tool in recipe.ExposedTools)
            {
                // Dry policy check: only surface tools the identity is actually granted.
                var request = new AccessRequest(caller, recipe.Target, tool.Action, onBehalfOf);
                var decision = _pdp.Evaluate(request);
                if (decision.Effect is Effect.Allow or Effect.StepUp)
                {
                    result.Add(new ProviderToolInfo(
                        recipe.Target,
                        tool.Name,
                        tool.Method,
                        tool.RequiresConfirmation,
                        tool.Description,
                        Tessera.Core.Policy.ActionPlanes.ToToken(tool.EffectivePlane),
                        OutputClassToken(tool.OutputClass),
                        tool.RequiresHandle));
                }
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public string? ResolveToolByHttp(string target, string method, string path)
    {
        var wantPath = NormalizePath(path);
        foreach (var recipe in _recipes)
        {
            if (recipe.Egress != EgressMode.Http || !string.Equals(recipe.Target, target, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var tool in recipe.ExposedTools)
            {
                // Exact-path only: a parameterized tool (a {placeholder} in its path)
                // can't be addressed by a concrete HTTP path, so it must be invoked by
                // name — never guess a template from a filled-in path.
                if (tool.Path.Contains('{', StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(tool.Method, method, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(NormalizePath(tool.Path), wantPath, StringComparison.Ordinal))
                {
                    return tool.Name;
                }
            }
        }

        return null;
    }

    /// <summary>Trims surrounding slashes so a recipe's <c>wanted/missing</c> matches a caller's <c>/wanted/missing</c>.</summary>
    private static string NormalizePath(string path) => path.Trim('/');

    /// <inheritdoc/>
    public async Task<ProviderCallToolResult> CallAsync(
        CallerIdentity caller,
        EndUserAssertion? onBehalfOf,
        string target,
        string tool,
        string? argsJson,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        var result = await _egress
            .CallAsync(caller, onBehalfOf, target, tool, argsJson, confirmed, cancellationToken)
            .ConfigureAwait(false);

        var status = result.Status switch
        {
            ProviderCallStatus.Completed => "completed",
            ProviderCallStatus.StepUpRequired => "stepup",
            ProviderCallStatus.Denied => "denied",
            ProviderCallStatus.NoCredential => "nocredential",
            ProviderCallStatus.NotAllowed => "notallowed",
            ProviderCallStatus.BadRequest => "badrequest",
            ProviderCallStatus.TransportError => "error",
            _ => "error",
        };
        return new ProviderCallToolResult(status, result.HttpStatus, result.Body, result.Detail, OutputClassToken(result.OutputClass));
    }

    /// <summary>The lowercase wire token for an output class, or null when unclassified.</summary>
    private static string? OutputClassToken(Tessera.Core.Results.ResultClass? outputClass) => outputClass switch
    {
        Tessera.Core.Results.ResultClass.Metadata => "metadata",
        Tessera.Core.Results.ResultClass.Preview => "preview",
        Tessera.Core.Results.ResultClass.FullBody => "fullBody",
        Tessera.Core.Results.ResultClass.Attachment => "attachment",
        Tessera.Core.Results.ResultClass.Receipt => "receipt",
        _ => null,
    };
}
