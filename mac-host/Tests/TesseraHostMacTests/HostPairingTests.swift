import Foundation
import XCTest
@testable import TesseraHostCore
@testable import TesseraHostMac

private actor PairingRecorder: HostPairingClaiming {
    var pairingId: String?
    var idempotencyKey: String?
    var request: HostPairingClaimRequest?

    func claim(pairingId: String, idempotencyKey: String, request: HostPairingClaimRequest) async throws -> HostPairingClaimResponse {
        self.pairingId = pairingId
        self.idempotencyKey = idempotencyKey
        self.request = request
        return HostPairingClaimResponse(pairingId: pairingId, state: "CLAIMED", expiresAt: "2026-08-14T12:05:00Z", version: 2)
    }

    func snapshot() -> (String?, String?, HostPairingClaimRequest?) { (pairingId, idempotencyKey, request) }
}

private actor FailOncePairingRecorder: HostPairingClaiming {
    enum Failure: Error { case responseLost }
    private var shouldFail = true
    private var requests: [(String, HostPairingClaimRequest)] = []

    func claim(pairingId: String, idempotencyKey: String, request: HostPairingClaimRequest) async throws -> HostPairingClaimResponse {
        requests.append((idempotencyKey, request))
        if shouldFail {
            shouldFail = false
            throw Failure.responseLost
        }
        return HostPairingClaimResponse(pairingId: pairingId, state: "CLAIMED", expiresAt: "2026-08-14T12:05:00Z", version: 2)
    }

    func snapshot() -> [(String, HostPairingClaimRequest)] { requests }
}

private struct MismatchedPairingClient: HostPairingClaiming {
    func claim(pairingId: String, idempotencyKey: String, request: HostPairingClaimRequest) async throws -> HostPairingClaimResponse {
        HostPairingClaimResponse(pairingId: "pairing-other", state: "CLAIMED", expiresAt: "2026-08-14T12:05:00Z", version: 2)
    }
}

private final class PairingURLProtocol: URLProtocol, @unchecked Sendable {
    static let lock = NSLock()
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let pair = try Self.lock.withLock { try Self.handler!(request) }
            client?.urlProtocol(self, didReceive: pair.0, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: pair.1)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}

final class HostPairingTests: XCTestCase {
    private var directory: URL!
    private var keyStore: SecurityDeviceKeyStore!
    private var resourceStore: KeychainRepositoryStore!
    private var configurationStore: HostConfigurationStore!
    private var attemptStore: HostPairingAttemptStore!

    override func setUpWithError() throws {
        let suffix = UUID().uuidString.lowercased()
        keyStore = SecurityDeviceKeyStore(tag: "ro.hont.tessera.tests.pairing-key.\(suffix)")
        resourceStore = KeychainRepositoryStore(service: "ro.hont.tessera.tests.pairing-resource.\(suffix)")
        configurationStore = HostConfigurationStore(service: "ro.hont.tessera.tests.configuration.\(suffix)")
        attemptStore = HostPairingAttemptStore(service: "ro.hont.tessera.tests.pairing-attempt.\(suffix)")
        let base = try XCTUnwrap(realpath(FileManager.default.temporaryDirectory.path, nil))
        defer { free(base) }
        directory = URL(fileURLWithPath: String(cString: base), isDirectory: true).appendingPathComponent("tessera-pairing-\(suffix)")
        try FileManager.default.createDirectory(at: directory.appendingPathComponent(".git/refs/heads"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: directory.appendingPathComponent(".git/objects"), withIntermediateDirectories: true)
        try Data("ref: refs/heads/main\n".utf8).write(to: directory.appendingPathComponent(".git/HEAD"))
        try Data("\(String(repeating: "a", count: 40))\n".utf8).write(to: directory.appendingPathComponent(".git/refs/heads/main"))
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: directory)
        try? await keyStore.delete()
        try? await resourceStore.delete(resourceId: "repo-main")
        try? await configurationStore.delete()
        try? await attemptStore.delete()
    }

