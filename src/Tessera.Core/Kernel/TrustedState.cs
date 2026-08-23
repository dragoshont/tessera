namespace Tessera.Core.Kernel;

public enum AssertionAuthority
{
    ExplicitUserCorrection,
    ExplicitUserAssertion,
    UnclassifiedSource,
    DeterministicSystem,
    Extraction,
    ModelInference,
    Derived,
}

public sealed record TrustedStateKey
{
    private TrustedStateKey(string subjectKey, string predicate)
    {
        SubjectKey = subjectKey;
        Predicate = predicate;
    }

    public string SubjectKey { get; }
    public string Predicate { get; }

    public static TrustedStateKey Create(string subjectKey, string predicate)
        => new(
            KernelValidation.Text(subjectKey, nameof(subjectKey), 512),
            KernelValidation.Text(predicate, nameof(predicate), 256));
}

public sealed record TrustedStateQuery
{
    private TrustedStateQuery(
        string ownerPrincipalId,
        IReadOnlyList<TrustedStateKey> keys,
        int maxItems)
    {
        OwnerPrincipalId = ownerPrincipalId;
        Keys = keys;
        MaxItems = maxItems;
    }

    public string OwnerPrincipalId { get; }
    public IReadOnlyList<TrustedStateKey> Keys { get; }
    public int MaxItems { get; }

    public static TrustedStateQuery Create(
        string ownerPrincipalId,
        IEnumerable<TrustedStateKey> keys,
        int maxItems = 100)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxItems, 100);
        var normalizedKeys = keys
            .Distinct()
            .OrderBy(key => key.SubjectKey, StringComparer.Ordinal)
            .ThenBy(key => key.Predicate, StringComparer.Ordinal)
            .ToArray();
        if (normalizedKeys.Length == 0)
        {
            throw new ArgumentException("Trusted State queries require at least one explicit key.", nameof(keys));
        }
        if (normalizedKeys.Length > 100)
        {
            throw new ArgumentException("Trusted State queries cannot contain more than 100 keys.", nameof(keys));
        }

        return new TrustedStateQuery(
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            Array.AsReadOnly(normalizedKeys),
            maxItems);
    }
}

public sealed record TrustedAssertion(
    AssertionRecord Assertion,
    AssertionAuthority Authority)
{
    public string AssertionId => Assertion.AssertionId;
    public string Value => Assertion.Value;
    public IReadOnlyList<string> EvidenceRefs => Assertion.EvidenceRefs;
    public IReadOnlyList<string> LineageRefs => Assertion.LineageRefs;
}

public sealed record TrustedStateEntry(
    TrustedStateKey Key,
    TrustedAssertion? Current,
    IReadOnlyList<TrustedAssertion> History,
    IReadOnlyList<TrustedAssertion> Conflicts,
    IReadOnlyList<EvidenceRecord> Evidence);

public sealed record TrustedStateSnapshot(
    string OwnerPrincipalId,
    IReadOnlyList<TrustedStateEntry> Entries,
    bool IsTruncated);

public sealed class TrustedStateProjection
{
    private readonly IAssertionRepository _assertions;
    private readonly IEvidenceRepository _evidence;

