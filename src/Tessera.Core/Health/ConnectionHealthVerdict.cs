namespace Tessera.Core.Health;

/// <summary>
/// The pure, fail-closed function that turns recorded liveness metadata into the
/// verdict the portal renders — applying the freshness bound so a long-stale
/// confirmation decays off <c>live</c> (ADR 0025; cross-phase analysis A5/A8).
/// Pure and clock-injected so it is exhaustively unit-testable.
/// </summary>
public static class ConnectionHealthVerdict
{
    /// <summary>
    /// Resolves <paramref name="record"/> into <c>(verifiedAlive, lastVerifiedAt)</c> for
    /// <see cref="Portal.PortalService"/> to feed <c>MapStatus</c> and the drawer.
    /// <list type="bullet">
    /// <item>null record (never observed) ⇒ <c>(null, null)</c> — <c>unverified</c>.</item>
    /// <item>last call unauthorized ⇒ <c>(false, lastAlive)</c> — <c>dead</c>, still showing when it was last alive.</item>
    /// <item>confirmed alive AND within <paramref name="maxAge"/> ⇒ <c>(true, lastAlive)</c> — earned <c>live</c>.</item>
    /// <item>confirmed alive BUT older than <paramref name="maxAge"/> ⇒ <c>(null, lastAlive)</c> — decayed to <c>unverified</c> (never silently green).</item>
    /// </list>
    /// Unknown/degenerate states fall through to <c>(null, lastAlive)</c> — never <c>true</c>.
    /// </summary>
    /// <param name="record">The stored metadata, or null when nothing has been observed.</param>
    /// <param name="now">The current instant (injected for testability).</param>
    /// <param name="maxAge">How long a confirmed-alive stays <c>live</c> before it decays to <c>unverified</c>.</param>
    public static (bool? VerifiedAlive, DateTimeOffset? LastVerifiedAt) Resolve(
        ConnectionHealthRecord? record,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (record is null)
        {
            // Never observed — presence is not liveness. Fail closed to unverified.
            return (null, null);
        }

        if (record.VerifiedAlive == false)
        {
            // A real call was rejected as unauthorized — dead. Keep the last-alive stamp
            // (may be null) so the UI can still say "last alive …".
            return (false, record.LastVerifiedAt);
        }

        if (record.VerifiedAlive == true && record.LastVerifiedAt is { } lastAlive)
        {
            // Earned green only while fresh; a stale confirmation decays to unverified.
            var fresh = now - lastAlive <= maxAge;
            return fresh ? (true, lastAlive) : (null, lastAlive);
        }

        // Alive-without-a-stamp, or an unobserved verdict: never green.
        return (null, record.LastVerifiedAt);
    }
}
