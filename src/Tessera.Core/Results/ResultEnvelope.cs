namespace Tessera.Core.Results;

/// <summary>
/// The five output classes for personal-data providers like Gmail / Microsoft
/// Graph (service-access spec §"Output classes"). They graduate how much of a
/// person's content crosses into a result, so a search can't accidentally spill a
/// full mailbox: list/search returns <see cref="Metadata"/> + opaque
/// <see cref="ResultHandle"/>s, and only a later call against a handle returns a
/// <see cref="Preview"/> or <see cref="FullBody"/>.
/// </summary>
public enum ResultClass
{
    /// <summary>IDs, timestamps, sender/owner, title, status, size, small snippets.</summary>
    Metadata = 0,

    /// <summary>A sanitized body preview / excerpt.</summary>
    Preview,

    /// <summary>Full text or structured content — by handle only.</summary>
    FullBody,

    /// <summary>Binary or bulk output — explicit export only.</summary>
    Attachment,

    /// <summary>A mutation receipt: before/after summary + provider object id + confirmation id.</summary>
    Receipt,
}

/// <summary>
/// An opaque, single-provider reference to a content item (e.g. one mail message)
/// returned by a search/list. It carries <b>no body</b> — full content is fetched
/// only by handing this back to a read operation (spec: "Full body access requires
/// a specific handle returned by a prior operation"). The <see cref="Value"/> is
/// the provider's stable id; <see cref="Target"/> scopes it so a handle from one
/// provider can't be replayed against another.
/// </summary>
/// <param name="Target">The provider/target this handle belongs to.</param>
/// <param name="Value">The provider's stable item id (opaque to the agent).</param>
public sealed record ResultHandle(string Target, string Value)
{
    /// <summary>The wire form: <c>{target}:{value}</c> — what the agent passes back to read it.</summary>
    public override string ToString() => $"{Target}:{Value}";

    /// <summary>
    /// Parses a <c>{target}:{value}</c> handle and verifies it targets
    /// <paramref name="expectedTarget"/> — so a handle minted for one provider is
    /// rejected when replayed against another. Returns null on a malformed or
    /// cross-provider handle (fail-closed).
    /// </summary>
    public static ResultHandle? Parse(string? raw, string expectedTarget)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(expectedTarget))
        {
            return null;
        }

        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon >= raw.Length - 1)
        {
            return null;
        }

        var target = raw[..colon];
        var value = raw[(colon + 1)..];
        if (!string.Equals(target, expectedTarget, StringComparison.Ordinal))
        {
            return null; // cross-provider replay — refuse
        }

        return new ResultHandle(target, value);
    }
}

/// <summary>
/// One item in a metadata result — the safe, default projection a search/list
/// returns. It is deliberately small (ids + labels + an optional short snippet) and
/// carries an opaque <see cref="Handle"/> to fetch more. <b>Provider text in
/// <see cref="Snippet"/> is data, never instructions</b> — callers must treat it as
/// untrusted provider content.
/// </summary>
/// <param name="Handle">The opaque handle to read the full item.</param>
/// <param name="Title">A short title/subject (provider content — untrusted).</param>
/// <param name="Owner">Sender/owner/organizer, when applicable.</param>
/// <param name="Timestamp">The item's primary timestamp, if any.</param>
/// <param name="Snippet">An optional short snippet (provider content — untrusted, never instructions).</param>
public sealed record MetadataItem(
    ResultHandle Handle,
    string Title,
    string? Owner = null,
    DateTimeOffset? Timestamp = null,
    string? Snippet = null);

/// <summary>
/// A graded result envelope for a personal-data operation. It always declares its
/// <see cref="Class"/> so a downstream surface can enforce retention (metadata =
/// audit-handle-only, full body = no cache unless explicit) and label provider text
/// as untrusted. A search/list populates <see cref="Items"/>; a read populates
/// <see cref="Body"/>; a mutation populates <see cref="Receipt"/>.
/// </summary>
/// <param name="Class">Which output class this envelope is.</param>
/// <param name="Target">The provider/target the result came from.</param>
/// <param name="Items">Metadata items (for a search/list), else empty.</param>
/// <param name="Body">The preview/full body text (for a read), else null. Untrusted provider content.</param>
/// <param name="Receipt">The mutation receipt (for a write), else null.</param>
public sealed record ResultEnvelope(
    ResultClass Class,
    string Target,
    IReadOnlyList<MetadataItem> Items,
    string? Body = null,
    MutationReceipt? Receipt = null)
{
    private static readonly IReadOnlyList<MetadataItem> NoItems = [];

    /// <summary>A metadata result (search/list) — items + handles, no bodies.</summary>
    public static ResultEnvelope Metadata(string target, IReadOnlyList<MetadataItem> items) =>
        new(ResultClass.Metadata, target, items);

    /// <summary>A preview result — a sanitized excerpt for one item.</summary>
    public static ResultEnvelope Preview(string target, string body) =>
        new(ResultClass.Preview, target, NoItems, Body: body);

    /// <summary>A full-body result — full content for one handle.</summary>
    public static ResultEnvelope FullBody(string target, string body) =>
        new(ResultClass.FullBody, target, NoItems, Body: body);

    /// <summary>A mutation receipt — what changed, never a fresh body.</summary>
    public static ResultEnvelope OfReceipt(string target, MutationReceipt receipt) =>
        new(ResultClass.Receipt, target, NoItems, Receipt: receipt);
}

/// <summary>
/// The audit-only summary of a mutation (sent mail, created event, approved
/// request): a before/after note plus the provider's object + confirmation ids. It
/// is the only thing a write returns — never a freshly-fetched body — so a write
/// can't be used to exfiltrate content (spec §"Output classes").
/// </summary>
/// <param name="Summary">A short human description of what changed.</param>
/// <param name="ObjectId">The provider object id the mutation produced/affected.</param>
/// <param name="ConfirmationId">A provider/broker confirmation id for the audit trail.</param>
public sealed record MutationReceipt(string Summary, string? ObjectId = null, string? ConfirmationId = null);