    public TrustedStateProjection(
        IAssertionRepository assertions,
        IEvidenceRepository evidence)
    {
        _assertions = assertions ?? throw new ArgumentNullException(nameof(assertions));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public async Task<TrustedStateSnapshot> ProjectAsync(
        TrustedStateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var loaded = new List<(TrustedStateKey Key, AssertionRecord Assertion)>();
        foreach (var key in query.Keys)
        {
            var history = await _assertions.ListHistoryAsync(
                query.OwnerPrincipalId,
                key.SubjectKey,
                key.Predicate,
                cancellationToken);
            foreach (var assertion in history)
            {
                if (!string.Equals(assertion.OwnerPrincipalId, query.OwnerPrincipalId, StringComparison.Ordinal)
                    || !string.Equals(assertion.SubjectKey, key.SubjectKey, StringComparison.Ordinal)
                    || !string.Equals(assertion.Predicate, key.Predicate, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Trusted State repository returned an assertion outside the requested owner or key.");
                }

                if (assertion.EpistemicStatus is EpistemicStatus.Current
                    or EpistemicStatus.Conflicted
                    or EpistemicStatus.Superseded
                    or EpistemicStatus.Rejected)
                {
                    loaded.Add((key, assertion));
                }
            }
        }

        var duplicateCurrent = loaded
            .Where(item => item.Assertion.EpistemicStatus == EpistemicStatus.Current)
            .GroupBy(item => item.Key)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCurrent is not null)
        {
            throw new InvalidOperationException("Trusted State contains more than one current assertion for a key.");
        }

        var ordered = loaded
            .OrderBy(item => StatusRank(item.Assertion.EpistemicStatus))
            .ThenBy(item => item.Key.SubjectKey, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Predicate, StringComparer.Ordinal)
            .ThenByDescending(item => item.Assertion.ValidFrom)
            .ThenByDescending(item => item.Assertion.CreatedAt)
            .ThenBy(item => item.Assertion.AssertionId, StringComparer.Ordinal)
            .ToArray();
        var selected = ordered.Take(query.MaxItems).ToArray();
        var entries = new List<TrustedStateEntry>(query.Keys.Count);
        foreach (var key in query.Keys)
        {
            var assertions = selected
                .Where(item => item.Key == key)
                .Select(item => new TrustedAssertion(item.Assertion, ClassifyAuthority(item.Assertion)))
                .ToArray();
            var current = assertions.SingleOrDefault(item => item.Assertion.EpistemicStatus == EpistemicStatus.Current);
            var conflicts = assertions
                .Where(item => item.Assertion.EpistemicStatus == EpistemicStatus.Conflicted)
                .ToArray();
            var history = assertions
                .Where(item => item.Assertion.EpistemicStatus is EpistemicStatus.Superseded or EpistemicStatus.Rejected)
                .OrderByDescending(item => item.Assertion.ValidFrom)
                .ThenByDescending(item => item.Assertion.CreatedAt)
                .ThenBy(item => item.AssertionId, StringComparer.Ordinal)
                .ToArray();
            var evidenceIds = assertions
                .SelectMany(item => item.EvidenceRefs)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var evidence = new List<EvidenceRecord>(evidenceIds.Length);
            foreach (var evidenceId in evidenceIds)
            {
                var record = await _evidence.GetAsync(query.OwnerPrincipalId, evidenceId, cancellationToken)
                    ?? throw new InvalidOperationException($"Trusted State evidence '{evidenceId}' is unavailable for the requested owner.");
                if (!string.Equals(record.OwnerPrincipalId, query.OwnerPrincipalId, StringComparison.Ordinal)
                    || !string.Equals(record.EvidenceId, evidenceId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Trusted State repository returned evidence outside the requested owner or identity.");
                }

                evidence.Add(record);
            }

            entries.Add(new TrustedStateEntry(
                key,
                current,
                Array.AsReadOnly(history),
                Array.AsReadOnly(conflicts),
                evidence.AsReadOnly()));
        }

        return new TrustedStateSnapshot(
            query.OwnerPrincipalId,
            entries.AsReadOnly(),
            loaded.Count > selected.Length);
    }

    private static int StatusRank(EpistemicStatus status) => status switch
    {
        EpistemicStatus.Current => 0,
        EpistemicStatus.Conflicted => 1,
        EpistemicStatus.Superseded => 2,
        EpistemicStatus.Rejected => 3,
        _ => 4,
    };

    private static AssertionAuthority ClassifyAuthority(AssertionRecord assertion) => assertion.AssertionType switch
    {
        AssertionType.UserAsserted when assertion.LineageRefs.Count > 0 => AssertionAuthority.ExplicitUserCorrection,
        AssertionType.UserAsserted => AssertionAuthority.ExplicitUserAssertion,
        AssertionType.SourceAsserted => AssertionAuthority.UnclassifiedSource,
        AssertionType.System => AssertionAuthority.DeterministicSystem,
        AssertionType.Extracted => AssertionAuthority.Extraction,
        AssertionType.Inferred => AssertionAuthority.ModelInference,
        AssertionType.Derived => AssertionAuthority.Derived,
        _ => throw new ArgumentOutOfRangeException(nameof(assertion)),
    };
}