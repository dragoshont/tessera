using Tessera.Core.Model;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ResolverTests
{
    [Fact]
    public void Binding_matches_target_and_principal()
    {
        var binding = new TargetBinding("health-portal", "health-portal-session", "alice@example.com");

        Assert.True(binding.Matches(TestData.Request(target: "health-portal", onBehalfOf: TestData.VerifiedUser("alice@example.com"))));
        Assert.False(binding.Matches(TestData.Request(target: "health-portal", onBehalfOf: TestData.VerifiedUser("bob@example.com"))));
        Assert.False(binding.Matches(TestData.Request(target: "marketplace", onBehalfOf: TestData.VerifiedUser("alice@example.com"))));
    }

    [Theory]
    [InlineData(true, false, false, CredentialStatus.Present)]
    [InlineData(false, true, false, CredentialStatus.Present)]
    [InlineData(false, false, true, CredentialStatus.Present)]
    public void Assess_present_when_any_usable_material(bool access, bool refresh, bool cookies, CredentialStatus expected)
    {
        var bundle = new CredentialBundle(
            AccessToken: access ? "a" : null,
            RefreshToken: refresh ? "r" : null,
            Cookies: cookies ? new Dictionary<string, string> { ["sid"] = "x" } : null);

        var (status, _) = CredentialResolver.Assess(bundle);
        Assert.Equal(expected, status);
    }

    [Fact]
    public void Assess_absent_for_empty_and_incomplete_for_extra_only()
    {
        Assert.Equal(CredentialStatus.Absent, CredentialResolver.Assess(CredentialBundle.Empty).Status);

        var extraOnly = new CredentialBundle(Extra: new Dictionary<string, string> { ["note"] = "x" });
        Assert.Equal(CredentialStatus.Incomplete, CredentialResolver.Assess(extraOnly).Status);
    }

    [Fact]
    public void Assess_detail_names_material_kinds_never_values()
    {
        var bundle = new CredentialBundle(AccessToken: "super-secret-value", RefreshToken: "another-secret");
        var (_, detail) = CredentialResolver.Assess(bundle);

        Assert.Contains("access_token", detail, StringComparison.Ordinal);
        Assert.Contains("refresh_token", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_absent_without_a_binding()
    {
        var resolver = new CredentialResolver([], new InMemoryCredentialStore());
        var result = await resolver.ResolveAsync(TestData.Request());

        Assert.Equal(CredentialStatus.Absent, result.Status);
        Assert.Contains("no target binding", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_present_when_bundle_has_tokens()
    {
        var store = new InMemoryCredentialStore();
        store.Put("health-portal-session", new CredentialBundle(AccessToken: "tok"));
        var resolver = new CredentialResolver(
            [new TargetBinding("health-portal", "health-portal-session", "alice@example.com")],
            store);

        var result = await resolver.ResolveAsync(TestData.Request(target: "health-portal", onBehalfOf: TestData.VerifiedUser("alice@example.com")));

        Assert.Equal(CredentialStatus.Present, result.Status);
        Assert.True(result.Usable);
    }

    [Fact]
    public async Task Resolve_error_when_store_throws()
    {
        var resolver = new CredentialResolver(
            [new TargetBinding("health-portal", "health-portal-session")],
            new ThrowingStore());

        var result = await resolver.ResolveAsync(TestData.Request(target: "health-portal"));

        Assert.Equal(CredentialStatus.Error, result.Status);
    }

    private sealed class ThrowingStore : ICredentialStore
    {
        public string Kind => "throwing";

        public Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default) =>
            throw new StoreException("vault unreachable");
    }
}
