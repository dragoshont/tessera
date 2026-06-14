using Tessera.Core.Resolution;

namespace Tessera.Core.Portal;

/// <summary>
/// A minimal v1 of the guardian/dependent relationship (ADR 0020 cross-cutting): a
/// guardian who seeded an <c>owner: dependent</c> binding may act <em>as</em> that
/// dependent. It is derived purely from the bindings already in the policy — the
/// guardian named on a dependent binding is the relationship — so there is no new
/// store and no new authority: the PDP grants still gate every action, this only
/// answers "is X allowed to stand in for the dependent Y?".
/// </summary>
public sealed class GuardianRelationships
{
    private readonly IReadOnlyList<TargetBinding> _bindings;

    /// <summary>Creates the relationship view over the policy's bindings.</summary>
    public GuardianRelationships(IEnumerable<TargetBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings.ToArray();
    }

    /// <summary>
    /// True when <paramref name="guardian"/> seeded at least one
    /// <c>owner: dependent</c> binding for <paramref name="dependent"/> — i.e. the
    /// guardian may act as that dependent. Both names are matched case-insensitively
    /// (they are verified principals). A self-pairing is never a guardianship.
    /// </summary>
    public bool MayActAs(string guardian, string dependent)
    {
        if (string.IsNullOrWhiteSpace(guardian)
            || string.IsNullOrWhiteSpace(dependent)
            || string.Equals(guardian, dependent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var binding in _bindings)
        {
            if (binding.Owner == CredentialOwner.Dependent
                && binding.Guardian is not null
                && string.Equals(binding.Guardian, guardian, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.Principal, dependent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The distinct dependents <paramref name="guardian"/> may act as (alphabetical).</summary>
    public IReadOnlyList<string> DependentsOf(string guardian)
    {
        if (string.IsNullOrWhiteSpace(guardian))
        {
            return [];
        }

        var dependents = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in _bindings)
        {
            if (binding.Owner == CredentialOwner.Dependent
                && binding.Principal is not null
                && binding.Guardian is not null
                && string.Equals(binding.Guardian, guardian, StringComparison.OrdinalIgnoreCase))
            {
                dependents.Add(binding.Principal);
            }
        }

        return [.. dependents];
    }
}
