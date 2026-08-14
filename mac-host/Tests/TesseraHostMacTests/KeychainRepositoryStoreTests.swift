import Foundation
import XCTest
@testable import TesseraHostCore
@testable import TesseraHostMac

final class KeychainRepositoryStoreTests: XCTestCase {
    private var store: KeychainRepositoryStore!
    private var service: String!
    private let reader = DescriptorRepositoryReader()
    private var directory: URL!

    override func setUpWithError() throws {
        service = "ro.hont.tessera.tests.repositories.\(UUID().uuidString.lowercased())"
        store = KeychainRepositoryStore(service: service)
        let base = try XCTUnwrap(realpath(FileManager.default.temporaryDirectory.path, nil))
        defer { free(base) }
        directory = URL(fileURLWithPath: String(cString: base), isDirectory: true)
            .appendingPathComponent("tessera-store-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory.appendingPathComponent(".git/refs/heads"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: directory.appendingPathComponent(".git/objects"), withIntermediateDirectories: true)
        try Data("ref: refs/heads/main\n".utf8).write(to: directory.appendingPathComponent(".git/HEAD"))
        try Data("\(String(repeating: "a", count: 40))\n".utf8).write(to: directory.appendingPathComponent(".git/refs/heads/main"))
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: directory)
        try? await store.delete(resourceId: "repo-main")
    }

    func testStoreKeepsPathInKeychainAndProviderChecksFingerprint() async throws {
        let record = try reader.grant(path: directory.path, resourceId: "repo-main", displayName: "Tessera")
        try await store.save(record)
        let reloaded = try await store.load(resourceId: "repo-main")
        XCTAssertEqual(reloaded, record)
        XCTAssertNil(UserDefaults.standard.string(forKey: "repo-main"))

        let provider = StoredRepositoryIdentityProvider(store: store, reader: reader)
        let valid = HostLeaseResource(resourceId: "repo-main", resourceGrantVersion: 1, accessMode: "READ_ONLY", fingerprint: record.fingerprint)
        let identity = try await provider.identity(for: valid)
        XCTAssertEqual(identity.commit, String(repeating: "a", count: 40))
        let changed = HostLeaseResource(resourceId: "repo-main", resourceGrantVersion: 1, accessMode: "READ_ONLY", fingerprint: String(repeating: "b", count: 64))
        do {
            _ = try await provider.identity(for: changed)
            XCTFail("Expected fingerprint mismatch")
        } catch RepositoryStoreError.fingerprintMismatch {}
    }

}