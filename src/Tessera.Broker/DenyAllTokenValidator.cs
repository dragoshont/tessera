using Tessera.Identity;

namespace Tessera.Broker;

/// <summary>
/// A token validator that always fails closed — used when OIDC is not configured
/// (no issuer/audience), so the broker never accepts a token it cannot verify.
/// </summary>
internal sealed class DenyAllTokenValidator : ITokenValidator
{
    private readonly string _reason;

    public DenyAllTokenValidator(string reason) => _reason = reason;

    public bool DelegationEnabled => false;

    public Task<TesseraTokenResult> ValidateAsync(string token, CancellationToken cancellationToken = default) =>
        Task.FromResult(TesseraTokenResult.Fail(_reason));
}
