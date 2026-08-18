namespace Tessera.Identity;

/// <summary>
/// Accepts a token only when exactly one configured OIDC trust lane validates it.
/// This prevents overlapping issuer/audience configuration from silently choosing
/// an identity source by ordering.
/// </summary>
public sealed class CompositeTokenValidator(IReadOnlyList<ITokenValidator> validators) : ITokenValidator
{
    private readonly IReadOnlyList<ITokenValidator> _validators = validators.Count > 0
        ? validators
        : throw new ArgumentException("At least one token validator is required.", nameof(validators));

    public bool DelegationEnabled => _validators.All(validator => validator.DelegationEnabled);

    public async Task<TesseraTokenResult> ValidateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!DelegationEnabled)
            return TesseraTokenResult.Fail("delegation fail-closed: one or more OIDC trust lanes are incomplete");

        var accepted = new List<TesseraTokenResult>(1);
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded) accepted.Add(result);
        }

        return accepted.Count switch
        {
            1 => accepted[0],
            > 1 => TesseraTokenResult.Fail("token rejected: ambiguous OIDC trust lane"),
            _ => TesseraTokenResult.Fail("token rejected by every configured OIDC trust lane"),
        };
    }
}