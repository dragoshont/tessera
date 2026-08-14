import Foundation
import XCTest
@testable import TesseraHostCore

private struct FixedSigner: HostRequestSigner {
    func signCanonicalRequest(_ canonicalRequest: Data) throws -> Data { Data(repeating: 0x11, count: 64) }
}

final class HostProtocolTests: XCTestCase {
    func testCanonicalPollVectorMatchesDotNetContract() throws {
        let canonical = HostProtocol.canonicalSigningInput(
            operation: .poll,
            targetId: "-",
            hostId: "host-main",
            messageId: "msg-1",
            sequence: 1,
            timestamp: 1_723_600_000,
            bodySha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        )
        XCTAssertEqual(
            String(decoding: canonical, as: UTF8.self),
            "TESSERA-HOST-V1\nPOST\npoll\n-\nhost-main\n1\n1\nmsg-1\n1\n1723600000\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        )
        XCTAssertEqual(HostProtocol.sha256Hex(canonical), "c835a50cb077766cbf71fe3f25638d100a6ed02083a583770747c37edc1144a1")
    }

    func testCanonicalLeaseVectorMatchesDotNetContract() throws {
        let canonical = HostProtocol.canonicalSigningInput(
            operation: .leaseAck,
            targetId: "lease-123",
            hostId: "host-main",
            messageId: "msg-2",
            sequence: 42,
            timestamp: 1_723_600_123,
            bodySha256: "44136fa355b3678a1146ad16f7e8649e94fb4fc21fef6f3fc490a0fdd9f9b403"
        )
        XCTAssertEqual(HostProtocol.sha256Hex(canonical), "9c428c0eacdf045ced0b7f3393e47c107d6cb52f19ecb3c84b3001df626a0dac")
    }

    func testPreparedRequestBindsBodyAndHeaders() throws {
        let prepared = try HostProtocol.prepareSignedRequest(
            operation: .poll,
            targetId: "-",
            hostId: "host-main",
            messageId: "message-1",
            sequence: 1,
            timestamp: 1_723_600_000,
            body: try HostPollRequest(maxWaitSeconds: 25),
            signer: FixedSigner()
        )
        XCTAssertEqual(prepared.headers["X-Tessera-Host-Body-SHA256"], HostProtocol.sha256Hex(prepared.body))
        XCTAssertEqual(prepared.headers["X-Tessera-Host-Signature"], HostProtocol.base64URL(Data(repeating: 0x11, count: 64)))
        XCTAssertEqual(prepared.requestHash, HostProtocol.sha256Hex(prepared.canonicalRequest))
    }

    func testClosedIdentifiersAndBoundsFail() throws {
        XCTAssertThrowsError(try HostProtocol.validateIdentifier("../repo", name: "resourceId"))
        XCTAssertThrowsError(try HostProtocol.validateIdentifier("UPPER", name: "hostId"))
        XCTAssertThrowsError(try HostPollRequest(maxWaitSeconds: 26))
    }

    func testProofCommandRejectsGenericAuthority() throws {
        let command = HostCommand(
            commandId: "cmd:lease-123",
            leaseId: "lease-123",
            leaseVersion: 1,
            runId: "run-123",
            schedulerFence: 1,
            profileId: "host.shell@1",
            capabilityId: "host.shell",
            capabilityVersion: "1",
            capabilityGrantVersion: 1,
            resources: [],
            inputHash: String(repeating: "a", count: 64),
            issuedAt: "2026-08-14T00:00:00Z",
            executeUntil: "2026-08-14T00:05:00Z",
            input: .init(resourceIds: []),
            outputLimitBytes: 32 * 1024,
            eventLimit: 50
        )
        XCTAssertThrowsError(try command.validateProofProfile())
    }

    func testPairingCodeMatchesRfc7638BasePointVector() throws {
        let jwk = P256PublicJWK(
            x: "axfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpY",
            y: "T-NC4v4af5uO5-tKfA-eFivOM1drMV7Oy7ZAaDe_UfU"
        )
        XCTAssertEqual(try HostProtocol.pairingConfirmationCode(pairingId: "pairing-1", publicJWK: jwk), "761405")
        let secret = HostProtocol.base64URL(Data(repeating: 1, count: 32))
        XCTAssertEqual(try HostProtocol.decodeCanonicalBase64URL(secret, expectedBytes: 32), Data(repeating: 1, count: 32))
        XCTAssertThrowsError(try HostProtocol.decodeCanonicalBase64URL("\(secret)=", expectedBytes: 32))
    }
}