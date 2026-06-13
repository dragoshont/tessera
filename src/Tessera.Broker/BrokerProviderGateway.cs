using Tessera.Broker.Egress;
using Tessera.Core.Configuration;
using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
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
        IHttpTransport transport)
    {
        if (!config.Egress.Enabled)
        {
            return DisabledProviderGateway.Instance;
        }

        var guard = new SsrfGuard(config.Egress.AllowedHosts);
        var egress = new ProviderEgress(new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, recipes, guard, transport);
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
                    result.Add(new ProviderToolInfo(recipe.Target, tool.Name, tool.Method, tool.RequiresConfirmation, tool.Description));
                }
            }
        }

        return result;
    }

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
            ProviderCallStatus.TransportError => "error",
            _ => "error",
        };
        return new ProviderCallToolResult(status, result.HttpStatus, result.Body, result.Detail);
    }
}
