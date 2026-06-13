using Tessera.Core.Identity;
using Tessera.Core.Model;

namespace Tessera.Core.Tests;

/// <summary>Small builders so tests read like the scenarios they describe.</summary>
internal static class TestData
{
    public static CallerIdentity VerifiedCaller(string id = "spiffe://tessera.local/chatbot") =>
        new(id, VerificationMethod.SpiffeSvid, "tessera.local");

    public static CallerIdentity UnverifiedCaller(string id = "dev-caller") =>
        new(id, VerificationMethod.Dev);

    public static EndUserAssertion VerifiedUser(string subject = "alice@example.com") =>
        new(subject, "https://issuer.example.com/v2.0", VerificationMethod.OidcJwt, subject);

    public static EndUserAssertion UnverifiedUser(string subject = "mallory@example.com") =>
        new(subject, "n/a", VerificationMethod.Dev, subject);

    public static AccessRequest Request(
        CallerIdentity? caller = null,
        string target = "health-portal",
        string action = "read:results",
        EndUserAssertion? onBehalfOf = null) =>
        new(caller ?? VerifiedCaller(), target, action, onBehalfOf);
}
