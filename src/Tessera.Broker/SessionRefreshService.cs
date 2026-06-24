using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tessera.Core.Rotation;
using Tessera.Providers;

namespace Tessera.Broker;

/// <summary>
/// The hosted glue for the Mode U rotation owner (ADR 0015): on an interval it asks
/// the <see cref="SessionRefreshOrchestrator"/> to run one rotation pass and logs a
/// secret-free summary. It is registered <b>only</b> when <c>refresh.enabled</c> is
/// set (which config validation already ties to <c>egress.enabled</c>), so deploying
/// the broker never auto-rotates anything by default. The first pass waits one full
/// interval so a restart doesn't immediately hammer upstreams.
/// </summary>
public sealed class SessionRefreshService : BackgroundService
{
    private readonly SessionRefreshOrchestrator _orchestrator;
    private readonly TimeSpan _interval;
    private readonly ISingleWriterLease _lease;
    private readonly ILogger<SessionRefreshService> _logger;

    // Compiled, allocation-free diagnostics (CA1848) — all secret-free (counts only).
    private static readonly Action<ILogger, Exception?> LogIdle =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogIdle)),
            "session-refresh: enabled, but no recipe declares Tessera-owned rotation; idle.");

    private static readonly Action<ILogger, int, int, int, int, int, Exception?> LogPass =
        LoggerMessage.Define<int, int, int, int, int>(
            LogLevel.Information,
            new EventId(2, nameof(LogPass)),
            "session-refresh pass: considered={Considered} rotated={Rotated} dead={Dead} errors={Errors} skipped={Skipped}");

    private static readonly Action<ILogger, Exception?> LogPassFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(LogPassFailed)),
            "session-refresh pass failed; will retry next interval.");

    private static readonly Action<ILogger, Exception?> LogNotWriter =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(4, nameof(LogNotWriter)),
            "session-refresh: another replica holds the single-writer lease; skipping this pass (inert).");

    /// <summary>Creates the hosted refresher over an orchestrator + interval + single-writer lease.</summary>
    public SessionRefreshService(SessionRefreshOrchestrator orchestrator, TimeSpan interval, ILogger<SessionRefreshService> logger, ISingleWriterLease? lease = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _orchestrator = orchestrator;
        _interval = interval;
        _logger = logger;
        // Default: single-replica in-process lease (today's behavior, now an explicit seam).
        _lease = lease ?? new ProcessSingleWriterLease();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_orchestrator.HasOwnedRotation)
        {
            // Enabled but nothing declares rotation.owner = tessera + a refreshSpec —
            // stay idle rather than spinning a pointless timer.
            LogIdle(_logger, null);
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TryRunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // shutdown
            }
#pragma warning disable CA1031 // a background pass must never crash the host — log and try again next tick
            catch (Exception ex)
            {
                LogPassFailed(_logger, ex);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Runs one rotation pass, gated on the single-writer lease (ADR 0026): rotate ONLY
    /// while this process holds the lease. Another replica holding it ⇒ stay inert (no two
    /// writers ever rotate the same single-use session). Returns true if the pass ran,
    /// false if it was skipped because another replica is the writer. Internal for testing.
    /// </summary>
    internal async Task<bool> TryRunPassAsync(CancellationToken cancellationToken)
    {
        // The hold's fencing token guards the write (store-side enforcement is the
        // plan-only follow-on, ADR 0026).
        await using var hold = await _lease.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (hold is null)
        {
            LogNotWriter(_logger, null);
            return false;
        }

        var summary = await _orchestrator.RunPassAsync(cancellationToken).ConfigureAwait(false);
        LogPass(_logger, summary.Considered, summary.Rotated, summary.Dead, summary.Errors, summary.Skipped, null);
        return true;
    }
}
