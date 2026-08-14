import Foundation
import XCTest
@testable import TesseraHostCore

private actor AgentJournal: HostSessionJournal {
    var state = HostSessionState()
    func load() async throws -> HostSessionState { state }
    func save(_ state: HostSessionState) async throws { self.state = state }
}

private actor AgentExecutor: HostHTTPExecutor {
    enum Failure: Error { case network }
    var paths: [String] = []
    var bodies: [Data] = []
    private var version: Int64 = 1
    private let command: HostCommand
    private var failOnceAtPath: String?
    private let reconcileResolution: HostLeaseReconcileResolution
    private let reconcileLeaseId: String?

    init(
        command: HostCommand,
        failOnceAtPath: String? = nil,
        reconcileResolution: HostLeaseReconcileResolution = .resume,
        reconcileLeaseId: String? = nil
    ) {
        self.command = command
        self.failOnceAtPath = failOnceAtPath
        self.reconcileResolution = reconcileResolution
        self.reconcileLeaseId = reconcileLeaseId
    }

    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        paths.append(path)
        bodies.append(request.body)
        if path == failOnceAtPath {
            failOnceAtPath = nil
            throw Failure.network
        }
        if path == "/host-channel/poll" {
            let response = HostPollResponse(
                serverTime: "2026-08-14T12:00:00Z",
                nextPollAfterMs: 250,
                lease: nil,
                command: command
            )
            return .init(statusCode: 200, body: try HostProtocol.canonicalJSONEncoder().encode(response), envelopeConsumed: true)
        }
        if path.hasSuffix("/reconcile") {
            let state = switch reconcileResolution {
            case .resume: "RUNNING"
            case .requeued: "EXPIRED"
            case .reconciliationRequired: "RECONCILIATION_REQUIRED"
            }
            let lease = HostLeaseSnapshot(
                leaseId: reconcileLeaseId ?? command.leaseId,
                leaseVersion: version,
                runId: command.runId,
                state: state,
                localAttemptId: "attempt-1",
                resources: command.resources
            )
            let response = HostLeaseReconcileResponse(resolution: reconcileResolution, lease: lease)
            return .init(statusCode: 200, body: try JSONEncoder().encode(response), envelopeConsumed: true)
        }
        if path.hasSuffix("/ack") || path.hasSuffix("/events") {
            version += 1
            return .init(statusCode: 200, body: try JSONEncoder().encode(HostResourceVersion(version: version)), envelopeConsumed: true)
        }
        return .init(statusCode: path.hasSuffix("/artifacts") ? 201 : 200, body: Data("{}".utf8), envelopeConsumed: true)
    }

    func snapshot() -> (paths: [String], bodies: [Data]) { (paths, bodies) }
}

private actor AckRejectingExecutor: HostHTTPExecutor {
    private let command: HostCommand
    private(set) var paths: [String] = []
    private(set) var bodies: [Data] = []

    init(command: HostCommand) { self.command = command }

    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        paths.append(path)
        bodies.append(request.body)
        if path.hasSuffix("/ack") {
            return .init(statusCode: 409, body: Data("{\"code\":\"host_lease_expired\"}".utf8), envelopeConsumed: true)
        }
        let response = HostPollResponse(serverTime: "2026-08-14T12:00:00Z", nextPollAfterMs: 250, lease: nil, command: nil)
        return .init(statusCode: 200, body: try HostProtocol.canonicalJSONEncoder().encode(response), envelopeConsumed: true)
    }

    func snapshot() -> (paths: [String], bodies: [Data]) { (paths, bodies) }
}

private struct AgentSigner: HostRequestSigner {
    func signCanonicalRequest(_ canonicalRequest: Data) throws -> Data { Data(repeating: 9, count: 64) }
}

private actor AgentRepository: HostRepositoryIdentityProvider {
    var calls = 0
    func identity(for resource: HostLeaseResource) async throws -> RepositoryIdentity {
        calls += 1
        return RepositoryIdentity(branch: "main", commit: String(repeating: "a", count: 40), resourceFingerprint: resource.fingerprint)
    }
    func callCount() -> Int { calls }
}

private struct FailingRepository: HostRepositoryIdentityProvider {
    enum Failure: Error { case changed }
    func identity(for resource: HostLeaseResource) async throws -> RepositoryIdentity { throw Failure.changed }
}

private final class IdentifierSource: @unchecked Sendable {
    private let lock = NSLock()
    private var counter = 0
    func next(_ prefix: String) -> String {
        lock.withLock {
            counter += 1
            return "\(prefix)-\(counter)"
        }
    }
}

