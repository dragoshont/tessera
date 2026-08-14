import Foundation

public enum HostChannelError: Error, Equatable, Sendable {
    case pendingMessageExists
    case noPendingMessage
    case unconsumedResponse(Int, String?)
    case businessRejection(Int, String?)
    case responseTooLarge
    case invalidResponse
    case invalidServerDescriptor
    case invalidPath
    case invalidJournal
    case terminal(String)

    public static func requiresOperatorForUnconsumedProblem(_ code: String?) -> Bool {
        switch code {
        case "host_auth_invalid", "host_revoked", "host_replay", "host_sequence_invalid", "host_protocol_unsupported":
            true
        default:
            false
        }
    }

    public static func canRefreshTimestampAfterUnconsumedProblem(_ code: String?) -> Bool {
        code == "host_clock_skew"
    }
}

public struct HostAttemptRecord: Codable, Equatable, Sendable {
    public var leaseId: String
    public var leaseVersion: Int64
    public var runId: String
    public var localAttemptId: String
    public var state: HostAttemptState
    public var stage: HostAttemptStage
    public var command: HostCommand
    public var artifactId: String
    public var outputText: String?
    public var outputSha256: String?
    public var updatedAt: String

    public init(
        leaseId: String,
        leaseVersion: Int64,
        runId: String,
        localAttemptId: String,
        state: HostAttemptState,
        stage: HostAttemptStage,
        command: HostCommand,
        artifactId: String,
        outputText: String? = nil,
        outputSha256: String? = nil,
        updatedAt: String
    ) {
        self.leaseId = leaseId
        self.leaseVersion = leaseVersion
        self.runId = runId
        self.localAttemptId = localAttemptId
        self.state = state
        self.stage = stage
        self.command = command
        self.artifactId = artifactId
        self.outputText = outputText
        self.outputSha256 = outputSha256
        self.updatedAt = updatedAt
    }
}

public enum HostAttemptStage: String, Codable, Sendable {
    case prepared
    case acknowledged
    case startEventPending
    case running
    case outputReady
    case stepCompleted
    case artifactAccepted
}

public struct PendingHostMessage: Codable, Equatable, Sendable {
    public let operation: HostOperation
    public let targetId: String
    public let messageId: String
    public let sequence: Int64
    public let timestamp: Int64
    public let body: Data

    public init(operation: HostOperation, targetId: String, messageId: String, sequence: Int64, timestamp: Int64, body: Data) {
        self.operation = operation
        self.targetId = targetId
        self.messageId = messageId
        self.sequence = sequence
        self.timestamp = timestamp
        self.body = body
    }
}

public struct HostSessionState: Codable, Equatable, Sendable {
    public let schemaVersion: Int
    public var lastAcceptedSequence: Int64
    public var pendingMessage: PendingHostMessage?
    public var activeAttempt: HostAttemptRecord?

    public init(
        schemaVersion: Int = 1,
        lastAcceptedSequence: Int64 = 0,
        pendingMessage: PendingHostMessage? = nil,
        activeAttempt: HostAttemptRecord? = nil
    ) {
        self.schemaVersion = schemaVersion
        self.lastAcceptedSequence = lastAcceptedSequence
        self.pendingMessage = pendingMessage
        self.activeAttempt = activeAttempt
    }
}

public protocol HostSessionJournal: Sendable {
    func load() async throws -> HostSessionState
    func save(_ state: HostSessionState) async throws
}

public struct HostHTTPResponse: Equatable, Sendable {
    public let statusCode: Int
    public let body: Data
    public let envelopeConsumed: Bool

    public init(statusCode: Int, body: Data, envelopeConsumed: Bool) {
        self.statusCode = statusCode
        self.body = body
        self.envelopeConsumed = envelopeConsumed
    }
}

public protocol HostHTTPExecutor: Sendable {
    func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse
}

