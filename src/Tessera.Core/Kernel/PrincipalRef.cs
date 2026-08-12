using System.Security.Cryptography;
using System.Text;

namespace Tessera.Core.Kernel;

public sealed record PrincipalRef
{
    private const int MaxIssuerLength = 2048;
    private const int MaxIdentityPartLength = 512;

    private PrincipalRef(
        string principalId,
        string issuer,
        string tenant,
        string subject,
        string? displayHint,
        DateTimeOffset createdAt)
    {
        PrincipalId = principalId;
        Issuer = issuer;
        Tenant = tenant;
        Subject = subject;
        DisplayHint = displayHint;
        CreatedAt = createdAt;
    }

    public string PrincipalId { get; }

    public string Issuer { get; }

    public string Tenant { get; }

    public string Subject { get; }

    public string? DisplayHint { get; }

    public DateTimeOffset CreatedAt { get; }

    public static PrincipalRef Create(
        string issuer,
        string tenant,
        string subject,
        string? displayHint,
        DateTimeOffset createdAt)
    {
        var canonicalIssuer = CanonicalizeIssuer(issuer);
        var canonicalTenant = ValidateIdentityPart(tenant, nameof(tenant));
        var canonicalSubject = ValidateIdentityPart(subject, nameof(subject));
        var validatedDisplayHint = ValidateDisplayHint(displayHint);

        if (createdAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(createdAt), "Creation time is required.");
        }

        var identity = string.Join('\n', canonicalIssuer, canonicalTenant, canonicalSubject);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var principalId = $"principal:sha256:{Convert.ToHexStringLower(digest)}";

        return new PrincipalRef(
            principalId,
            canonicalIssuer,
            canonicalTenant,
            canonicalSubject,
            validatedDisplayHint,
            createdAt.ToUniversalTime());
    }

    private static string CanonicalizeIssuer(string issuer)
    {
        var value = ValidateText(issuer, nameof(issuer), MaxIssuerLength);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException("Issuer must be an absolute URI with a host.", nameof(issuer));
        }

        return uri.AbsoluteUri;
    }

    private static string ValidateIdentityPart(string value, string parameterName)
        => ValidateText(value, parameterName, MaxIdentityPartLength);

    private static string? ValidateDisplayHint(string? displayHint)
    {
        if (displayHint is null)
        {
            return null;
        }

        return ValidateText(displayHint, nameof(displayHint), MaxIdentityPartLength);
    }

    private static string ValidateText(string value, string parameterName, int maxLength)
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
}