    func testCoordinatorClaimsOnlyFixedCapabilityAndOpaqueResource() async throws {
        let record = try DescriptorRepositoryReader().grant(path: directory.path, resourceId: "repo-main", displayName: "Tessera")
        try await resourceStore.save(record)
        let recorder = PairingRecorder()
        let coordinator = HostPairingCoordinator(
            keys: keyStore,
            resources: resourceStore,
            attempts: attemptStore,
            keyPolicy: .keychainOnly,
            makeClient: { _ in recorder }
        )
        let secret = HostProtocol.base64URL(Data(repeating: 2, count: 32))
        let result = try await coordinator.claim(input: .init(
            serverURL: "https://tessera.example.com",
            pairingId: "pairing-1",
            claimSecret: secret,
            resourceId: "repo-main"
        ))
        XCTAssertEqual(result.state, "CLAIMED")
        XCTAssertEqual(result.resourceFingerprint, record.fingerprint)
        XCTAssertEqual(result.confirmationCode.count, 6)
        let snapshot = await recorder.snapshot()
        XCTAssertEqual(snapshot.0, "pairing-1")
        let idempotencyKey = try XCTUnwrap(snapshot.1)
        XCTAssertTrue(idempotencyKey.hasPrefix("claim-"))
        let request = try XCTUnwrap(snapshot.2)
        XCTAssertEqual(request.claimSecret, secret)
        XCTAssertEqual(request.protection, .keychainThisDeviceOnly)
        XCTAssertEqual(request.requestedCapabilities.map(\.capabilityId), ["host.repo.identity"])
        XCTAssertEqual(request.requestedCapabilities.map(\.sideEffectClass), ["READ_ONLY"])
        XCTAssertEqual(request.requestedResources.map(\.resourceId), ["repo-main"])
        let body = String(decoding: try JSONEncoder().encode(request), as: UTF8.self)
        XCTAssertFalse(body.contains(directory.path))
        XCTAssertFalse(body.contains("private"))
    }

    func testRuntimeConfigurationIsKeychainOnlyAndStrict() async throws {
        let configuration = HostRuntimeConfiguration(serverURL: "https://tessera.example.com", hostId: "host-main")
        try await configurationStore.save(configuration)
        let loaded = try await configurationStore.load()
        XCTAssertEqual(loaded, configuration)
        XCTAssertNil(UserDefaults.standard.string(forKey: "host-main"))
        do {
            try await configurationStore.save(.init(serverURL: "http://tessera.example.com", hostId: "host-main"))
            XCTFail("Expected unsafe server rejection")
        } catch HostChannelError.invalidServerDescriptor {}
    }

    func testIdentityIsStableForRetryAndChangesOnlyWhenExplicitlyReplaced() async throws {
        let record = try DescriptorRepositoryReader().grant(path: directory.path, resourceId: "repo-main", displayName: "Tessera")
        try await resourceStore.save(record)
        let recorder = PairingRecorder()
        let coordinator = HostPairingCoordinator(
            keys: keyStore,
            resources: resourceStore,
            attempts: attemptStore,
            keyPolicy: .keychainOnly,
            makeClient: { _ in recorder }
        )
        let base = HostPairingInput(
            serverURL: "https://tessera.example.com",
            pairingId: "pairing-1",
            claimSecret: HostProtocol.base64URL(Data(repeating: 3, count: 32)),
            resourceId: "repo-main"
        )
        _ = try await coordinator.claim(input: base)
        let firstSnapshot = await recorder.snapshot()
        let first = try XCTUnwrap(firstSnapshot.2?.publicKeyJwk)
        let firstIdempotencyKey = firstSnapshot.1
        _ = try await coordinator.claim(input: base)
        let retrySnapshot = await recorder.snapshot()
        let retry = try XCTUnwrap(retrySnapshot.2?.publicKeyJwk)
        XCTAssertEqual(firstIdempotencyKey, retrySnapshot.1)
        XCTAssertEqual(first, retry)
        _ = try await coordinator.claim(input: .init(
            serverURL: base.serverURL,
            pairingId: base.pairingId,
            claimSecret: base.claimSecret,
            resourceId: base.resourceId,
            replaceIdentity: true
        ))
        let replacedSnapshot = await recorder.snapshot()
        let replaced = try XCTUnwrap(replacedSnapshot.2?.publicKeyJwk)
        XCTAssertNotEqual(first, replaced)
    }

