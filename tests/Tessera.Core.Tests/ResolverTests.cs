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

    // ── Service-owned shared-key fallback (ADR 0020) ──────────────────────────

    [Fact]
    public void A_delegated_request_falls_back_to_a_service_owned_shared_key()
    {
        // Media key: principal = null, owner = service (the household key nobody holds).
        var serviceKey = new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service);
        var resolver = new CredentialResolver([serviceKey], new InMemoryCredentialStore());

        // bob, acting via chat, has no per-person seerr binding → uses the shared key.
        var binding = resolver.BindingFor(TestData.Request(target: "seerr", onBehalfOf: TestData.VerifiedUser("bob@example.com")));
        Assert.Same(serviceKey, binding);
    }

    [Fact]
    public void A_personal_binding_still_wins_over_the_shared_service_key()
    {
        var serviceKey = new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service);
        var alicesOwn = new TargetBinding("seerr", "seerr-alice", "alice@example.com", CredentialOwner.User);
        var resolver = new CredentialResolver([serviceKey, alicesOwn], new InMemoryCredentialStore());

        var binding = resolver.BindingFor(TestData.Request(target: "seerr", onBehalfOf: TestData.VerifiedUser("alice@example.com")));
        Assert.Same(alicesOwn, binding); // exact per-principal match wins
    }

    [Fact]
    public void Automation_does_not_use_the_fallback_path_it_matches_directly()
    {
        // A no-human (automation) request matches the principal-null binding directly
        // (step 1), so the fallback is irrelevant — and an automation request never
        // triggers the delegated fallback.
        var serviceKey = new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service);
        var resolver = new CredentialResolver([serviceKey], new InMemoryCredentialStore());

        Assert.Same(serviceKey, resolver.BindingFor(new AccessRequest(TestData.VerifiedCaller(), "seerr", "read:requests")));
    }

    [Fact]
    public void A_non_service_principal_null_binding_is_never_a_delegated_fallback()
    {
        // Only owner: service is a shared key. A principal-null binding explicitly
        // marked user/dependent is not a fallback for some other person.
        var notShared = new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.User);
        var resolver = new CredentialResolver([notShared], new InMemoryCredentialStore());

        Assert.Null(resolver.BindingFor(TestData.Request(target: "seerr", onBehalfOf: TestData.VerifiedUser("bob@example.com"))));
    }

    [Fact]
    public async Task Resolve_uses_the_shared_service_key_bundle_for_a_delegated_call()
    {
        var store = new InMemoryCredentialStore();
        store.Put("seerr-key", new CredentialBundle(AccessToken: "shared-key"));
        var resolver = new CredentialResolver(
            [new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service)],
            store);

        var result = await resolver.ResolveAsync(TestData.Request(target: "seerr", onBehalfOf: TestData.VerifiedUser("bob@example.com")));
        Assert.Equal(CredentialStatus.Present, result.Status);
    }

    private sealed class ThrowingStore : ICredentialStore
    {
        public string Kind => "throwing";

        public Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default) =>
            throw new StoreException("vault unreachable");
    }
}
