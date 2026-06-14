namespace Tessera.Core.Recipes;

/// <summary>
/// How a provider rotates (keeps warm) a session — declarative recipe config
/// (ADR 0014 / 0015). It names the refresh endpoint and where the rotated tokens
/// appear in the response; it carries <b>no secret</b>. The broker's
/// <c>SessionRefresher</c> (Tessera.Providers) consumes it as the <em>sole session
/// owner</em> path (Mode U) — it is inert until egress is on and Tessera owns
/// rotation for the provider.
/// </summary>
/// <param name="Path">Refresh endpoint path (appended to the recipe base URL).</param>
/// <param name="Method">HTTP method (usually <c>POST</c>).</param>
/// <param name="AccessTokenField">JSON field in the response holding the new access token.</param>
/// <param name="RefreshTokenField">JSON field in the response holding the new refresh token.</param>
/// <param name="AbsorbSetCookie">Whether to also absorb rotated cookies from <c>Set-Cookie</c>.</param>
public sealed record RefreshSpec(
    string Path,
    string Method = "POST",
    string AccessTokenField = "access_token",
    string RefreshTokenField = "refresh_token",
    bool AbsorbSetCookie = true);
