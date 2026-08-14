import Foundation

public protocol HostRepositoryIdentityProvider: Sendable {
    func identity(for resource: HostLeaseResource) async throws -> RepositoryIdentity
}

public actor HostAgent {
    private let channel: ReliableHostChannel
    private let journal: any HostSessionJournal
    private let repository: any HostRepositoryIdentityProvider
    private let now: @Sendable () -> Date
    private let makeIdentifier: @Sendable (String) -> String

    public init(
        channel: ReliableHostChannel,
        journal: any HostSessionJournal,
        repository: any HostRepositoryIdentityProvider,
        now: @escaping @Sendable () -> Date = Date.init,
        makeIdentifier: @escaping @Sendable (String) -> String = { prefix in
            "\(prefix)-\(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())"
        }
    ) {
        self.channel = channel
        self.journal = journal
        self.repository = repository
        self.now = now
        self.makeIdentifier = makeIdentifier
    }

    public func runOneCycle() async throws {
        let initial = try await journal.load()
        if let pending = initial.pendingMessage {
            let poll = try await resume(pending: pending)
            if let poll { try await handlePoll(poll) }
            if try await journal.load().activeAttempt != nil { try await continueAttempt() }
            return
        }

        if let active = initial.activeAttempt {
            if active.stage == .prepared {
                try await continueAttempt()
                return
            }
            let response: HostLeaseReconcileResponse = try await channel.send(
                operation: .leaseReconcile,
                targetId: active.leaseId,
                body: HostLeaseReconcileRequest(
                    leaseVersion: active.leaseVersion,
                    localAttemptId: active.localAttemptId,
                    observedState: active.state,
                    outputSha256: active.outputSha256
                ),
                response: HostLeaseReconcileResponse.self
            ) { state, response in
                try Self.applyReconciliation(response, to: &state)
            }
            if response.resolution != .resume { return }
            try await continueAttempt()
            return
        }

        let response: HostPollResponse = try await channel.send(
            operation: .poll,
            targetId: "-",
            body: try HostPollRequest(maxWaitSeconds: 25, activeAttempt: nil),
            response: HostPollResponse.self
        )
        try await handlePoll(response)
        if try await journal.load().activeAttempt != nil { try await continueAttempt() }
    }

    private func handlePoll(_ response: HostPollResponse) async throws {
        var state = try await journal.load()
        guard let command = response.command else { return }
        try command.validateProofProfile()
        guard let expiry = Self.parseDate(command.executeUntil), expiry > now() else {
            try await decline(command: command, code: "lease-expired")
            return
        }
        let attempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: makeIdentifier("attempt"),
            state: .notStarted,
            stage: .prepared,
            command: command,
            artifactId: makeIdentifier("artifact"),
            updatedAt: Self.timestamp(now())
        )
        state.activeAttempt = attempt
        try await journal.save(state)
    }

    private func continueAttempt() async throws {
        while let attempt = try await journal.load().activeAttempt {
            if let expiry = Self.parseDate(attempt.command.executeUntil), expiry <= now() {
                if attempt.stage == .prepared { try await declinePrepared(attempt, code: "lease-expired") }
                return
            }
            switch attempt.stage {
            case .prepared:
                let request = HostLeaseAckRequest(
                    leaseVersion: attempt.leaseVersion,
                    localAttemptId: attempt.localAttemptId,
                    accepted: true,
                    rejectionCode: nil
                )
                _ = try await channel.send(
                    operation: .leaseAck,
                    targetId: attempt.leaseId,
                    body: request,
                    response: HostResourceVersion.self
                ) { state, response in
                    guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                    current.leaseVersion = response.version
                    current.stage = .acknowledged
                    state.activeAttempt = current
                } onRejection: { state, _, _ in
                    state.activeAttempt = nil
                }
            case .acknowledged:
                try await updateAttempt { current in
                    current.state = .started
                    current.stage = .startEventPending
                }
            case .startEventPending:
                let events = [
                    HostLeaseEventInput(eventId: makeIdentifier("event"), sequence: 1, type: "JOB_ACCEPTED", occurredAt: Self.timestamp(now()), summary: "Host accepted the repository identity lease", data: [:]),
                    HostLeaseEventInput(eventId: makeIdentifier("event"), sequence: 2, type: "STEP_STARTED", occurredAt: Self.timestamp(now()), summary: "Reading descriptor-bound repository identity", data: ["profile": "host.repo.identity@1"]),
                ]
                _ = try await channel.send(
                    operation: .leaseEvents,
                    targetId: attempt.leaseId,
                    body: HostLeaseEventsRequest(leaseVersion: attempt.leaseVersion, localAttemptId: attempt.localAttemptId, events: events),
                    response: HostResourceVersion.self
                ) { state, response in
                    guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                    current.leaseVersion = response.version
                    current.stage = .running
                    state.activeAttempt = current
                }
            case .running:
                let identity: RepositoryIdentity
                do {
                    identity = try await repository.identity(for: attempt.command.resources[0])
                } catch {
                    try await completeUnknown(attempt)
                    return
                }
                let data = try HostProtocol.canonicalJSONEncoder().encode(identity)
                let normalized = try HostOutputNormalizer.normalize(data, limitBytes: attempt.command.outputLimitBytes)
                try await updateAttempt { current in
                    current.outputText = normalized.text
                    current.outputSha256 = normalized.sha256
                    current.stage = .outputReady
                }
            case .outputReady:
                let event = HostLeaseEventInput(
                    eventId: makeIdentifier("event"),
                    sequence: 3,
                    type: "STEP_COMPLETED",
                    occurredAt: Self.timestamp(now()),
                    summary: "Repository identity captured",
                    data: [:]
                )
                _ = try await channel.send(
                    operation: .leaseEvents,
                    targetId: attempt.leaseId,
                    body: HostLeaseEventsRequest(leaseVersion: attempt.leaseVersion, localAttemptId: attempt.localAttemptId, events: [event]),
                    response: HostResourceVersion.self
                ) { state, response in
                    guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                    current.leaseVersion = response.version
                    current.stage = .stepCompleted
                    state.activeAttempt = current
                }
            case .stepCompleted:
                guard let output = attempt.outputText,
                      let outputData = output.data(using: .utf8),
                      let outputHash = attempt.outputSha256
                else { throw HostChannelError.invalidJournal }
                let artifact = HostLeaseArtifactRequest(
                    leaseVersion: attempt.leaseVersion,
                    localAttemptId: attempt.localAttemptId,
                    artifactId: attempt.artifactId,
                    kind: "TEXT",
                    mediaType: "text/plain",
                    summary: "Repository identity",
                    declaredSize: outputData.count,
                    declaredSha256: outputHash,
                    retention: "RUN",
                    textContent: output
                )
                _ = try await channel.send(
                    operation: .leaseArtifact,
                    targetId: attempt.leaseId,
                    body: artifact,
                    response: IgnoredHostResponse.self
                ) { state, _ in
                    guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                    current.stage = .artifactAccepted
                    state.activeAttempt = current
                }
            case .artifactAccepted:
                let completion = HostLeaseCompleteRequest(
                    leaseVersion: attempt.leaseVersion,
                    outcome: "SUCCEEDED",
                    output: attempt.outputText,
                    outputSha256: attempt.outputSha256,
                    truncated: false,
                    localAttemptId: attempt.localAttemptId
                )
                _ = try await channel.send(
                    operation: .leaseComplete,
                    targetId: attempt.leaseId,
                    body: completion,
                    response: IgnoredHostResponse.self
                ) { state, _ in
                    state.activeAttempt = nil
                }
            }
        }
    }

    private func resume(pending: PendingHostMessage) async throws -> HostPollResponse? {
        switch pending.operation {
        case .poll:
            return try await channel.retryPending(response: HostPollResponse.self)
        case .leaseAck:
            let acknowledgement = try JSONDecoder().decode(HostLeaseAckRequest.self, from: pending.body)
            _ = try await channel.retryPending(response: HostResourceVersion.self) { state, response in
                if acknowledgement.accepted {
                    guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                    current.leaseVersion = response.version
                    current.stage = .acknowledged
                    state.activeAttempt = current
                } else {
                    state.activeAttempt = nil
                }
            } onRejection: { state, _, _ in
                state.activeAttempt = nil
            }
        case .leaseEvents:
            _ = try await channel.retryPending(response: HostResourceVersion.self) { state, response in
                guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                current.leaseVersion = response.version
                current.stage = current.stage == .startEventPending ? .running : .stepCompleted
                state.activeAttempt = current
            }
        case .leaseArtifact:
            _ = try await channel.retryPending(response: IgnoredHostResponse.self) { state, _ in
                guard var current = state.activeAttempt else { throw HostChannelError.invalidJournal }
                current.stage = .artifactAccepted
                state.activeAttempt = current
            }
        case .leaseComplete:
            _ = try await channel.retryPending(response: IgnoredHostResponse.self) { state, _ in state.activeAttempt = nil }
        case .leaseReconcile:
            _ = try await channel.retryPending(response: HostLeaseReconcileResponse.self) { state, response in
                try Self.applyReconciliation(response, to: &state)
            }
        }
        return nil
    }

    private func decline(command: HostCommand, code: String) async throws {
        let attemptId = makeIdentifier("attempt")
        var state = try await journal.load()
        state.activeAttempt = HostAttemptRecord(
            leaseId: command.leaseId,
            leaseVersion: command.leaseVersion,
            runId: command.runId,
            localAttemptId: attemptId,
            state: .notStarted,
            stage: .prepared,
            command: command,
            artifactId: makeIdentifier("artifact"),
            updatedAt: Self.timestamp(now())
        )
        try await journal.save(state)
        try await declinePrepared(state.activeAttempt!, code: code)
    }

    private func declinePrepared(_ attempt: HostAttemptRecord, code: String) async throws {
        _ = try await channel.send(
            operation: .leaseAck,
            targetId: attempt.leaseId,
            body: HostLeaseAckRequest(leaseVersion: attempt.leaseVersion, localAttemptId: attempt.localAttemptId, accepted: false, rejectionCode: code),
            response: HostResourceVersion.self
        ) { state, _ in state.activeAttempt = nil } onRejection: { state, _, _ in
            state.activeAttempt = nil
        }
    }

    private func completeUnknown(_ attempt: HostAttemptRecord) async throws {
        let completion = HostLeaseCompleteRequest(
            leaseVersion: attempt.leaseVersion,
            outcome: "UNKNOWN",
            output: nil,
            outputSha256: nil,
            truncated: false,
            localAttemptId: attempt.localAttemptId
        )
        _ = try await channel.send(
            operation: .leaseComplete,
            targetId: attempt.leaseId,
            body: completion,
            response: IgnoredHostResponse.self
        ) { state, _ in state.activeAttempt = nil }
    }

    private func updateAttempt(_ update: (inout HostAttemptRecord) throws -> Void) async throws {
        var state = try await journal.load()
        guard var attempt = state.activeAttempt else { throw HostChannelError.invalidJournal }
        try update(&attempt)
        attempt.updatedAt = Self.timestamp(now())
        state.activeAttempt = attempt
        try await journal.save(state)
    }

    private static func parseDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: value) { return date }
        return ISO8601DateFormatter().date(from: value)
    }

    private static func applyReconciliation(_ response: HostLeaseReconcileResponse, to state: inout HostSessionState) throws {
        guard var current = state.activeAttempt,
              let lease = response.lease,
              lease.leaseId == current.leaseId,
              lease.runId == current.runId,
              lease.localAttemptId == current.localAttemptId,
              lease.leaseVersion >= current.leaseVersion,
              lease.resources == current.command.resources
        else { throw HostChannelError.invalidResponse }
        switch response.resolution {
        case .resume:
            guard lease.state == "RUNNING" else { throw HostChannelError.invalidResponse }
            current.leaseVersion = lease.leaseVersion
            state.activeAttempt = current
        case .requeued:
            guard lease.state == "EXPIRED" else { throw HostChannelError.invalidResponse }
            state.activeAttempt = nil
        case .reconciliationRequired:
            guard lease.state == "RECONCILIATION_REQUIRED" else { throw HostChannelError.invalidResponse }
            state.activeAttempt = nil
        }
    }

    private static func timestamp(_ value: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: value)
    }
}