    func testReplacementRetryReusesExactPersistedClaimAfterLostResponse() async throws {
        let record = try DescriptorRepositoryReader().grant(path: directory.path, resourceId: "repo-main", displayName: "Tessera")
        try await resourceStore.save(record)
        _ = try await keyStore.loadOrCreate(policy: .keychainOnly)
        let recorder = FailOncePairingRecorder()
        let coordinator = HostPairingCoordinator(
            keys: keyStore,
            resources: resourceStore,
            attempts: attemptStore,
            keyPolicy: .keychainOnly,
            makeClient: { _ in recorder }
        )
        let input = HostPairingInput(
            serverURL: "https://tessera.example.com",
            pairingId: "pairing-1",
            claimSecret: HostProtocol.base64URL(Data(repeating: 4, count: 32)),
            resourceId: "repo-main",
            replaceIdentity: true
        )

        do {
            _ = try await coordinator.claim(input: input)
            XCTFail("Expected a lost response")
        } catch FailOncePairingRecorder.Failure.responseLost {}
        _ = try await coordinator.claim(input: input)

        let requests = await recorder.snapshot()
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests[0].0, requests[1].0)
        XCTAssertEqual(requests[0].1, requests[1].1)
        let pending = try await attemptStore.load()
        XCTAssertNil(pending)
    }

    func testMismatchedClaimResponseRetainsPersistedAttempt() async throws {
        let record = try DescriptorRepositoryReader().grant(path: directory.path, resourceId: "repo-main", displayName: "Tessera")
        try await resourceStore.save(record)
        let coordinator = HostPairingCoordinator(
            keys: keyStore,
            resources: resourceStore,
            attempts: attemptStore,
            keyPolicy: .keychainOnly,
            makeClient: { _ in MismatchedPairingClient() }
        )
        let input = HostPairingInput(
            serverURL: "https://tessera.example.com",
            pairingId: "pairing-1",
            claimSecret: HostProtocol.base64URL(Data(repeating: 5, count: 32)),
            resourceId: "repo-main"
        )

        do {
            _ = try await coordinator.claim(input: input)
            XCTFail("Expected mismatched claim response rejection")
        } catch HostChannelError.invalidResponse {}

        let pending = try await attemptStore.load()
        XCTAssertEqual(pending?.pairingId, input.pairingId)
        XCTAssertEqual(pending?.request.claimSecret, input.claimSecret)
    }

    func testPairingClaimUsesNamedHTTPSOriginWithoutCookiesOrCompression() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [PairingURLProtocol.self]
        let privatePath = directory.path
        PairingURLProtocol.lock.withLock {
            PairingURLProtocol.handler = { request in
                XCTAssertEqual(request.url?.absoluteString, "https://tessera.example.com/api/v1/host-pairings/pairing-1/claim")
                XCTAssertEqual(request.httpMethod, "POST")
                XCTAssertEqual(request.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
                XCTAssertEqual(request.value(forHTTPHeaderField: "Idempotency-Key"), "claim-test")
                XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
                XCTAssertFalse(String(decoding: request.httpBody ?? Data(), as: UTF8.self).contains(privatePath))
                let response = HostPairingClaimResponse(pairingId: "pairing-1", state: "CLAIMED", expiresAt: "2026-08-14T12:05:00Z", version: 2)
                return (
                    HTTPURLResponse(url: request.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!,
                    try JSONEncoder().encode(response)
                )
            }
        }
        let client = HostPairingHTTPClient(
            descriptor: try HostServerDescriptor(baseURL: URL(string: "https://tessera.example.com")!),
            configuration: configuration
        )
        let request = HostPairingClaimRequest(
            claimSecret: HostProtocol.base64URL(Data(repeating: 1, count: 32)),
            publicKeyJwk: .init(x: String(repeating: "a", count: 43), y: String(repeating: "b", count: 43)),
            protection: .keychainThisDeviceOnly,
            platform: "macOS", architecture: "arm64", agentVersion: "1.0.0", protocolVersion: "1",
            requestedCapabilities: [], requestedResources: []
        )
        let response = try await client.claim(pairingId: "pairing-1", idempotencyKey: "claim-test", request: request)
        XCTAssertEqual(response.state, "CLAIMED")
    }
}