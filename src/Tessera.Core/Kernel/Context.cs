using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Tessera.Core.Kernel;

public enum ContextItemKind
{
    CurrentFact,
    UncertainAssertion,
    RelevantEvent,
    EvidenceReference,
    ActionConstraint,
}

public enum ContextOmissionReason
{
    SensitivityNotAllowed,
    SizeBudgetExceeded,
}

public sealed record ContextItem
{
    private ContextItem(
        string itemId,
        ContextItemKind kind,
        string content,
        SensitivityClass sensitivity,
        decimal relevance,
        DateTimeOffset timestamp,
        IReadOnlyList<string> provenanceRefs)
    {
        ItemId = itemId;
        Kind = kind;
        Content = content;
        Sensitivity = sensitivity;
        Relevance = relevance;
        Timestamp = timestamp;
        ProvenanceRefs = provenanceRefs;
    }

    public string ItemId { get; }
    public ContextItemKind Kind { get; }
    public string Content { get; }
    public SensitivityClass Sensitivity { get; }
    public decimal Relevance { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<string> ProvenanceRefs { get; }

    public static ContextItem Create(
        string itemId,
        ContextItemKind kind,
        string content,
        SensitivityClass sensitivity,
        decimal relevance,
        DateTimeOffset timestamp,
        IEnumerable<string> provenanceRefs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relevance, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relevance, 1m);
        var references = KernelValidation.References(provenanceRefs, nameof(provenanceRefs));
        if (references.Count == 0 && kind is ContextItemKind.CurrentFact or ContextItemKind.UncertainAssertion)
        {
            throw new ArgumentException("State context requires provenance.", nameof(provenanceRefs));
        }

        return new ContextItem(
            KernelValidation.Text(itemId, nameof(itemId), 256),
            kind,
            KernelValidation.Text(content, nameof(content), 16384),
            sensitivity,
            relevance,
            KernelValidation.Timestamp(timestamp, nameof(timestamp)),
            references);
    }
}

public sealed record ContextOmission(
    string ItemId,
    ContextOmissionReason Reason);

public sealed record ContextBuildRequest(
    string OwnerPrincipalId,
    string Intent,
    string TaskId,
    int SizeBudget,
    IReadOnlySet<SensitivityClass> AllowedSensitivity,
    IReadOnlyList<string> RequestedCapabilities);

public sealed record ContextEnvelope(
    string ContextId,
    string OwnerPrincipalId,
    string Intent,
    string TaskId,
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<ContextOmission> Omissions,
    IReadOnlyList<string> CapabilityConstraints,
    int SizeBudget)
{
    public IReadOnlyList<ContextItem> CurrentFacts => Items
        .Where(item => item.Kind == ContextItemKind.CurrentFact)
        .ToArray();

    public IReadOnlyList<ContextItem> UncertainAssertions => Items
        .Where(item => item.Kind == ContextItemKind.UncertainAssertion)
        .ToArray();
}

public static class ContextBuilder
{
    public static ContextEnvelope Build(
        ContextBuildRequest request,
        IEnumerable<ContextItem> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.SizeBudget, 1);
        ArgumentNullException.ThrowIfNull(request.AllowedSensitivity);

        var owner = KernelValidation.Text(request.OwnerPrincipalId, nameof(request.OwnerPrincipalId), 256);
        var intent = KernelValidation.Text(request.Intent, nameof(request.Intent), 2048);
        var taskId = KernelValidation.Text(request.TaskId, nameof(request.TaskId), 256);
        var ordered = candidates
            .OrderByDescending(item => item.Relevance)
            .ThenByDescending(item => item.Timestamp)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToArray();

        var included = new List<ContextItem>();
        var omitted = new List<ContextOmission>();
        var used = 0;
        foreach (var item in ordered)
        {
            if (!request.AllowedSensitivity.Contains(item.Sensitivity))
            {
                omitted.Add(new ContextOmission(item.ItemId, ContextOmissionReason.SensitivityNotAllowed));
                continue;
            }

            var size = Encoding.UTF8.GetByteCount(item.Content);
            if (used + size > request.SizeBudget)
            {
                omitted.Add(new ContextOmission(item.ItemId, ContextOmissionReason.SizeBudgetExceeded));
                continue;
            }

            included.Add(item);
            used += size;
        }

        var constraints = request.RequestedCapabilities
            .Select(capability => KernelValidation.Text(capability, nameof(request.RequestedCapabilities), 256))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var contextId = ComputeContextId(owner, intent, taskId, included, omitted, constraints, request.SizeBudget);
        return new ContextEnvelope(
            contextId,
            owner,
            intent,
            taskId,
            included.AsReadOnly(),
            omitted.AsReadOnly(),
            Array.AsReadOnly(constraints),
            request.SizeBudget);
    }

    private static string ComputeContextId(
        string owner,
        string intent,
        string taskId,
        IEnumerable<ContextItem> items,
        IEnumerable<ContextOmission> omissions,
        IEnumerable<string> constraints,
        int sizeBudget)
    {
        var canonical = new StringBuilder()
            .Append(owner).Append('\n')
            .Append(intent).Append('\n')
            .Append(taskId).Append('\n')
            .Append(sizeBudget).Append('\n');
        foreach (var item in items)
        {
            canonical.Append(item.ItemId).Append('|').Append(item.Kind).Append('|')
                .Append(item.Content).Append('|').Append(item.Sensitivity).Append('|')
                .Append(item.Relevance.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(item.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            foreach (var provenanceRef in item.ProvenanceRefs.Order(StringComparer.Ordinal))
            {
                canonical.Append('|').Append(provenanceRef);
            }

            canonical.Append('\n');
        }

        foreach (var omission in omissions)
        {
            canonical.Append("omit|").Append(omission.ItemId).Append('|').Append(omission.Reason).Append('\n');
        }

        foreach (var constraint in constraints)
        {
            canonical.Append("cap|").Append(constraint).Append('\n');
        }

        return $"context:sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";
    }
}