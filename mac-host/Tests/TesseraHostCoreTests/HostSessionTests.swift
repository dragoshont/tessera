import Foundation
import XCTest
@testable import TesseraHostCore

private actor MemoryJournal: HostSessionJournal {
    var state = HostSessionState()
    func load() async throws -> HostSessionState { state }
    func save(_ state: HostSessionState) async throws { self.state = state }
}

private actor RecordingExecutor: HostHTTPExecutor {
    enum Failure: Error { case network }
    var requests: [(String, PreparedHostRequest)] = []
    var failFirst = true

    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        requests.append((path, request))
        if failFirst { failFirst = false; throw Failure.network }
        return HostHTTPResponse(statusCode: 200, body: Data("{\"version\":2}".utf8), envelopeConsumed: true)
    }
}

private actor UnavailableThenVersionExecutor: HostHTTPExecutor {
    var requests: [(String, PreparedHostRequest)] = []

    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        requests.append((path, request))
        if requests.count == 1 {
            return HostHTTPResponse(statusCode: 503, body: Data("{\"code\":\"product_storage_unavailable\"}".utf8), envelopeConsumed: false)
        }
        return HostHTTPResponse(statusCode: 200, body: Data("{\"version\":2}".utf8), envelopeConsumed: true)
    }
}

private struct VersionResponse: Codable, Equatable, Sendable { let version: Int64 }
private struct SessionSigner: HostRequestSigner { func signCanonicalRequest(_ canonicalRequest: Data) throws -> Data { Data(repeating: 7, count: 64) } }

