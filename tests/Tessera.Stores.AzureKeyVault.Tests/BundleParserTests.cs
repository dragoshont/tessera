using Tessera.Stores.AzureKeyVault;
using Xunit;

namespace Tessera.Stores.AzureKeyVault.Tests;

public sealed class BundleParserTests
{
    [Fact]
    public void Parses_a_full_harvester_bundle()
    {
        var bundle = BundleParser.Parse("""
            {
              "access_token": "AT",
              "refresh_token": "RT",
              "cookies": { "sid": "abc", "csrf": "def" },
              "extra": { "subscription_key": "xyz" }
            }
            """);

        Assert.Equal("AT", bundle.AccessToken);
        Assert.Equal("RT", bundle.RefreshToken);
        Assert.True(bundle.HasCookies);
        Assert.Equal("abc", bundle.Cookies!["sid"]);
        Assert.Equal("xyz", bundle.Extra!["subscription_key"]);
        Assert.False(bundle.IsEmpty);
    }

    [Fact]
    public void Empty_or_blank_value_is_empty_bundle()
    {
        Assert.True(BundleParser.Parse("").IsEmpty);
        Assert.True(BundleParser.Parse("   ").IsEmpty);
        Assert.True(BundleParser.Parse("{}").IsEmpty);
    }

    [Fact]
    public void Malformed_json_degrades_to_empty()
    {
        Assert.True(BundleParser.Parse("not json").IsEmpty);
        Assert.True(BundleParser.Parse("[1,2,3]").IsEmpty); // not an object
    }

    [Fact]
    public void Cookies_only_bundle_is_present_not_empty()
    {
        var bundle = BundleParser.Parse("""{ "cookies": { "sid": "abc" } }""");
        Assert.False(bundle.IsEmpty);
        Assert.True(bundle.HasCookies);
        Assert.False(bundle.HasAccessToken);
    }

    [Fact]
    public void FromEnvironment_returns_null_without_a_vault_url()
    {
        var store = AzureKeyVaultCredentialStore.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal));
        Assert.Null(store);
    }

    [Fact]
    public void FromEnvironment_builds_a_store_when_url_present()
    {
        var store = AzureKeyVaultCredentialStore.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TESSERA_VAULT_URL"] = "https://example-vault.vault.azure.net",
            });

        Assert.NotNull(store);
        Assert.Equal("azure-key-vault", store!.Kind);
    }

    [Fact]
    public void FromEnvironment_lowkey_emulator_marks_the_kind()
    {
        // The local dev loop points the same Azure SDK client at Lowkey; the Kind
        // makes /status show local-vs-real at a glance (spec open question 1).
        var store = AzureKeyVaultCredentialStore.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TESSERA_VAULT_URL"] = "https://localhost:8443",
                ["TESSERA_KEYVAULT_EMULATOR"] = "lowkey",
            });

        Assert.NotNull(store);
        Assert.Equal("azure-key-vault(lowkey)", store!.Kind);
    }

    [Fact]
    public void FromEnvironment_without_the_emulator_flag_is_the_real_kind()
    {
        // Absent/!= "lowkey" → the production path is unchanged (real DefaultAzureCredential).
        var store = AzureKeyVaultCredentialStore.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TESSERA_VAULT_URL"] = "https://example-vault.vault.azure.net",
                ["TESSERA_KEYVAULT_EMULATOR"] = "",
            });

        Assert.NotNull(store);
        Assert.Equal("azure-key-vault", store!.Kind);
    }
}
