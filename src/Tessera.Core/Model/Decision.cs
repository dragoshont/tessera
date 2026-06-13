namespace Tessera.Core.Model;

/// <summary>The outcome of a policy evaluation.</summary>
public enum Effect
{
    /// <summary>Denied (the default, fail-closed outcome).</summary>
    Deny = 0,

    /// <summary>Allowed.</summary>
    Allow,

    /// <summary>Allowed in principle, but a human confirmation is required first.</summary>
    StepUp,
}

/// <summary>
/// The policy engine's verdict for an <see cref="AccessRequest"/>.
/// </summary>
/// <remarks>
/// <see cref="Reason"/> is always populated (including for allows) so every
/// decision is auditable. <see cref="Obligations"/> carries any conditions the
/// broker must honour, e.g. <c>{ "step_up": "approve-payment" }</c>.
/// </remarks>
public sealed record Decision
{
    /// <summary>The decision effect.</summary>
    public Effect Effect { get; }

    /// <summary>A human-readable, audit-safe reason for the decision.</summary>
    public string Reason { get; }

    /// <summary>Conditions the broker must honour before proceeding.</summary>
    public IReadOnlyDictionary<string, string> Obligations { get; }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    /// <summary>Creates a decision.</summary>
    public Decision(Effect effect, string reason, IReadOnlyDictionary<string, string>? obligations = null)
    {
        Effect = effect;
        Reason = reason;
        Obligations = obligations ?? Empty;
    }

    /// <summary>True only when the request was allowed outright.</summary>
    public bool Allowed => Effect == Effect.Allow;

    /// <summary>Builds a <see cref="Effect.Deny"/> decision.</summary>
    public static Decision Deny(string reason) => new(Effect.Deny, reason);

    /// <summary>Builds an <see cref="Effect.Allow"/> decision.</summary>
    public static Decision Allow(string reason) => new(Effect.Allow, reason);

    /// <summary>Builds a <see cref="Effect.StepUp"/> decision.</summary>
    public static Decision StepUp(string reason, string obligation) =>
        new(Effect.StepUp, reason, new Dictionary<string, string> { ["step_up"] = obligation });
}
