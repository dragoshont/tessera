import Foundation
import XCTest
@testable import TesseraHostCore

final class HostOutputNormalizerTests: XCTestCase {
    func testNormalizesRedactsTruncatesAndHashesPersistedBytes() throws {
        let input = Data("line1\r\nAuthorization: Bearer canary-token\u{0}\rPATH=/Users/alice/repo\n-----BEGIN " .utf8) + Data("PRIVATE KEY-----abc".utf8)
        let normalized = try HostOutputNormalizer.normalize(input, limitBytes: 80)
        XCTAssertFalse(normalized.text.contains("canary-token"))
        XCTAssertFalse(normalized.text.contains("/Users/"))
        XCTAssertFalse(normalized.text.contains("PRIVATE KEY"))
        XCTAssertFalse(normalized.text.unicodeScalars.contains("\u{0}"))
        XCTAssertTrue(normalized.redacted)
        XCTAssertLessThanOrEqual(normalized.sizeBytes, 80)
        XCTAssertEqual(normalized.sha256, HostProtocol.sha256Hex(Data(normalized.text.utf8)))
    }

    func testTruncationKeepsUTF8Boundary() throws {
        let normalized = try HostOutputNormalizer.normalize(Data("ééé".utf8), limitBytes: 5)
        XCTAssertEqual(normalized.text, "éé")
        XCTAssertTrue(normalized.truncated)
        XCTAssertEqual(normalized.sizeBytes, 4)
    }

    func testRejectsInvalidUTF8() {
        XCTAssertThrowsError(try HostOutputNormalizer.normalize(Data([0xff]), limitBytes: 10))
    }

    func testRepositoryIdentityBranchesThatContainSensitiveWordsRemainIntact() throws {
        for branch in ["env", "feature/env-vars", "fix/path", "token-auth", "secret-rotation", "command-palette"] {
            let identity = RepositoryIdentity(
                branch: branch,
                commit: String(repeating: "a", count: 40),
                resourceFingerprint: String(repeating: "b", count: 64)
            )
            let input = try HostProtocol.canonicalJSONEncoder().encode(identity)
            let normalized = try HostOutputNormalizer.normalize(input, limitBytes: 32 * 1024)
            XCTAssertEqual(normalized.text, String(decoding: input, as: UTF8.self), branch)
            XCTAssertFalse(normalized.redacted, branch)
        }
    }

    func testOnlyAssignmentKeyControlsSensitiveRedaction() throws {
        let normalized = try HostOutputNormalizer.normalize(
            Data("note=token-auth\ntoken=canary".utf8),
            limitBytes: 1024
        )
        XCTAssertEqual(normalized.text, "note=token-auth\ntoken= [REDACTED]")
        XCTAssertTrue(normalized.redacted)
    }
}