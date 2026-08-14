import Foundation
import XCTest
@testable import TesseraHostCore
@testable import TesseraHostMac

private final class MockURLProtocol: URLProtocol, @unchecked Sendable {
    static let lock = NSLock()
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let response = try Self.lock.withLock { try Self.handler!(request) }
            client?.urlProtocol(self, didReceive: response.0, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: response.1)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}

final class HostTransportTests: XCTestCase {
    func testServerDescriptorRejectsUnsafeOrigins() throws {
        XCTAssertThrowsError(try HostServerDescriptor(baseURL: URL(string: "http://tessera.example.com")!))
        XCTAssertThrowsError(try HostServerDescriptor(baseURL: URL(string: "https://user@tessera.example.com")!))
        XCTAssertThrowsError(try HostServerDescriptor(baseURL: URL(string: "https://127.0.0.1")!))
        XCTAssertNoThrow(try HostServerDescriptor(baseURL: URL(string: "https://tessera.example.com")!))
    }

    func testHTTPSExecutorSendsSignedHeadersWithoutCookiesOrCompression() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MockURLProtocol.self]
        MockURLProtocol.lock.withLock {
            MockURLProtocol.handler = { request in
                XCTAssertEqual(request.url?.absoluteString, "https://tessera.example.com/host-channel/poll")
                XCTAssertEqual(request.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
                XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
                XCTAssertEqual(request.value(forHTTPHeaderField: "X-Tessera-Host-Id"), "host-main")
                return (
                    HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!,
                    Data("{}".utf8)
                )
            }
        }
        let descriptor = try HostServerDescriptor(baseURL: URL(string: "https://tessera.example.com")!)
        let executor = URLSessionHostHTTPExecutor(descriptor: descriptor, configuration: configuration)
        let prepared = PreparedHostRequest(
            body: Data("{}".utf8),
            canonicalRequest: Data(),
            requestHash: String(repeating: "a", count: 64),
            headers: ["X-Tessera-Host-Id": "host-main"]
        )
        let response = try await executor.execute(path: "/host-channel/poll", request: prepared)
        XCTAssertEqual(response.statusCode, 200)
        XCTAssertTrue(response.envelopeConsumed)
    }

    func testHostTransportClassifiesOnlyBrokerJSONAsConsumed() async throws {
        let prepared = PreparedHostRequest(body: Data("{}".utf8), canonicalRequest: Data(), requestHash: String(repeating: "a", count: 64), headers: [:])
        let business = try await executeResponse(status: 409, headers: ["Content-Type": "application/json"], body: Data("{\"code\":\"host_lease_invalid\"}".utf8), prepared: prepared)
        XCTAssertTrue(business.envelopeConsumed)

        let problem = try await executeResponse(status: 409, headers: ["Content-Type": "application/problem+json"], body: Data("{\"code\":\"host_replay\"}".utf8), prepared: prepared)
        XCTAssertFalse(problem.envelopeConsumed)

        let proxy = try await executeResponse(status: 502, headers: ["Content-Type": "text/html"], body: Data("bad gateway".utf8), prepared: prepared)
        XCTAssertFalse(proxy.envelopeConsumed)

        do {
            _ = try await executeResponse(status: 200, headers: ["Content-Type": "text/html"], body: Data("not broker json".utf8), prepared: prepared)
            XCTFail("Expected invalid response")
        } catch HostChannelError.invalidResponse {}
    }

    func testHostTransportRejectsCompressionAndOversizeBeforeReturningBody() async throws {
        let prepared = PreparedHostRequest(body: Data("{}".utf8), canonicalRequest: Data(), requestHash: String(repeating: "a", count: 64), headers: [:])
        do {
            _ = try await executeResponse(
                status: 200,
                headers: ["Content-Type": "application/json", "Content-Encoding": "gzip"],
                body: Data("{}".utf8),
                prepared: prepared
            )
            XCTFail("Expected compressed response rejection")
        } catch HostChannelError.invalidResponse {}

        do {
            _ = try await executeResponse(
                status: 200,
                headers: ["Content-Type": "application/json", "Content-Length": "1000"],
                body: Data("{}".utf8),
                prepared: prepared,
                maximumBytes: 16
            )
            XCTFail("Expected oversized response rejection")
        } catch HostChannelError.responseTooLarge {}
    }

    func testFileJournalPersistsModeAndRejectsSymlink() async throws {
        let base = URL(fileURLWithPath: String(cString: realpath(FileManager.default.temporaryDirectory.path, nil)!))
            .appendingPathComponent("tessera-journal-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: base) }
        let file = base.appendingPathComponent("state.json")
        let journal = FileHostSessionJournal(fileURL: file)
        let state = HostSessionState(lastAcceptedSequence: 9)
        try await journal.save(state)
        let reloaded = try await journal.load()
        XCTAssertEqual(reloaded, state)
        let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)

        try FileManager.default.removeItem(at: file)
        try FileManager.default.createSymbolicLink(atPath: file.path, withDestinationPath: "/dev/null")
        do {
            _ = try await journal.load()
            XCTFail("Expected invalid symlink journal")
        } catch HostChannelError.invalidJournal {}
    }

    private func executeResponse(
        status: Int,
        headers: [String: String],
        body: Data,
        prepared: PreparedHostRequest,
        maximumBytes: Int = 2 * 1024 * 1024
    ) async throws -> HostHTTPResponse {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MockURLProtocol.self]
        MockURLProtocol.lock.withLock {
            MockURLProtocol.handler = { request in
                (HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: headers)!, body)
            }
        }
        let descriptor = try HostServerDescriptor(baseURL: URL(string: "https://tessera.example.com")!)
        return try await URLSessionHostHTTPExecutor(
            descriptor: descriptor,
            configuration: configuration,
            maximumResponseBytes: maximumBytes
        ).execute(path: "/host-channel/poll", request: prepared)
    }
}