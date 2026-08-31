using Microsoft.AspNetCore.Http;
using Tessera.Core.Kernel;

namespace Tessera.Broker;

/// <summary>
/// The one place the broker is allowed to materialize an authenticated principal row.
///
/// Authentication boundary helpers resolve the caller on every request, but a read must
/// not change product state: RFC 9110 §9.2.1 defines GET, HEAD, OPTIONS, and TRACE as
/// <em>safe</em> methods, "essentially read-only". Registering the principal from a safe
/// method made a first authenticated GET a write, so simply reading the product surface
/// created durable rows.
///
/// The principal row exists to satisfy the owner foreign key that unsafe (state-changing)
/// requests need before they persist anything, so registration is bound to exactly those
/// methods. Authentication, authorization, error ordering, fail-closed behavior, and audit
/// semantics are unchanged — only the write is gated.
///
/// Registration itself stays idempotent (<c>ON CONFLICT DO NOTHING</c>), so a repeated
/// mutation still resolves to exactly one principal row.
/// </summary>
internal static class PrincipalRegistration
{
    /// <summary>RFC 9110 §9.2.1 safe methods: read-only, must not change product state.</summary>
    /// <remarks>An absent/unknown method is treated as unsafe, preserving write capability.</remarks>
    public static bool IsSafeMethod(string? method)
        => method is not null
            && (HttpMethods.IsGet(method)
                || HttpMethods.IsHead(method)
                || HttpMethods.IsOptions(method)
                || HttpMethods.IsTrace(method));

    /// <summary>True when the request is a read and therefore may not register a principal.</summary>
    public static bool IsSafeRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsSafeMethod(context.Request.Method);
    }

    /// <summary>
    /// Registers the authenticated principal for unsafe (state-changing) requests only.
    /// A safe-method request returns without touching the store.
    /// </summary>
    public static async ValueTask RegisterForMutationAsync(
        HttpContext context,
        IPrincipalRepository principals,
        PrincipalRef principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(principal);
        if (IsSafeRequest(context))
        {
            return;
        }

        await principals.AddAsync(principal, cancellationToken).ConfigureAwait(false);
    }
}
