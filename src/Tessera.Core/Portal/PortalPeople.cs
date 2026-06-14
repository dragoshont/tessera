using Tessera.Core.Configuration;

namespace Tessera.Core.Portal;

/// <summary>
/// Derives the portal's <em>people</em> from what already exists — the loaded
/// policy (grants + bindings) plus the admins allow-list — with <b>no database</b>
/// (ADR 0016 / spec §7b). A "user" in the portal is not a stored row; it is a
/// verified OIDC principal that the policy already delegates to, classified by the
/// allow-list. This is a pure projection (no I/O), so it is unit-testable offline
/// and answers "who are the people, and who is admin?" directly from config.
/// </summary>
public static class PortalPeople
{
    /// <summary>
    /// Projects the set of people from the policy and the admins allow-list.
    ///
    /// <para>Principals are the union of every <c>onBehalfOf</c> named in a binding
    /// or a grant, plus every entry in <paramref name="admins"/> (an admin is a
    /// person even before they connect anything). Matching is case-insensitive
    /// (emails/usernames), mirroring how grants match a verified
    /// <c>preferred_username</c>. <see cref="Person.ConnectionCount"/> counts the
    /// bindings acting on that person's behalf.</para>
    ///
    /// <para>The result is ordered admins-first, then alphabetically — the order the
    /// Users view renders.</para>
    /// </summary>
    /// <param name="policy">The loaded grants + bindings (recipes are not people).</param>
    /// <param name="admins">The portal admins allow-list (config, not a DB).</param>
    /// <returns>The people, deduplicated and ordered admins-first then alphabetically.</returns>
    public static IReadOnlyList<Person> Project(
        LoadedPolicy policy,
        IReadOnlyCollection<string> admins)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(admins);

        var adminSet = new HashSet<string>(admins, StringComparer.OrdinalIgnoreCase);

        // Count connections (bindings) per delegated principal. A binding without a
        // principal is pure automation — not a person — so it is skipped here.
        var connectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in policy.Bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Principal))
            {
                continue;
            }

            connectionCounts.TryGetValue(binding.Principal, out var current);
            connectionCounts[binding.Principal] = current + 1;
        }

        // The principal set: everyone a binding or grant delegates to, plus every
        // admin (who is a person even with zero connections). Case-insensitive,
        // and we keep the first-seen spelling for display.
        var principals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Remember(string? principal)
        {
            if (!string.IsNullOrWhiteSpace(principal) && !principals.ContainsKey(principal))
            {
                principals[principal] = principal;
            }
        }

        foreach (var binding in policy.Bindings)
        {
            Remember(binding.Principal);
        }

        foreach (var grant in policy.Grants)
        {
            Remember(grant.OnBehalfOf);
        }

        foreach (var admin in admins)
        {
            Remember(admin);
        }

        var people = new List<Person>(principals.Count);
        foreach (var principal in principals.Values)
        {
            connectionCounts.TryGetValue(principal, out var count);
            var role = adminSet.Contains(principal) ? PortalRole.Admin : PortalRole.Member;
            people.Add(new Person(principal, role, count));
        }

        // Admins first (the operator is listed at the top), then alphabetical so the
        // Users view is stable regardless of policy-file ordering.
        return people
            .OrderByDescending(p => p.Role == PortalRole.Admin)
            .ThenBy(p => p.Principal, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
