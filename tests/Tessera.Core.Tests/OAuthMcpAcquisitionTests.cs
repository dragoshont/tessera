using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tessera.Core.OAuthMcp;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// PKCE (RFC 7636) + the authorization-code redirect URL (OAuth 2.1 + RFC 8707) — the pure,
/// I/O-free half of the OAuth-MCP acquisition (ADR 0027, spec P3).
/// </summary>
public sealed class OAuthMcpAcquisitionTests
{
    // ── PKCE ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Pkce_challenge_is_the_s256_of_the_verifier()
    {
        var pair = PkcePair.Generate();

        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)));
        Assert.Equal(expected, pair.Challenge);
        Assert.Equal("S256", PkcePair.Method);
    }

    [Fact]
    public void Pkce_verifier_is_43_chars_from_the_unreserved_set()
    {
        var pair = PkcePair.Generate();

        // base64url(32 bytes) = 43 chars, no padding, only the RFC 7636 unreserved subset.
        Assert.Equal(43, pair.Verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", pair.Verifier);
        Assert.DoesNotContain('=', pair.Verifier);
    }

    [Fact]
    public void Pkce_generates_a_fresh_verifier_each_call()
    {
        var a = PkcePair.Generate();
        var b = PkcePair.Generate();

        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.Challenge, b.Challenge);
    }

    // ── Authorize URL ─────────────────────────────────────────────────────────

    private static readonly PkcePair FixedPkce = new("verifier-xyz", "challenge-abc");

    [Fact]
    public void Authorize_url_carries_every_oauth_and_pkce_parameter()
    {
        var url = OAuthAuthorizeUrl.Build(
            new Uri("https://as.example.com/authorize"),
            clientId: "tessera-broker",
            redirectUri: new Uri("https://tessera.example.com/oauth/callback"),
            scopes: ["read", "write"],
            resource: "https://mob.example.com/mcp",
            state: "state-nonce-123",
            pkce: FixedPkce);

        var q = url.Query;
        Assert.Contains("response_type=code", q, StringComparison.Ordinal);
        Assert.Contains("client_id=tessera-broker", q, StringComparison.Ordinal);
        Assert.Contains("code_challenge=challenge-abc", q, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", q, StringComparison.Ordinal);
        Assert.Contains("state=state-nonce-123", q, StringComparison.Ordinal);
        // The verifier must NEVER appear on the front channel — only the challenge.
        Assert.DoesNotContain("verifier-xyz", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorize_url_space_joins_scopes_and_url_encodes_values()
    {
        var url = OAuthAuthorizeUrl.Build(
            new Uri("https://as.example.com/authorize"),
            clientId: "c",
            redirectUri: new Uri("https://tessera.example.com/cb"),
            scopes: ["read", "write"],
            resource: "https://mob.example.com/mcp",
            state: "s",
            pkce: FixedPkce);

        // OAuth space-delimited scope, URL-encoded → %20; the redirect_uri + resource encoded.
        Assert.Contains("scope=read%20write", url.Query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Ftessera.example.com%2Fcb", url.Query, StringComparison.Ordinal);
        Assert.Contains("resource=https%3A%2F%2Fmob.example.com%2Fmcp", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorize_url_appends_to_an_endpoint_that_already_has_a_query()
    {
        var url = OAuthAuthorizeUrl.Build(
            new Uri("https://as.example.com/authorize?tenant=acme"),
            clientId: "c",
            redirectUri: new Uri("https://tessera.example.com/cb"),
            scopes: ["read"],
            resource: "https://mob.example.com/mcp",
            state: "s",
            pkce: FixedPkce);

        Assert.Contains("tenant=acme", url.Query, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url.Query, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"tenant=acme&.*response_type=code"), url.Query);
    }

    [Fact]
    public void Authorize_url_rejects_a_blank_resource()
    {
        // RFC 8707 resource is the audience binding — refuse to build a request without it.
        Assert.Throws<ArgumentException>(() => OAuthAuthorizeUrl.Build(
            new Uri("https://as.example.com/authorize"),
            clientId: "c",
            redirectUri: new Uri("https://tessera.example.com/cb"),
            scopes: ["read"],
            resource: "  ",
            state: "s",
            pkce: FixedPkce));
    }
}
