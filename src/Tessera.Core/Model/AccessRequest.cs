using Tessera.Core.Identity;

namespace Tessera.Core.Model;

/// <summary>
/// A request to perform <see cref="Action"/> on <see cref="Target"/> as
/// <see cref="Caller"/>, optionally on behalf of a human.
/// </summary>
/// <remarks>
/// <see cref="OnBehalfOf"/> is set when a human is delegating; <c>null</c> means
/// the caller acts purely as itself (automation). <see cref="Action"/> is a
/// provider-defined verb such as <c>read:listings</c> or <c>write:events.create</c>.
/// </remarks>
/// <param name="Caller">The verified workload identity making the request.</param>
/// <param name="Target">The provider/target being accessed (e.g. <c>health-portal</c>).</param>
/// <param name="Action">The provider-defined action verb.</param>
/// <param name="OnBehalfOf">The delegated human, or <c>null</c> for pure automation.</param>
public sealed record AccessRequest(
    CallerIdentity Caller,
    string Target,
    string Action,
    EndUserAssertion? OnBehalfOf = null);
