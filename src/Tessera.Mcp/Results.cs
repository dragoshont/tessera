namespace Tessera.Mcp;

/// <summary>The identity Tessera resolved from a forwarded token (whoami tool).</summary>
/// <param name="Authenticated">True when a verified identity was established.</param>
/// <param name="Caller">The caller id (WHO), or <c>null</c>.</param>
/// <param name="User">The delegated end-user (FOR WHOM), or <c>null</c> for automation.</param>
/// <param name="IsAutomation">True when this is an app-only (no human) caller.</param>
/// <param name="Detail">A secret-free explanation.</param>
public sealed record WhoAmIResult(
    bool Authenticated,
    string? Caller,
    string? User,
    bool IsAutomation,
    string Detail);

/// <summary>One target the current identity could ask about.</summary>
/// <param name="Target">The target name.</param>
/// <param name="Actions">The actions the recipe exposes.</param>
/// <param name="Granted">Whether the current identity is granted (a dry policy check).</param>
/// <param name="Egress">The egress mode (<c>none</c> = read-only status only).</param>
public sealed record TargetInfo(
    string Target,
    IReadOnlyList<string> Actions,
    bool Granted,
    string Egress);

/// <summary>The targets visible to the current identity (list_targets tool).</summary>
/// <param name="Authenticated">True when a verified identity was established.</param>
/// <param name="Targets">The targets and whether each is granted.</param>
/// <param name="Detail">A secret-free explanation.</param>
public sealed record ListTargetsResult(
    bool Authenticated,
    IReadOnlyList<TargetInfo> Targets,
    string Detail);

/// <summary>The result of a brokered access check (check_access tool).</summary>
/// <param name="Effect">The policy effect: <c>allow</c> / <c>deny</c> / <c>stepup</c>.</param>
/// <param name="Reason">The audit-safe reason.</param>
/// <param name="CredentialStatus">The resolved credential status, or <c>null</c>.</param>
/// <param name="Ok">True when allowed and a usable credential resolved.</param>
public sealed record CheckAccessResult(
    string Effect,
    string Reason,
    string? CredentialStatus,
    bool Ok);