final class HostAgentTests: XCTestCase {
    private func command(executeUntil: String = "2026-08-14T12:05:00Z") throws -> HostCommand {
        let resource = HostLeaseResource(resourceId: "repo-main", resourceGrantVersion: 1, accessMode: "READ_ONLY", fingerprint: String(repeating: "b", count: 64))
        let input = HostCommandInput(resourceIds: [resource.resourceId])
        return HostCommand(
            commandId: "cmd:lease-main", leaseId: "lease-main", leaseVersion: 1, runId: "run-main",
            schedulerFence: 1, profileId: "host.repo.identity@1", capabilityId: "host.repo.identity",
            capabilityVersion: "1", capabilityGrantVersion: 1, resources: [resource],
            inputHash: HostProtocol.sha256Hex(try HostProtocol.canonicalJSONEncoder().encode(input)),
            issuedAt: "2026-08-14T11:59:00Z", executeUntil: executeUntil,
            input: input, outputLimitBytes: 32 * 1024, eventLimit: 50
        )
    }

    func testFixedProfileRunsWithoutGenericAuthorityAndClearsJournal() async throws {
        let command = try command()
        let journal = AgentJournal()
        let executor = AgentExecutor(command: command)
        let identifiers = IdentifierSource()
        let channel = try ReliableHostChannel(
            hostId: "host-main",
            signer: AgentSigner(),
            journal: journal,
            executor: executor,
            now: { Date(timeIntervalSince1970: 1_723_636_800) },
            makeMessageId: { identifiers.next("message") }
        )
        let repository = AgentRepository()
        let agent = HostAgent(
            channel: channel,
            journal: journal,
            repository: repository,
            now: { Date(timeIntervalSince1970: 1_723_636_800) },
            makeIdentifier: { prefix in identifiers.next(prefix) }
        )
        try await agent.runOneCycle()
        let state = try await journal.load()
        XCTAssertNil(state.activeAttempt)
        XCTAssertNil(state.pendingMessage)
        XCTAssertEqual(state.lastAcceptedSequence, 6)
        let repositoryCalls = await repository.callCount()
        XCTAssertEqual(repositoryCalls, 1)
        let snapshot = await executor.snapshot()
        let paths = snapshot.paths
        XCTAssertEqual(paths, [
            "/host-channel/poll",
            "/host-channel/leases/lease-main/ack",
            "/host-channel/leases/lease-main/events",
            "/host-channel/leases/lease-main/events",
            "/host-channel/leases/lease-main/artifacts",
            "/host-channel/leases/lease-main/complete",
        ])
        let artifactBody = try XCTUnwrap(snapshot.bodies.last(where: { data in
            String(decoding: data, as: UTF8.self).contains("artifactId")
        }))
        let artifactText = String(decoding: artifactBody, as: UTF8.self)
        XCTAssertFalse(artifactText.contains("/Users/"))
        XCTAssertFalse(artifactText.contains("Process"))
        XCTAssertTrue(artifactText.contains("resourceFingerprint"))
    }


    func testRepositoryUncertaintyCompletesUnknownWithoutArtifact() async throws {
        let command = try command()
        let identifiers = IdentifierSource()
        let journal = AgentJournal()
        let executor = AgentExecutor(command: command)
        let channel = try ReliableHostChannel(
            hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor,
            now: { Date(timeIntervalSince1970: 1_723_636_800) },
            makeMessageId: { identifiers.next("message") }
        )
        let agent = HostAgent(
            channel: channel, journal: journal, repository: FailingRepository(),
            now: { Date(timeIntervalSince1970: 1_723_636_800) },
            makeIdentifier: { identifiers.next($0) }
        )
        try await agent.runOneCycle()
        let snapshot = await executor.snapshot()
        XCTAssertEqual(snapshot.paths.last, "/host-channel/leases/lease-main/complete")
        XCTAssertFalse(snapshot.paths.contains("/host-channel/leases/lease-main/artifacts"))
        let completion = try XCTUnwrap(snapshot.bodies.last)
        XCTAssertTrue(String(decoding: completion, as: UTF8.self).contains("\"outcome\":\"UNKNOWN\""))
        let finalState = try await journal.load()
        XCTAssertNil(finalState.activeAttempt)
    }

