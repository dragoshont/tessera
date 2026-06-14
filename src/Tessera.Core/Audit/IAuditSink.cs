using Tessera.Core.Model;
using Tessera.Core.Resolution;

namespace Tessera.Core.Audit;

/// <summary>
/// One audit record — who asked, for whom, what they wanted, what was decided, and
/// the <em>status</em> of any credential resolved. Carries no secret material by
/// construction (only identifiers + enums).
/// </summary>
/// <param name="Timestamp">UTC time of the decision.</param>
/// <param name="Caller">The caller id.</param>
/// <param name="CallerVerified">Whether the caller was verified.</param>
/// <param name="OnBehalfOf">The delegated end-user subject, or <c>null</c>.</param>
/// <param name="Target">The target.</param>
/// <param name="Action">The action.</param>
/// <param name="Effect">The decision effect.</param>
/// <param name="Reason">The decision reason.</param>
/// <param name="CredentialStatus">The resolved credential status, or <c>null</c>.</param>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Caller,
    bool CallerVerified,
    string? OnBehalfOf,
    string Target,
    string Action,
    Effect Effect,
    string Reason,
    string? CredentialStatus)
{
    /// <summary>
    /// Builds an entry from a brokering decision. The <see cref="OnBehalfOf"/> field
    /// carries the <em>human-readable principal</em> (the <c>preferred_username</c>
    /// when present, else the stable <c>subject</c>) — the same identity key grants,
    /// bindings, and the portal use, so an audit row is legible and can be scoped to
    /// a person without an oid↔email lookup. Never carries a secret value.
    /// </summary>
    public static AuditEntry From(AccessRequest request, Decision decision, ResolvedCredential? credential) =>
        new(
            DateTimeOffset.UtcNow,
            request.Caller.Id,
            request.Caller.IsVerified,
            request.OnBehalfOf?.PreferredUsername ?? request.OnBehalfOf?.Subject,
            request.Target,
            request.Action,
            decision.Effect,
            decision.Reason,
            credential?.Status.ToString());
}

/// <summary>An append-only, secret-free sink for brokering decisions (ADR 0008).</summary>
public interface IAuditSink
{
    /// <summary>Records one decision. Implementations must never persist secret bytes.</summary>
    void Record(AccessRequest request, Decision decision, ResolvedCredential? credential);
}

/// <summary>An audit sink that discards everything (tests / dev).</summary>
public sealed class NullAuditSink : IAuditSink
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NullAuditSink Instance = new();

    /// <inheritdoc/>
    public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential)
    {
        // Intentionally empty.
    }
}
