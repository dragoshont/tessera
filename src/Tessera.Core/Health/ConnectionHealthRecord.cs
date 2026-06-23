namespace Tessera.Core.Health;

/// <summary>
/// The non-secret, per-connection liveness metadata Tessera records by <em>observing
/// real brokered calls</em> (ADR 0025 "use-based truth"). It lives <b>beside</b> the
/// credential store, never inside the credential bundle — so it carries no secret, and
/// adding a new credential backend (e.g. the SDD-04 non-Azure store) never has to
/// re-implement verdict persistence (cross-phase analysis A4).
/// </summary>
/// <param name="VerifiedAlive">
/// The last observed verdict: <c>true</c> = a real call to the upstream succeeded
/// (the session is alive), <c>false</c> = a real call was rejected as unauthorized
/// (the session is dead), <c>null</c> = never observed. Presence alone never sets this
/// (ADR 0025): presence is not liveness.
/// </param>
/// <param name="LastVerifiedAt">
/// When the session was last <em>confirmed alive</em> by a successful real call, or null
/// when it never has. This is the freshness clock the projection decays against — a
/// long-stale confirmation is no longer <c>live</c> (cross-phase analysis A8).
/// </param>
/// <param name="ConsecutiveFailures">
/// How many unauthorized rejections in a row since the last confirmed-alive. Reset to 0
/// on any confirmed-alive. Recorded now as honest verdict metadata; it is the seed for
/// the per-connection breaker that SDD-05's read-through refresh will share (analysis A2),
/// not an admission gate in this passive phase.
/// </param>
public sealed record ConnectionHealthRecord(
    bool? VerifiedAlive = null,
    DateTimeOffset? LastVerifiedAt = null,
    int ConsecutiveFailures = 0);