final class HostSessionTests: XCTestCase {
    func testNetworkFailurePersistsExactMessageForRestartRetry() async throws {
        let journal = MemoryJournal()
        let executor = RecordingExecutor()
        let channel = try ReliableHostChannel(
            hostId: "host-main",
            signer: SessionSigner(),
            journal: journal,
            executor: executor,
            now: { Date(timeIntervalSince1970: 1_723_600_000) },
            makeMessageId: { "message-retry" }
        )
        do {
            _ = try await channel.send(
                operation: .leaseAck,
                targetId: "lease-123",
                body: HostLeaseAckRequest(leaseVersion: 1, localAttemptId: "attempt-1", accepted: true, rejectionCode: nil),
                response: VersionResponse.self
            )
            XCTFail("Expected network failure")
        } catch RecordingExecutor.Failure.network {}

        let failedState = try await journal.load()
        let pending = try XCTUnwrap(failedState.pendingMessage)
        XCTAssertEqual(pending.messageId, "message-retry")
        XCTAssertEqual(pending.sequence, 1)
        let restarted = try ReliableHostChannel(hostId: "host-main", signer: SessionSigner(), journal: journal, executor: executor)
        let result: VersionResponse = try await restarted.retryPending(response: VersionResponse.self)
        XCTAssertEqual(result.version, 2)
        let requests = await executor.requests
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests[0].0, requests[1].0)
        XCTAssertEqual(requests[0].1.body, requests[1].1.body)
        XCTAssertEqual(requests[0].1.headers["X-Tessera-Host-Message-Id"], requests[1].1.headers["X-Tessera-Host-Message-Id"])
        XCTAssertEqual(requests[0].1.headers["X-Tessera-Host-Sequence"], requests[1].1.headers["X-Tessera-Host-Sequence"])
        let committedState = try await journal.load()
        XCTAssertNil(committedState.pendingMessage)
        XCTAssertEqual(committedState.lastAcceptedSequence, 1)
    }

    func testUnconsumedProblemDoesNotAdvanceSequence() async throws {
        let journal = MemoryJournal()
        let executor = ProblemExecutor()
        let channel = try ReliableHostChannel(hostId: "host-main", signer: SessionSigner(), journal: journal, executor: executor)
        do {
            _ = try await channel.send(operation: .poll, targetId: "-", body: try HostPollRequest(maxWaitSeconds: 1), response: HostPollResponse.self)
            XCTFail("Expected unconsumed response")
        } catch HostChannelError.unconsumedResponse(409, "host_revoked") {}
        let state = try await journal.load()
        XCTAssertEqual(state.lastAcceptedSequence, 0)
        XCTAssertNotNil(state.pendingMessage)
    }

    func testTransientUnavailableRetriesExactPendingEnvelope() async throws {
        let journal = MemoryJournal()
        let executor = UnavailableThenVersionExecutor()
        let channel = try ReliableHostChannel(
            hostId: "host-main",
            signer: SessionSigner(),
            journal: journal,
            executor: executor,
            now: { Date(timeIntervalSince1970: 1_723_600_000) },
            makeMessageId: { "message-unavailable" }
        )
        do {
            _ = try await channel.send(
                operation: .leaseAck,
                targetId: "lease-123",
                body: HostLeaseAckRequest(leaseVersion: 1, localAttemptId: "attempt-1", accepted: true, rejectionCode: nil),
                response: VersionResponse.self
            )
            XCTFail("Expected transient unavailability")
        } catch HostChannelError.unconsumedResponse(503, "product_storage_unavailable") {}

        XCTAssertFalse(HostChannelError.requiresOperatorForUnconsumedProblem("product_storage_unavailable"))
        let result: VersionResponse = try await channel.retryPending(response: VersionResponse.self)
        XCTAssertEqual(result.version, 2)
        let requests = await executor.requests
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests[0].0, requests[1].0)
        XCTAssertEqual(requests[0].1.body, requests[1].1.body)
        XCTAssertEqual(requests[0].1.headers["X-Tessera-Host-Message-Id"], requests[1].1.headers["X-Tessera-Host-Message-Id"])
        XCTAssertEqual(requests[0].1.headers["X-Tessera-Host-Sequence"], requests[1].1.headers["X-Tessera-Host-Sequence"])
    }

    func testStableSignedFailuresRequireOperatorAction() {
        for code in ["host_auth_invalid", "host_revoked", "host_replay", "host_sequence_invalid", "host_protocol_unsupported"] {
            XCTAssertTrue(HostChannelError.requiresOperatorForUnconsumedProblem(code), code)
        }
        XCTAssertFalse(HostChannelError.requiresOperatorForUnconsumedProblem("host_clock_skew"))
        XCTAssertTrue(HostChannelError.canRefreshTimestampAfterUnconsumedProblem("host_clock_skew"))
        XCTAssertFalse(HostChannelError.canRefreshTimestampAfterUnconsumedProblem("host_revoked"))
        XCTAssertFalse(HostChannelError.requiresOperatorForUnconsumedProblem(nil))
    }

    func testBackoffIsBounded() throws {
        let backoff = try ExponentialBackoff(baseMilliseconds: 500, maximumMilliseconds: 30_000)
        XCTAssertEqual(backoff.delayMilliseconds(attempt: 0, jitterUnit: 0.5), 500)
        XCTAssertEqual(backoff.delayMilliseconds(attempt: 20, jitterUnit: 0.5), 30_000)
    }

    func testMalformedSuccessRetainsExactPendingEnvelopeWithoutAdvancing() async throws {
        let journal = MemoryJournal()
        let channel = try ReliableHostChannel(
            hostId: "host-main",
            signer: SessionSigner(),
            journal: journal,
            executor: MalformedConsumedExecutor(),
            now: { Date(timeIntervalSince1970: 1_723_600_000) },
            makeMessageId: { "message-malformed" }
        )
        do {
            _ = try await channel.send(operation: .poll, targetId: "-", body: try HostPollRequest(maxWaitSeconds: 1), response: HostPollResponse.self)
            XCTFail("Expected invalid response")
        } catch HostChannelError.invalidResponse {}
        let state = try await journal.load()
        XCTAssertEqual(state.lastAcceptedSequence, 0)
        XCTAssertEqual(state.pendingMessage?.messageId, "message-malformed")
        do {
            _ = try await channel.send(operation: .poll, targetId: "-", body: try HostPollRequest(maxWaitSeconds: 1), response: HostPollResponse.self)
            XCTFail("Expected exact pending retry requirement")
        } catch HostChannelError.pendingMessageExists {}
    }
}

private struct ProblemExecutor: HostHTTPExecutor {
    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        HostHTTPResponse(statusCode: 409, body: Data("{\"code\":\"host_revoked\"}".utf8), envelopeConsumed: false)
    }
}

private struct MalformedConsumedExecutor: HostHTTPExecutor {
    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        HostHTTPResponse(statusCode: 200, body: Data("not-json".utf8), envelopeConsumed: true)
    }
}