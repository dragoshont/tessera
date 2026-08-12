using System.Collections.ObjectModel;

namespace Tessera.Core.Kernel;

internal static class KernelValidation
{
    public static string Text(string value, string parameterName, int maxLength = 4096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Value cannot contain control characters.", parameterName);
        }

        return trimmed;
    }

    public static string VisibleAscii(string value, string parameterName, int maxLength)
    {
        var text = Text(value, parameterName, maxLength);
        if (text.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("Value must contain visible ASCII characters only.", parameterName);
        }

        return text;
    }

    public static DateTimeOffset Timestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timestamp is required.");
        }

        return value.ToUniversalTime();
    }

    public static DateTimeOffset UtcTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }

        return Timestamp(value, parameterName);
    }

    public static string? PersistedNonSecretText(
        string? value,
        string parameterName,
        int maxLength = 4096)
    {
        if (value is null)
        {
            return null;
        }

        var text = Text(value, parameterName, maxLength);
        var lower = text.ToLowerInvariant();
        var markers = new[]
        {
            "authorization: bearer",
            "password=",
            "password:",
            "api_key=",
            "api-key=",
            "apikey=",
            "access_token=",
            "refresh_token=",
            "client_secret=",
        };
        if (markers.Any(lower.Contains)
            || (lower.Contains("bearer ", StringComparison.Ordinal)
                && text.Length - lower.IndexOf("bearer ", StringComparison.Ordinal) > 24))
        {
            throw new ArgumentException("Value appears to contain secret material and cannot be persisted.", parameterName);
        }

        return text;
    }

    public static IReadOnlyList<string> References(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Array.AsReadOnly(values
            .Select(value => Text(value, parameterName, 512))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    public static IReadOnlyDictionary<string, string> Attributes(
        IReadOnlyDictionary<string, string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > 128)
        {
            throw new ArgumentException("Attributes cannot contain more than 128 entries.", parameterName);
        }

        return new ReadOnlyDictionary<string, string>(values.ToDictionary(
            pair => Text(pair.Key, parameterName, 128),
            pair => Text(pair.Value, parameterName, 4096),
            StringComparer.Ordinal));
    }
}