using System.IO.Enumeration;

namespace Tessera.Core.Policy;

/// <summary>
/// Shell-glob action matching (<c>*</c>, <c>?</c>), case-sensitive — so
/// <c>read:*</c> grants every read verb but no writes.
/// </summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string value) =>
        FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: false);

    public static bool AnyMatch(IEnumerable<string> patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(pattern, value))
            {
                return true;
            }
        }

        return false;
    }
}
