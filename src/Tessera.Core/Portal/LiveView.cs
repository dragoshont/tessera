namespace Tessera.Core.Portal;

/// <summary>
/// How much control the human has over the live remote browser during a captcha
/// hand-off. Read-only is for "watch what the agent is doing"; read-write is the
/// seed/login flow where the human must type and click.
/// </summary>
public enum LiveViewMode
{
    /// <summary>The human can see the session but not interact.</summary>
    ReadOnly,

    /// <summary>The human can drive the session (log in, solve the captcha).</summary>
    ReadWrite,
}

/// <summary>
/// A short-TTL, single-use handle to a live remote-browser session for the captcha
/// hand-off (ADR 0016 / admin-portal spec §3). The portal embeds
/// <see cref="LiveViewUrl"/> and shows the target-identity strip from
/// <see cref="TargetHostname"/>. The live browser runs in the harvest-worker trust
/// zone — the session cookie it produces is written worker→vault and <b>never
/// crosses the broker</b>. The handle itself is not a secret, but it is
/// identity-bound and expires fast, so it is issued only to the authenticated user.
/// </summary>
/// <param name="LiveViewUrl">The embeddable remote-browser URL (short-TTL, single-use).</param>
/// <param name="Mode">Whether the human may drive the session.</param>
/// <param name="SessionTtlSeconds">How long the handle is valid.</param>
/// <param name="ExpiresAt">Absolute expiry (drives the countdown).</param>
/// <param name="TargetHostname">The verified upstream hostname (the anti-phishing anchor).</param>
/// <param name="FaviconUrl">An optional server-vouched favicon for the target strip.</param>
public sealed record LiveViewHandle(
    string LiveViewUrl,
    LiveViewMode Mode,
    int SessionTtlSeconds,
    DateTimeOffset ExpiresAt,
    string TargetHostname,
    string? FaviconUrl = null);

/// <summary>
/// The outcome of requesting a live-view handle: either a <see cref="Handle"/> or a
/// fail-closed <see cref="Reason"/>. Fail-closed is the default — the broker opens
/// no live session until a worker provider is configured.
/// </summary>
/// <param name="Handle">The issued handle, or null when unavailable.</param>
/// <param name="Reason">A secret-free reason when no handle was issued.</param>
public sealed record LiveViewResult(LiveViewHandle? Handle, string? Reason)
{
    /// <summary>True when a handle was issued.</summary>
    public bool Issued => Handle is not null;

    /// <summary>A successful result carrying a handle.</summary>
    public static LiveViewResult Ok(LiveViewHandle handle) => new(handle, null);