    func testRestartRetriesArtifactExactlyThenCompletesWithoutRereadingRepository() async throws {
        let command = try command()
        let identifiers = IdentifierSource()
        let journal = AgentJournal()
        let executor = AgentExecutor(command: command, failOnceAtPath: "/host-channel/leases/lease-main/artifacts")
        let repository = AgentRepository()
        func makeAgent() throws -> HostAgent {
            let channel = try ReliableHostChannel(
                hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor,
                now: { Date(timeIntervalSince1970: 1_723_636_800) },
                makeMessageId: { identifiers.next("message") }
            )
            return HostAgent(
                channel: channel, journal: journal, repository: repository,
                now: { Date(timeIntervalSince1970: 1_723_636_800) },
                makeIdentifier: { identifiers.next($0) }
            )
        }
        do { try await makeAgent().runOneCycle(); XCTFail("Expected artifact network failure") }
        catch AgentExecutor.Failure.network {}
        let failedState = try await journal.load()
        XCTAssertEqual(failedState.pendingMessage?.operation, .leaseArtifact)
        XCTAssertEqual(failedState.activeAttempt?.stage, .stepCompleted)
        let pendingBody = failedState.pendingMessage?.body
        try await makeAgent().runOneCycle()
        let finishedState = try await journal.load()
        XCTAssertNil(finishedState.pendingMessage)
        XCTAssertNil(finishedState.activeAttempt)
        let repositoryCalls = await repository.callCount()
        XCTAssertEqual(repositoryCalls, 1)
        let snapshot = await executor.snapshot()
        let artifactBodies = snapshot.bodies.filter { String(decoding: $0, as: UTF8.self).contains("artifactId") }
        XCTAssertEqual(artifactBodies, [pendingBody, pendingBody].compactMap { $0 })
    }

    func testExpiredCommandIsDeclinedWithoutRepositoryRead() async throws {
        let testNow = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-08-14T12:00:00Z"))
        let identifiers = IdentifierSource()
        let journal = AgentJournal()
        let executor = AgentExecutor(command: try command(executeUntil: "2026-08-14T11:59:59Z"))
        let repository = AgentRepository()
        let channel = try ReliableHostChannel(
            hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor,
            now: { testNow },
            makeMessageId: { identifiers.next("message") }
        )
        let agent = HostAgent(
            channel: channel, journal: journal, repository: repository,
            now: { testNow },
            makeIdentifier: { identifiers.next($0) }
        )
        try await agent.runOneCycle()
        let repositoryCalls = await repository.callCount()
        XCTAssertEqual(repositoryCalls, 0)
        let snapshot = await executor.snapshot()
        XCTAssertEqual(snapshot.paths, ["/host-channel/poll", "/host-channel/leases/lease-main/ack"])
        XCTAssertTrue(String(decoding: snapshot.bodies.last!, as: UTF8.self).contains("lease-expired"))
    }

