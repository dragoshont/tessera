namespace Tessera.Core.Policy;

/// <summary>
/// The action plane a verb operates on (ADR 0019). A plane describes <em>what kind
/// of authority</em> a verb exercises — orthogonal to step-up risk, which is about
/// how dangerous a single call is.
/// </summary>
/// <remarks>
/// Planes are derived from a verb's namespace prefix (the text before the first
/// <c>:</c>), so the existing namespaced verbs classify themselves: <c>read:*</c> ⇒
/// <see cref="Read"/>, <c>use:*</c> ⇒ <see cref="Use"/>, <c>manage:*</c> ⇒
/// <see cref="Manage"/>. Anything else — a legacy verb like <c>write:</c>/<c>pay:</c>
/// or a bare verb with no namespace — is <see cref="Unspecified"/>, so old grants
/// keep working unchanged.
/// </remarks>
public enum ActionPlane
{
    /// <summary>No recognised plane prefix (legacy <c>write:</c>/<c>pay:</c>, or no namespace).</summary>
    Unspecified = 0,

    /// <summary>Observe — read state without changing it (<c>read:</c>).</summary>
    Read,

    /// <summary>Operate within configured behaviour — the data plane (<c>use:</c>).</summary>
    Use,

    /// <summary>Reshape the integration itself — the control plane (<c>manage:</c>).</summary>
    Manage,
}

/// <summary>
/// Classifies action verbs into <see cref="ActionPlane"/>s by their namespace
/// prefix. Pure, allocation-light, case-sensitive (verbs are lowercase by
/// convention, matching the case-sensitive action <see cref="Glob"/>).
/// </summary>
public static class ActionPlanes
{
    /// <summary>
    /// The plane a verb — or a grant action glob — belongs to, taken from the text
    /// before its first <c>:</c>. An empty/null verb or an unrecognised prefix is
    /// <see cref="ActionPlane.Unspecified"/>.
    /// </summary>
    public static ActionPlane Of(string? verb)
    {
        if (string.IsNullOrEmpty(verb))
        {
            return ActionPlane.Unspecified;
        }

        var colon = verb.IndexOf(':');
        var prefix = colon < 0 ? verb.AsSpan() : verb.AsSpan(0, colon);

        if (prefix.Equals("read", StringComparison.Ordinal))
        {
            return ActionPlane.Read;
        }

        if (prefix.Equals("use", StringComparison.Ordinal))
        {
            return ActionPlane.Use;
        }

        if (prefix.Equals("manage", StringComparison.Ordinal))
        {
            return ActionPlane.Manage;
        }

        return ActionPlane.Unspecified;
    }

    /// <summary>
    /// True when a grant action <paramref name="pattern"/> is explicitly scoped to
    /// the manage plane (its prefix is <c>manage:</c>). A broad wildcard (<c>*</c>)
    /// or a <c>use:*</c> grant is deliberately <em>not</em> manage-scoped — the
    /// control plane stays default-deny unless an operator opts in by name
    /// (ADR 0019). This is the rule the PDP uses to keep <c>*</c> from silently
    /// granting control-plane authority.
    /// </summary>
    public static bool IsManageScoped(string pattern) => Of(pattern) == ActionPlane.Manage;

    /// <summary>
    /// The lowercase wire token for a plane (<c>read</c>/<c>use</c>/<c>manage</c>),
    /// or <c>null</c> for <see cref="ActionPlane.Unspecified"/> — used by the
    /// awareness projections and the policy DTO round-trip.
    /// </summary>
    public static string? ToToken(ActionPlane plane) => plane switch
    {
        ActionPlane.Read => "read",
        ActionPlane.Use => "use",
        ActionPlane.Manage => "manage",
        _ => null,
    };

    /// <summary>
    /// The distinct recognised plane tokens across a set of action verbs/globs,
    /// ordered read → use → manage (legacy/unspecified verbs contribute nothing).
    /// Drives the awareness dashboard's plane chips (ADR 0017 / 0019).
    /// </summary>
    public static IReadOnlyList<string> TokensOf(IEnumerable<string> verbs)
    {
        var planes = new SortedSet<ActionPlane>();
        foreach (var verb in verbs)
        {
            var plane = Of(verb);
            if (plane != ActionPlane.Unspecified)
            {
                planes.Add(plane);
            }
        }

        // SortedSet orders by the enum's value: Read(1) < Use(2) < Manage(3).
        return planes.Select(p => ToToken(p)!).ToArray();
    }
}
