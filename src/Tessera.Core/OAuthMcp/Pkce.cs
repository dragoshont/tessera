using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Tessera.Core.OAuthMcp;

/// <summary>
/// A PKCE (RFC 7636) verifier/challenge pair for the authorization-code flow. Only the
/// <c>S256</c> method is emitted — OAuth 2.1 forbids <c>plain</c>, and S256 keeps the
/// verifier off the front channel: a network observer of the authorize redirect learns
/// only the SHA-256 challenge, never the verifier that redeems the code.
/// </summary>
/// <param name="Verifier">
/// The high-entropy <c>code_verifier</c>. Kept server-side and sent ONLY on the
/// back-channel token exchange, never on the authorize redirect. 43 characters drawn
/// from the RFC 7636 §4.1 unreserved set (base64url of 32 random bytes), so it never
/// needs URL-escaping.
/// </param>
/// <param name="Challenge">The <c>code_challenge</c> — BASE64URL(SHA-256(ASCII(verifier))) — sent on the authorize redirect.</param>
public sealed record PkcePair(string Verifier, string Challenge)
{
    /// <summary>The only challenge method emitted (RFC 7636 §4.2); OAuth 2.1 forbids <c>plain</c>.</summary>
    public const string Method = "S256";

    /// <summary>
    /// Generate a fresh pair from 32 cryptographically-random bytes: the verifier is their
    /// base64url encoding (43 chars, within the RFC 7636 43–128 range and drawn only from
    /// the unreserved set) and the challenge is its S256 hash. Each call is independent —
    /// a fresh verifier per authorization request, never reused.
    /// </summary>
    public static PkcePair Generate()
    {
        Span<byte> seed = stackalloc byte[32];
        RandomNumberGenerator.Fill(seed);
        var verifier = Base64Url.EncodeToString(seed);
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }
}
