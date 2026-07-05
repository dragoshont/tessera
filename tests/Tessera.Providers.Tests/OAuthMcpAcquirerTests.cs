using Tessera.Core.Egress;
using Tessera.Core.Stores;
using Tessera.Providers.OAuthMcp;
using Xunit;

namespace Tessera.Providers.Tests;

/// <summary>
/// The OAuth-MCP acquirer (ADR 0027, spec P3): the <c>authorization_code</c> and
/// <c>refresh_token</c> legs of the token endpoint, exercised fully offline against a
/// <see cref="FakeTransport"/> mock AS. Asserts the grant shape, the SSRF pre-check,
/// per-principal write-back, RFC 6749 §6 refresh-token preservation, and the dead-grant
/// (report, never auto-login) path.
/// </summary>
public sealed class OAuthMcpAcquirerTests
{
    private static readonly Uri Token = new("https://as.example.com/token");
    private static readonly Uri Redirect = new("https://tessera.example.com/oauth/callback");
    private const string Resource = "https://mob.example.com/mcp";
    private const string Client = "tessera-broker";
    private const string Secret = "mob-alice";

    // The token endpoint host must be on the same allow-list the data egress uses.
    private static SsrfGuard Guard() => new(["as.example.com"]);

    // ── authorization_code ────────────────────────────────────────────────────

    [Fact]
    public async Task Acquire_writes_the_issued_bundle_per_principal()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.AcquireAsync(Token, Client, Redirect, "auth-code-abc", "ver-123", Resource, Secret);

