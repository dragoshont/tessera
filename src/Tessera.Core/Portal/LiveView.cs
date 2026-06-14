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
