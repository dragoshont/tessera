using System.Security.Cryptography;
using System.Text;

namespace Tessera.Core.Product;

public enum AccountLifecycle
{
    Connecting,
    Connected,
    Degraded,
    AuthRequired,
    Error,
    Disabled,
    Revoked,
}

public enum AccountHealth
{
    Unknown,
    Healthy,
    Degraded,
    AuthRequired,
    Error,
}

public static class AccountStateContractExtensions
{
    public static string ToContractValue(this AccountLifecycle value) => value switch
    {
        AccountLifecycle.AuthRequired => "AUTH_REQUIRED",
        _ => value.ToString().ToUpperInvariant()
    };
    public static string ToContractValue(this AccountHealth value) => value switch
    {
        AccountHealth.AuthRequired => "AUTH_REQUIRED",
        _ => value.ToString().ToUpperInvariant()
    };
}

public sealed record ConnectedAccount(
    string OwnerPrincipalId,
    string AccountId,
    string ProviderId,
    string PluginId,
    string PluginVersion,
    string DisplayName,
    string? IdentityHint,
    AccountLifecycle Lifecycle,
    string CredentialRef,
    AccountHealth Health,
    DateTimeOffset? LastSuccessfulUse,
    string NonSecretConfigJson,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<AccountCapabilityBinding> CapabilityBindings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version)
{
    public string? ProviderAccountId { get; init; }
    public IReadOnlyList<string> ProviderScopes { get; init; } = [];
}

public static class ConnectedAccountCredentialRef
{
    public static string Create(string ownerPrincipalId, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return $"r2/account/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerPrincipalId)))}/{accountId}";
    }

    public static void Validate(ConnectedAccount account, string ownerPrincipalId)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!string.Equals(account.OwnerPrincipalId, ownerPrincipalId, StringComparison.Ordinal)
            || !string.Equals(account.CredentialRef, Create(ownerPrincipalId, account.AccountId), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Account credential reference is not bound to the canonical owner.");
    }
}

public sealed record AccountCapabilityBinding(
    string PluginId,
    string PluginVersion,
    string CapabilityId,
    string CapabilityVersion);

public sealed record PluginInstallation(
    string OwnerPrincipalId,
    string PluginId,
    string PluginVersion,
    string Name,
    string Publisher,
    string PackageHash,
    string ManifestJson,
    string ConfigurationJson,
    bool Enabled,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record ModelProfile(
    string OwnerPrincipalId,
    string ProfileId,
    string AccountId,
    string AdapterKind,
    string Endpoint,
    string Model,
    int ContextLimit,
    bool SupportsStreaming,
    bool SupportsTools,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed class ProductConcurrencyException(string message) : InvalidOperationException(message);