public actor ReliableHostChannel {
    private let hostId: String
    private let signer: any HostRequestSigner
    private let journal: any HostSessionJournal
    private let executor: any HostHTTPExecutor
    private let now: @Sendable () -> Date
    private let makeMessageId: @Sendable () -> String

    public init(
        hostId: String,
        signer: any HostRequestSigner,
        journal: any HostSessionJournal,
        executor: any HostHTTPExecutor,
        now: @escaping @Sendable () -> Date = Date.init,
        makeMessageId: @escaping @Sendable () -> String = {
            "msg-\(UUID().uuidString.lowercased())"
        }
    ) throws {
        try HostProtocol.validateIdentifier(hostId, name: "hostId")
        self.hostId = hostId
        self.signer = signer
        self.journal = journal
        self.executor = executor
        self.now = now
        self.makeMessageId = makeMessageId
    }

    public func send<Request: Encodable & Sendable, Response: Decodable & Sendable>(
        operation: HostOperation,
        targetId: String,
        body: Request,
        response: Response.Type,
        onSuccess: @escaping @Sendable (inout HostSessionState, Response) throws -> Void = { _, _ in },
        onRejection: @escaping @Sendable (inout HostSessionState, Int, String?) -> Void = { _, _, _ in }
    ) async throws -> Response {
        var state = try await journal.load()
        guard state.schemaVersion == 1 else { throw HostChannelError.invalidJournal }
        guard state.pendingMessage == nil else { throw HostChannelError.pendingMessageExists }
        let bodyData = try HostProtocol.canonicalJSONEncoder().encode(body)
        let pending = PendingHostMessage(
            operation: operation,
            targetId: targetId,
            messageId: makeMessageId(),
            sequence: state.lastAcceptedSequence + 1,
            timestamp: Int64(now().timeIntervalSince1970),
            body: bodyData
        )
        state.pendingMessage = pending
        try await journal.save(state)
        return try await executePending(state: state, response: response, onSuccess: onSuccess, onRejection: onRejection)
    }

    public func retryPending<Response: Decodable & Sendable>(
        response: Response.Type,
        onSuccess: @escaping @Sendable (inout HostSessionState, Response) throws -> Void = { _, _ in },
        onRejection: @escaping @Sendable (inout HostSessionState, Int, String?) -> Void = { _, _, _ in }
    ) async throws -> Response {
        let state = try await journal.load()
        guard state.pendingMessage != nil else { throw HostChannelError.noPendingMessage }
        return try await executePending(state: state, response: response, onSuccess: onSuccess, onRejection: onRejection)
    }

    public func clearUnconsumedPending() async throws {
        var state = try await journal.load()
        state.pendingMessage = nil
        try await journal.save(state)
    }

    private func executePending<Response: Decodable & Sendable>(
        state: HostSessionState,
        response: Response.Type,
        onSuccess: @Sendable (inout HostSessionState, Response) throws -> Void,
        onRejection: @Sendable (inout HostSessionState, Int, String?) -> Void
    ) async throws -> Response {
        guard let pending = state.pendingMessage else { throw HostChannelError.noPendingMessage }
        let prepared = try HostProtocol.prepareSignedRequest(
            operation: pending.operation,
            targetId: pending.targetId,
            hostId: hostId,
            messageId: pending.messageId,
            sequence: pending.sequence,
            timestamp: pending.timestamp,
            bodyData: pending.body,
            signer: signer
        )
        let result = try await executor.execute(path: Self.path(operation: pending.operation, targetId: pending.targetId), request: prepared)
        guard result.envelopeConsumed else {
            throw HostChannelError.unconsumedResponse(result.statusCode, Self.problemCode(result.body))
        }
        var committed = state
        guard (200..<300).contains(result.statusCode) else {
            let code = Self.problemCode(result.body)
            onRejection(&committed, result.statusCode, code)
            committed.lastAcceptedSequence = pending.sequence
            committed.pendingMessage = nil
            try await journal.save(committed)
            throw HostChannelError.businessRejection(result.statusCode, code)
        }
        let decoded: Response
        do {
            decoded = try JSONDecoder().decode(response, from: result.body)
            try onSuccess(&committed, decoded)
        } catch {
            throw HostChannelError.invalidResponse
        }
        committed.lastAcceptedSequence = pending.sequence
        committed.pendingMessage = nil
        try await journal.save(committed)
        return decoded
    }

    private static func path(operation: HostOperation, targetId: String) -> String {
        switch operation {
        case .poll: "/host-channel/poll"
        case .leaseAck: "/host-channel/leases/\(targetId)/ack"
        case .leaseEvents: "/host-channel/leases/\(targetId)/events"
        case .leaseComplete: "/host-channel/leases/\(targetId)/complete"
        case .leaseReconcile: "/host-channel/leases/\(targetId)/reconcile"
        case .leaseArtifact: "/host-channel/leases/\(targetId)/artifacts"
        }
    }

    private static func problemCode(_ data: Data) -> String? {
        guard let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return value["code"] as? String
    }
}

public struct ExponentialBackoff: Equatable, Sendable {
    public let baseMilliseconds: Int
    public let maximumMilliseconds: Int

    public init(baseMilliseconds: Int = 500, maximumMilliseconds: Int = 30_000) throws {
        guard baseMilliseconds >= 100, maximumMilliseconds >= baseMilliseconds else {
            throw HostChannelError.invalidJournal
        }
        self.baseMilliseconds = baseMilliseconds
        self.maximumMilliseconds = maximumMilliseconds
    }

    public func delayMilliseconds(attempt: Int, jitterUnit: Double) -> Int {
        let boundedAttempt = min(max(attempt, 0), 20)
        let exponential = min(maximumMilliseconds, baseMilliseconds * (1 << boundedAttempt))
        let boundedJitter = min(max(jitterUnit, 0), 1)
        return Int(Double(exponential) * (0.75 + boundedJitter * 0.5))
    }
}