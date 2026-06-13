using Tessera.Core.Model;
using Tessera.Core.Resolution;

namespace Tessera.Core.Broker;

/// <summary>The broker's verdict for a request. Carries status, never secrets.</summary>
/// <param name="Decision">The policy decision.</param>
/// <param name="Credential">The resolved credential status, or <c>null</c> if not resolved.</param>
public sealed record BrokerResult(Decision Decision, ResolvedCredential? Credential)
{
    /// <summary>True when the request was allowed <em>and</em> a usable credential resolved.</summary>
    public bool Ok => Decision.Allowed && Credential is { Usable: true };
}