        Assert.Equal(OAuthAcquireStatus.Acquired, result.Status);
        Assert.Equal(Secret, writer.LastName);
        Assert.Equal("AT", writer.LastBundle!.AccessToken);
        Assert.Equal("RT", writer.LastBundle!.RefreshToken);
    }

    [Fact]
    public async Task Acquire_sends_the_authorization_code_grant_with_pkce_verifier_and_resource()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"AT\"}");
        var acquirer = new OAuthMcpAcquirer(transport, new CapturingWriter(), Guard());

        await acquirer.AcquireAsync(Token, Client, Redirect, "auth-code-abc", "ver-123", Resource, Secret);

        Assert.Equal("POST", transport.LastMethod);
        Assert.Equal(Token.ToString(), transport.LastUrl);
        Assert.Equal("application/x-www-form-urlencoded", transport.LastHeaders!["Content-Type"]);
        var body = transport.LastBody!;
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains("code=auth-code-abc", body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=ver-123", body, StringComparison.Ordinal);
        Assert.Contains("resource=https%3A%2F%2Fmob.example.com%2Fmcp", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acquire_off_allow_list_token_endpoint_is_refused_before_any_request()
    {
        var transport = new FakeTransport();
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.AcquireAsync(
            new Uri("https://evil.example.com/token"), Client, Redirect, "c", "v", Resource, Secret);

        Assert.Equal(OAuthAcquireStatus.Error, result.Status);
        Assert.Equal(0, transport.Calls);      // never left the process
        Assert.Null(writer.LastBundle);         // never wrote
    }

    [Fact]
    public async Task Acquire_non_2xx_is_an_error_and_writes_nothing()
    {
        var transport = new FakeTransport(500, "boom");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.AcquireAsync(Token, Client, Redirect, "c", "v", Resource, Secret);

        Assert.Equal(OAuthAcquireStatus.Error, result.Status);
        Assert.Null(writer.LastBundle);
    }

    [Fact]
    public async Task Acquire_2xx_without_an_access_token_is_an_error()
    {
        var transport = new FakeTransport(200, "{\"token_type\":\"Bearer\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.AcquireAsync(Token, Client, Redirect, "c", "v", Resource, Secret);

        Assert.Equal(OAuthAcquireStatus.Error, result.Status);
        Assert.Null(writer.LastBundle);
    }

    // ── refresh_token ─────────────────────────────────────────────────────────

    private static CredentialBundle Current() => new(AccessToken: "OLD_AT", RefreshToken: "OLD_RT");

    [Fact]
    public async Task Refresh_rotates_and_writes_new_tokens()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\",\"refresh_token\":\"NEW_RT\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.RefreshAsync(Token, Client, Resource, Secret, Current());

        Assert.Equal(OAuthAcquireStatus.Acquired, result.Status);
        Assert.Equal("NEW_AT", writer.LastBundle!.AccessToken);
        Assert.Equal("NEW_RT", writer.LastBundle!.RefreshToken);
        Assert.Contains("grant_type=refresh_token", transport.LastBody!, StringComparison.Ordinal);
        Assert.Contains("refresh_token=OLD_RT", transport.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_preserves_the_current_refresh_token_when_the_as_omits_a_new_one()
    {
        // RFC 6749 §6: a non-rotating AS returns only a new access_token.
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.RefreshAsync(Token, Client, Resource, Secret, Current());

        Assert.Equal(OAuthAcquireStatus.Acquired, result.Status);
        Assert.Equal("NEW_AT", writer.LastBundle!.AccessToken);
        Assert.Equal("OLD_RT", writer.LastBundle!.RefreshToken);   // preserved, not blanked
    }

    [Fact]
    public async Task Refresh_without_a_current_refresh_token_is_dead_and_makes_no_call()
    {
        var transport = new FakeTransport();
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.RefreshAsync(Token, Client, Resource, Secret, new CredentialBundle(AccessToken: "AT"));

        Assert.Equal(OAuthAcquireStatus.Dead, result.Status);
        Assert.Equal(0, transport.Calls);
        Assert.Null(writer.LastBundle);
    }

    [Fact]
    public async Task Refresh_invalid_grant_is_dead_not_error()
    {
        // A revoked/expired refresh token: reported dead (a human re-authorizes), never a login.
        var transport = new FakeTransport(400, "{\"error\":\"invalid_grant\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        var result = await acquirer.RefreshAsync(Token, Client, Resource, Secret, Current());

        Assert.Equal(OAuthAcquireStatus.Dead, result.Status);
        Assert.Null(writer.LastBundle);
    }

    // ── stamped refresh context (W1) ──────────────────────────────────────────

    [Fact]
    public async Task Acquire_stamps_the_refresh_context_into_extra()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        await acquirer.AcquireAsync(Token, Client, Redirect, "code", "ver", Resource, Secret);

        var extra = writer.LastBundle!.Extra!;
        Assert.Equal(Token.ToString(), extra[OAuthMcpAcquirer.ExtraTokenEndpoint]);
        Assert.Equal(Client, extra[OAuthMcpAcquirer.ExtraClientId]);
        Assert.Equal(Resource, extra[OAuthMcpAcquirer.ExtraResource]);
    }

    [Fact]
    public async Task RefreshStored_rotates_using_the_stamped_context_without_rediscovery()
    {
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\",\"refresh_token\":\"NEW_RT\"}");
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());
        var stored = new CredentialBundle(
            AccessToken: "OLD_AT",
            RefreshToken: "OLD_RT",
            Extra: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OAuthMcpAcquirer.ExtraTokenEndpoint] = Token.ToString(),
                [OAuthMcpAcquirer.ExtraClientId] = Client,
                [OAuthMcpAcquirer.ExtraResource] = Resource,
            });

        var result = await acquirer.RefreshStoredAsync(Secret, stored);

        Assert.Equal(OAuthAcquireStatus.Acquired, result.Status);
        Assert.Equal(Token.ToString(), transport.LastUrl);   // the endpoint from Extra, no re-discovery
        Assert.Contains("grant_type=refresh_token", transport.LastBody!, StringComparison.Ordinal);
        Assert.Contains("refresh_token=OLD_RT", transport.LastBody!, StringComparison.Ordinal);
        Assert.Equal("NEW_AT", writer.LastBundle!.AccessToken);
        // the refresh context survives the rotation so the NEXT refresh is still self-contained
        Assert.Equal(Token.ToString(), writer.LastBundle!.Extra![OAuthMcpAcquirer.ExtraTokenEndpoint]);
    }

    [Fact]
    public async Task RefreshStored_without_the_context_is_an_error_and_makes_no_call()
    {
        var transport = new FakeTransport();
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, Guard());

        // a bundle with a refresh token but no stamped OAuth context (e.g. a harvest bundle)
        var result = await acquirer.RefreshStoredAsync(Secret, new CredentialBundle(RefreshToken: "RT"));

        Assert.Equal(OAuthAcquireStatus.Error, result.Status);
        Assert.Equal(0, transport.Calls);
        Assert.Null(writer.LastBundle);
    }

    private sealed class CapturingWriter : ICredentialWriter
    {
        public string? LastName { get; private set; }
        public CredentialBundle? LastBundle { get; private set; }

        public Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
        {
            LastName = name;
            LastBundle = bundle;
            return Task.CompletedTask;
        }
    }
}
