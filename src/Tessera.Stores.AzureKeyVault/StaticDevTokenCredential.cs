using Azure.Core;

namespace Tessera.Stores.AzureKeyVault;

/// <summary>
/// A no-op <see cref="TokenCredential"/> for the LOCAL DEV LOOP only. The Lowkey
/// Vault emulator speaks the Azure Key Vault REST API but does not validate the
/// bearer token, so the Azure SDK's <see cref="SecretClient"/> just needs *a*
/// token to attach. This returns a fixed dummy token with a far-future expiry.
///
/// <para><b>Never used in production.</b> It is selected only when
/// <c>TESSERA_KEYVAULT_EMULATOR=lowkey</c> (see
/// <see cref="AzureKeyVaultCredentialStore.FromEnvironment"/>), which the production
/// path never sets. The real path keeps <see cref="Azure.Identity.DefaultAzureCredential"/>
/// (Managed Identity / Workload Identity Federation / SP env), so no live or
/// developer-tool credential is ever used against a real vault.</para>
/// </summary>
internal sealed class StaticDevTokenCredential : TokenCredential
{
    // A syntactically-valid-looking but entirely fake token. Lowkey ignores it.
    private const string DummyToken = "dev.lowkey.token";

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(DummyToken, DateTimeOffset.UtcNow.AddYears(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