    /// <summary>A fail-closed result with a secret-free reason.</summary>
    public static LiveViewResult Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// Issues live-view handles for the captcha hand-off. Implemented by a harvest-worker
/// adapter (e.g. the existing noVNC/sessionkeeper browser worker) — the broker holds
/// only this seam, never the browser. Disabled by default (fail-closed).
/// </summary>
public interface ILiveViewProvider
{
    /// <summary>
    /// Requests a live remote-browser session to seed/re-seed
    /// <paramref name="connectionId"/> on behalf of <paramref name="principal"/>.
    /// Returns a fail-closed result when no worker is configured.
    /// </summary>
    Task<LiveViewResult> RequestAsync(string connectionId, string principal, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default live-view provider: always fail-closed. Deploying the broker never
/// opens a live remote-browser path until a real worker provider is wired (mirrors
/// the egress/broker fail-closed posture). The captcha hand-off is unavailable, not
/// faked.
/// </summary>
public sealed class DisabledLiveViewProvider : ILiveViewProvider
{
    /// <summary>The shared instance.</summary>
    public static readonly DisabledLiveViewProvider Instance = new();

    private DisabledLiveViewProvider()
    {
    }

    /// <inheritdoc />
    public Task<LiveViewResult> RequestAsync(string connectionId, string principal, CancellationToken cancellationToken = default) =>
        Task.FromResult(LiveViewResult.Unavailable(
            "live hand-off is not configured: no harvest-worker live-view provider is wired (fail-closed). "
            + "Configure a browser worker (e.g. the noVNC/sessionkeeper adapter) to enable captcha seeding."));
}

/// <summary>
/// What the broker asks a browser worker to arm for a captcha hand-off — the
/// request half of the worker contract (ADR 0016 §3). It carries only identifiers:
/// the connection to seed, the owning principal (so the worker can bind the session
/// to that person), and the provider/target the worker should navigate to. No
/// secret is ever part of this request — the worker resolves credentials itself,
/// inside its own trust zone.
/// </summary>
/// <param name="ConnectionId">The <c>{provider}:{principal}</c> connection to seed.</param>
/// <param name="Principal">The verified person the seeded session belongs to (identity-bound).</param>
/// <param name="Provider">The provider/target the worker should arm a login for.</param>
public sealed record LiveViewWorkerRequest(string ConnectionId, string Principal, string Provider);

/// <summary>
/// What a browser worker returns when it has armed a live session — the response
/// half of the worker contract. The worker is the component that actually navigates
/// to the provider, so it is the authoritative source of the <see cref="TargetHostname"/>
/// (the anti-phishing anchor) and the embeddable <see cref="LiveViewUrl"/>. The
/// session cookie the human produces is written worker→vault and never returned
/// here. The optional <see cref="TtlSeconds"/> lets the worker pin the session
/// lifetime; when null the broker applies its configured default.
/// </summary>
/// <param name="LiveViewUrl">The embeddable, short-TTL, single-use remote-browser URL.</param>
/// <param name="TargetHostname">The hostname the worker navigated to (server-verified).</param>
/// <param name="TtlSeconds">The session lifetime the worker grants, or null to use the broker default.</param>
/// <param name="ReadWrite">True when the human may drive the session (login/captcha); false for view-only.</param>
/// <param name="FaviconUrl">An optional worker-vouched favicon for the target strip.</param>
public sealed record WorkerLiveViewSession(
    string LiveViewUrl,
    string TargetHostname,
    int? TtlSeconds = null,
    bool ReadWrite = true,
    string? FaviconUrl = null);

/// <summary>
/// The seam to a browser worker that can arm a live remote-browser session and
/// harvest the resulting cookie to the vault (e.g. the homelab noVNC/sessionkeeper
/// pool). The broker holds only this seam — it never touches the browser, the CDP
/// channel, or the cookie. An implementation that cannot arm a session (worker
/// unreachable, slot busy, no mapping) returns <c>null</c>, which the provider maps
/// to a fail-closed result.
/// </summary>
public interface ILiveViewWorker
{
    /// <summary>Arms a live session for <paramref name="request"/>; null when it cannot.</summary>
    Task<WorkerLiveViewSession?> ArmAsync(LiveViewWorkerRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A real live-view provider that brokers a handle to a browser worker (ADR 0016
/// §3, the "Phase 1 wraps the existing noVNC/harvester" path). It authorizes
/// nothing itself — the portal endpoint already checked owner/operator — but it
/// owns the <em>handle contract</em>: it asks the worker to arm a session for the
/// exact principal (identity-bound), stamps a short absolute expiry from the
/// worker's TTL or a configured default, and surfaces the worker-verified hostname.
/// It is fail-closed by construction: any worker failure (unreachable, null, throw)
/// becomes a secret-free <see cref="LiveViewResult.Unavailable"/>, never a faked
/// session. The cookie the human produces never crosses this provider — it goes
/// worker→vault, the cardinal worker-trust-zone invariant.
/// </summary>
public sealed class WorkerLiveViewProvider : ILiveViewProvider
{
    private readonly ILiveViewWorker _worker;
    private readonly int _defaultTtlSeconds;
    private readonly TimeProvider _time;

    /// <summary>Creates a provider over a browser-worker seam.</summary>
    /// <param name="worker">The worker that arms sessions and harvests cookies to the vault.</param>
    /// <param name="defaultTtlSeconds">The handle lifetime used when the worker does not pin one (must be &gt; 0).</param>
    /// <param name="timeProvider">Clock (injected for tests); defaults to the system clock.</param>
    public WorkerLiveViewProvider(ILiveViewWorker worker, int defaultTtlSeconds = 300, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultTtlSeconds, 1);
        _worker = worker;
        _defaultTtlSeconds = defaultTtlSeconds;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<LiveViewResult> RequestAsync(string connectionId, string principal, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(principal))
        {
            return LiveViewResult.Unavailable("live hand-off request is missing a connection id or principal");
        }

        var provider = ProviderOf(connectionId);
        var request = new LiveViewWorkerRequest(connectionId, principal, provider);

        WorkerLiveViewSession? session;
        try
        {
            session = await _worker.ArmAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-closed on ANY worker error — never surface a half-armed session.
            // The message is the worker's own (secret-free by contract); we do not
            // attach request data that could carry identifiers into a shared log.
            return LiveViewResult.Unavailable($"browser worker could not arm a session: {ex.Message}");
        }

        if (session is null || string.IsNullOrWhiteSpace(session.LiveViewUrl) || string.IsNullOrWhiteSpace(session.TargetHostname))
        {
            return LiveViewResult.Unavailable("browser worker has no live session to offer right now (it may be busy or unmapped)");
        }

        var ttl = session.TtlSeconds is > 0 ? session.TtlSeconds.Value : _defaultTtlSeconds;
        var handle = new LiveViewHandle(
            LiveViewUrl: session.LiveViewUrl,
            Mode: session.ReadWrite ? LiveViewMode.ReadWrite : LiveViewMode.ReadOnly,
            SessionTtlSeconds: ttl,
            ExpiresAt: _time.GetUtcNow().AddSeconds(ttl),
            TargetHostname: session.TargetHostname,
            FaviconUrl: session.FaviconUrl);
        return LiveViewResult.Ok(handle);
    }

    /// <summary>The provider/target is the part of <c>{provider}:{principal}</c> before the first colon.</summary>
    private static string ProviderOf(string connectionId)
    {
        var colon = connectionId.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? connectionId[..colon] : connectionId;
    }
}