    func testActiveAttemptReconcilesBeforeAnyRepositoryReadAndClearsOnConflict() async throws {
        let command = try command()
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: "attempt-1",
            state: .started,
            stage: .running,
            command: command,
            artifactId: "artifact-1",
            updatedAt: "2026-08-14T12:00:00Z"
        )
        let journal = AgentJournal()
        try await journal.save(.init(lastAcceptedSequence: 5, activeAttempt: attempt))
        let executor = AgentExecutor(command: command, reconcileResolution: .reconciliationRequired)
        let repository = AgentRepository()
        let identifiers = IdentifierSource()
        let channel = try ReliableHostChannel(
            hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor,
            now: { Date(timeIntervalSince1970: 1_723_636_800) },
            makeMessageId: { identifiers.next("message") }
        )
        let agent = HostAgent(channel: channel, journal: journal, repository: repository)
        try await agent.runOneCycle()
        let snapshot = await executor.snapshot()
        let calls = await repository.callCount()
        let final = try await journal.load()
        XCTAssertEqual(snapshot.paths, ["/host-channel/leases/lease-main/reconcile"])
        XCTAssertEqual(calls, 0)
        XCTAssertNil(final.activeAttempt)
    }

    func testPendingReconcileReplayAppliesConflictBeforeRepositoryRead() async throws {
        for resolution in [HostLeaseReconcileResolution.requeued, .reconciliationRequired] {
            let command = try command()
            let attempt = HostAttemptRecord(
                leaseId: command.leaseId,
                leaseVersion: command.leaseVersion,
                runId: command.runId,
                localAttemptId: "attempt-1",
                state: .started,
                stage: .running,
                command: command,
                artifactId: "artifact-1",
                updatedAt: "2026-08-14T12:00:00Z"
            )
            let request = HostLeaseReconcileRequest(
                leaseVersion: attempt.leaseVersion,
                localAttemptId: attempt.localAttemptId,
                observedState: attempt.state,
                outputSha256: nil
            )
            let pending = PendingHostMessage(
                operation: .leaseReconcile,
                targetId: attempt.leaseId,
                messageId: "message-reconcile",
                sequence: 6,
                timestamp: 1_723_636_800,
                body: try HostProtocol.canonicalJSONEncoder().encode(request)
            )
            let journal = AgentJournal()
            try await journal.save(.init(lastAcceptedSequence: 5, pendingMessage: pending, activeAttempt: attempt))
            let executor = AgentExecutor(command: command, reconcileResolution: resolution)
            let repository = AgentRepository()
            let channel = try ReliableHostChannel(hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor)
            let agent = HostAgent(channel: channel, journal: journal, repository: repository)

            try await agent.runOneCycle()

            let final = try await journal.load()
            let calls = await repository.callCount()
            let snapshot = await executor.snapshot()
            XCTAssertNil(final.pendingMessage, resolution.rawValue)
            XCTAssertNil(final.activeAttempt, resolution.rawValue)
            XCTAssertEqual(final.lastAcceptedSequence, 6, resolution.rawValue)
            XCTAssertEqual(calls, 0, resolution.rawValue)
            XCTAssertEqual(snapshot.paths, ["/host-channel/leases/lease-main/reconcile"], resolution.rawValue)
        }
    }

    func testPendingReconcileReplayResumesWithServerLeaseVersion() async throws {
        let command = try command()
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: "attempt-1",
            state: .started,
            stage: .running,
            command: command,
            artifactId: "artifact-1",
            updatedAt: "2026-08-14T12:00:00Z"
        )
        let request = HostLeaseReconcileRequest(
            leaseVersion: attempt.leaseVersion,
            localAttemptId: attempt.localAttemptId,
            observedState: attempt.state,
            outputSha256: nil
        )
        let pending = PendingHostMessage(
            operation: .leaseReconcile,
            targetId: attempt.leaseId,
            messageId: "message-reconcile",
            sequence: 6,
            timestamp: 1_723_636_800,
            body: try HostProtocol.canonicalJSONEncoder().encode(request)
        )
        let journal = AgentJournal()
        try await journal.save(.init(lastAcceptedSequence: 5, pendingMessage: pending, activeAttempt: attempt))
        let executor = AgentExecutor(command: command, reconcileResolution: .resume)
        let repository = AgentRepository()
        let identifiers = IdentifierSource()
        let channel = try ReliableHostChannel(
            hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor,
            makeMessageId: { identifiers.next("message") }
        )
        let agent = HostAgent(
            channel: channel, journal: journal, repository: repository,
            now: { Date(timeIntervalSince1970: 1_723_636_800) },
            makeIdentifier: { identifiers.next($0) }
        )

        try await agent.runOneCycle()

        let final = try await journal.load()
        let calls = await repository.callCount()
        let snapshot = await executor.snapshot()
        XCTAssertNil(final.pendingMessage)
        XCTAssertNil(final.activeAttempt)
        XCTAssertEqual(calls, 1)
        XCTAssertEqual(snapshot.paths.first, "/host-channel/leases/lease-main/reconcile")
        XCTAssertEqual(snapshot.paths.last, "/host-channel/leases/lease-main/complete")
    }

    func testSubstitutedReconcileLeaseFailsClosedOnFreshAndReplayResponses() async throws {
        let command = try command()
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: "attempt-1",
            state: .started,
            stage: .running,
            command: command,
            artifactId: "artifact-1",
            updatedAt: "2026-08-14T12:00:00Z"
        )
        let journal = AgentJournal()
        try await journal.save(.init(lastAcceptedSequence: 5, activeAttempt: attempt))
        let executor = AgentExecutor(command: command, reconcileLeaseId: "lease-substituted")
        let repository = AgentRepository()
        let channel = try ReliableHostChannel(hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor)
        let agent = HostAgent(channel: channel, journal: journal, repository: repository)

        for _ in 0..<2 {
            do {
                try await agent.runOneCycle()
                XCTFail("Expected substituted reconcile response rejection")
            } catch HostChannelError.invalidResponse {}
        }

        let final = try await journal.load()
        let snapshot = await executor.snapshot()
        let calls = await repository.callCount()
        XCTAssertEqual(snapshot.paths, [
            "/host-channel/leases/lease-main/reconcile",
            "/host-channel/leases/lease-main/reconcile",
        ])
        XCTAssertEqual(calls, 0)
        XCTAssertEqual(final.pendingMessage?.operation, .leaseReconcile)
        XCTAssertEqual(final.activeAttempt, attempt)
        XCTAssertEqual(final.lastAcceptedSequence, 5)
    }

    func testUnknownReconcileResolutionFailsClosedDuringDecode() throws {
        let data = Data("{\"resolution\":\"BOGUS\",\"lease\":null}".utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(HostLeaseReconcileResponse.self, from: data))
    }

    func testConsumedAckRejectionClearsPreparedAttemptBeforeRestart() async throws {
        let command = try command()
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: "attempt-1",
            state: .notStarted,
            stage: .prepared,
            command: command,
            artifactId: "artifact-1",
            updatedAt: "2026-08-14T12:00:00Z"
        )
        let journal = AgentJournal()
        try await journal.save(.init(activeAttempt: attempt))
        let executor = AckRejectingExecutor(command: command)
        let channel = try ReliableHostChannel(hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor)
        let repository = AgentRepository()
        let agent = HostAgent(
            channel: channel,
            journal: journal,
            repository: repository,
            now: { Date(timeIntervalSince1970: 1_723_636_800) }
        )

        do {
            try await agent.runOneCycle()
            XCTFail("Expected consumed acknowledgement rejection")
        } catch HostChannelError.businessRejection(409, "host_lease_expired") {}
        let rejected = try await journal.load()
        XCTAssertNil(rejected.pendingMessage)
        XCTAssertNil(rejected.activeAttempt)
        XCTAssertEqual(rejected.lastAcceptedSequence, 1)

        try await agent.runOneCycle()
        let snapshot = await executor.snapshot()
        let calls = await repository.callCount()
        XCTAssertEqual(snapshot.paths, ["/host-channel/leases/lease-main/ack", "/host-channel/poll"])
        XCTAssertEqual(snapshot.paths.filter { $0.hasSuffix("/ack") }.count, 1)
        XCTAssertEqual(calls, 0)
    }

    func testRestartedPreparedAttemptRechecksExpiryBeforeAcceptedAck() async throws {
        let command = try command(executeUntil: "2026-08-14T11:59:59Z")
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: "attempt-1",
            state: .notStarted,
            stage: .prepared,
            command: command,
            artifactId: "artifact-1",
            updatedAt: "2026-08-14T11:59:58Z"
        )
        let journal = AgentJournal()
        try await journal.save(.init(activeAttempt: attempt))
        let executor = AgentExecutor(command: command)
        let channel = try ReliableHostChannel(hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor)
        let testNow = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-08-14T12:00:00Z"))
        let agent = HostAgent(
            channel: channel,
            journal: journal,
            repository: AgentRepository(),
            now: { testNow }
        )

        try await agent.runOneCycle()

        let snapshot = await executor.snapshot()
        let acknowledgement = try JSONDecoder().decode(HostLeaseAckRequest.self, from: XCTUnwrap(snapshot.bodies.first))
        XCTAssertEqual(snapshot.paths, ["/host-channel/leases/lease-main/ack"])
        XCTAssertFalse(acknowledgement.accepted)
        XCTAssertEqual(acknowledgement.rejectionCode, "lease-expired")
        let final = try await journal.load()
        XCTAssertNil(final.activeAttempt)
    }

    func testDeclinedAckRetryClearsAttemptInsteadOfResurrectingExecution() async throws {
        let command = try command()
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: "attempt-1",
            state: .notStarted,
            stage: .prepared,
            command: command,
            artifactId: "artifact-1",
            updatedAt: "2026-08-14T12:00:00Z"
        )
        let body = try HostProtocol.canonicalJSONEncoder().encode(HostLeaseAckRequest(
            leaseVersion: 1,
            localAttemptId: "attempt-1",
            accepted: false,
            rejectionCode: "lease-expired"
        ))
        let pending = PendingHostMessage(
            operation: .leaseAck,
            targetId: "lease-main",
            messageId: "message-decline",
            sequence: 2,
            timestamp: 1_723_636_800,
            body: body
        )
        let journal = AgentJournal()
        try await journal.save(.init(lastAcceptedSequence: 1, pendingMessage: pending, activeAttempt: attempt))
        let executor = AgentExecutor(command: command)
        let repository = AgentRepository()
        let channel = try ReliableHostChannel(hostId: "host-main", signer: AgentSigner(), journal: journal, executor: executor)
        let agent = HostAgent(channel: channel, journal: journal, repository: repository)
        try await agent.runOneCycle()
        let final = try await journal.load()
        XCTAssertNil(final.pendingMessage)
        XCTAssertNil(final.activeAttempt)
        XCTAssertEqual(final.lastAcceptedSequence, 2)
        let snapshot = await executor.snapshot()
        let calls = await repository.callCount()
        XCTAssertEqual(snapshot.paths, ["/host-channel/leases/lease-main/ack"])
        XCTAssertEqual(calls, 0)
    }
}