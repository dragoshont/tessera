namespace Tessera.Core.Health;

/// <summary>
/// A secret-free record that a connection's liveness <em>degraded</em> — the signal the
/// awareness surface needs so a death is <b>seen</b>, not discovered days later (the RM
/// incident behind ADR 0025; gap analysis §II.1 "silent failure is the cardinal sin").
/// Emitted by <see cref="IConnectionHealthStore"/> when a real call (data plane) or the
/// keep-warm pass (rotation plane) flips a connection into <c>dead</c> (cross-phase
/// analysis A6). Carries identity metadata only — never any credential value.
/// </summary>
/// <param name="ConnectionKey">The connection identity <c>"{target}:{principal}"</c>.</param>
/// <param name="Provider">The target/provider the session is for (parsed from the key).</param>
/// <param name="Principal">The person the session acts for (parsed from the key).</param>
/// <param name="From">The verdict it degraded <em>from</em>: <c>live</c> or <c>unverified</c>.</param>
/// <param name="At">When the degradation was observed.</param>
/// <param name="Remediation">A human next step — what an operator/owner should do.</param>
public sealed record DegradationEvent(
    string ConnectionKey,
    string Provider,
    string Principal,
    string From,
    DateTimeOffset At,
    string Remediation)
{
    /// <summary>The verdict it degraded <em>to</em> — always <c>dead</c> (the only degradation this models).</summary>
    public string To { get; init; } = "dead";
}
