namespace Tessera.Core.Resolution;

/// <summary>The status of a resolved credential — all a caller ever learns.</summary>
public enum CredentialStatus
{
    /// <summary>No bundle / no binding.</summary>
    Absent = 0,

    /// <summary>A bundle exists and has a usable access/refresh token or cookies.</summary>
    Present,

    /// <summary>A bundle exists but is missing tokens/cookies.</summary>
    Incomplete,

    /// <summary>The store could not be read.</summary>
    Error,
}

/// <summary>
/// The result of resolution. Deliberately carries no secret material — only the
/// <see cref="Status"/> and a secret-free <see cref="Detail"/>.
/// </summary>
/// <param name="Target">The target that was resolved.</param>
/// <param name="Status">The credential status.</param>
/// <param name="Detail">A secret-free explanation (e.g. "has access_token, cookies").</param>
public sealed record ResolvedCredential(
    string Target,
    CredentialStatus Status,
    string Detail = "")
{
    /// <summary>True when a usable credential is present.</summary>
    public bool Usable => Status == CredentialStatus.Present;
}
