import Foundation

public enum HostKeyProtection: String, Codable, Sendable {
    case secureEnclave = "SECURE_ENCLAVE"
    case keychainThisDeviceOnly = "KEYCHAIN_THIS_DEVICE_ONLY"
}

public struct P256PublicJWK: Codable, Equatable, Sendable {
    public let kty: String
    public let crv: String
    public let x: String
    public let y: String

    public init(x: String, y: String) {
        self.kty = "EC"
        self.crv = "P-256"
        self.x = x
        self.y = y
    }
}

public struct RequestedHostCapability: Codable, Equatable, Sendable {
    public let capabilityId: String
    public let capabilityVersion: String
    public let schemaHash: String
    public let sideEffectClass: String

    public init(capabilityId: String, capabilityVersion: String, schemaHash: String, sideEffectClass: String) {
        self.capabilityId = capabilityId
        self.capabilityVersion = capabilityVersion
        self.schemaHash = schemaHash
        self.sideEffectClass = sideEffectClass
    }
}

public struct RequestedHostResource: Codable, Equatable, Sendable {
    public let resourceId: String
    public let type: String
    public let displayName: String
    public let fingerprint: String
    public let state: String

    public init(resourceId: String, type: String, displayName: String, fingerprint: String, state: String) {
        self.resourceId = resourceId
        self.type = type
        self.displayName = displayName
        self.fingerprint = fingerprint
        self.state = state
    }
}

public struct HostPairingClaimRequest: Codable, Equatable, Sendable {
    public let claimSecret: String
    public let publicKeyJwk: P256PublicJWK
    public let protection: HostKeyProtection
    public let platform: String
    public let architecture: String
    public let agentVersion: String
    public let protocolVersion: String
    public let requestedCapabilities: [RequestedHostCapability]
    public let requestedResources: [RequestedHostResource]

    public init(
        claimSecret: String,
        publicKeyJwk: P256PublicJWK,
        protection: HostKeyProtection,
        platform: String,
        architecture: String,
        agentVersion: String,
        protocolVersion: String,
        requestedCapabilities: [RequestedHostCapability],
        requestedResources: [RequestedHostResource]
    ) {
        self.claimSecret = claimSecret
        self.publicKeyJwk = publicKeyJwk
        self.protection = protection
        self.platform = platform
        self.architecture = architecture
        self.agentVersion = agentVersion
        self.protocolVersion = protocolVersion
        self.requestedCapabilities = requestedCapabilities
        self.requestedResources = requestedResources
    }
}

public struct HostPairingClaimResponse: Codable, Equatable, Sendable {
    public let pairingId: String
    public let state: String
    public let expiresAt: String
    public let version: Int64
}

public enum HostOperation: String, Codable, CaseIterable, Sendable {
    case poll
    case leaseAck = "lease-ack"
    case leaseEvents = "lease-events"
    case leaseComplete = "lease-complete"
    case leaseReconcile = "lease-reconcile"
    case leaseArtifact = "lease-artifact"
}

public enum HostAttemptState: String, Codable, Sendable {
    case notStarted = "NOT_STARTED"
    case started = "STARTED"
    case completed = "COMPLETED"
}

public struct HostPollActiveAttempt: Codable, Equatable, Sendable {
    public let leaseId: String
    public let localAttemptId: String
    public let state: HostAttemptState

    public init(leaseId: String, localAttemptId: String, state: HostAttemptState) {
        self.leaseId = leaseId
        self.localAttemptId = localAttemptId
        self.state = state
    }
}

public struct HostPollRequest: Codable, Equatable, Sendable {
    public let maxWaitSeconds: Int
    public let activeAttempt: HostPollActiveAttempt?

    public init(maxWaitSeconds: Int = 25, activeAttempt: HostPollActiveAttempt? = nil) throws {
        guard (1...25).contains(maxWaitSeconds) else { throw HostProtocolError.outOfBounds("maxWaitSeconds") }
        self.maxWaitSeconds = maxWaitSeconds
        self.activeAttempt = activeAttempt
    }
}

public struct HostLeaseResource: Codable, Equatable, Sendable {
    public let resourceId: String
    public let resourceGrantVersion: Int64
    public let accessMode: String
    public let fingerprint: String
}

public struct HostCommandInput: Codable, Equatable, Sendable {
    public let resourceIds: [String]
}

public struct HostCommand: Codable, Equatable, Sendable {
    public let commandId: String
    public let leaseId: String
    public let leaseVersion: Int64
    public let runId: String
    public let schedulerFence: Int64
    public let profileId: String
    public let capabilityId: String
    public let capabilityVersion: String
    public let capabilityGrantVersion: Int64
    public let resources: [HostLeaseResource]
    public let inputHash: String
    public let issuedAt: String
    public let executeUntil: String
    public let input: HostCommandInput
    public let outputLimitBytes: Int
    public let eventLimit: Int

    public func validateProofProfile() throws {
        guard profileId == "host.repo.identity@1",
              capabilityId == "host.repo.identity",
              capabilityVersion == "1",
              commandId == "cmd:\(leaseId)",
              leaseVersion >= 1,
              schedulerFence >= 1,
              capabilityGrantVersion >= 1,
              resources.count == 1,
              resources[0].resourceGrantVersion >= 1,
              resources[0].accessMode == "READ_ONLY",
              input.resourceIds == resources.map(\.resourceId),
              outputLimitBytes == 32 * 1024,
              eventLimit == 50
        else { throw HostProtocolError.unsupportedCommand }
        try HostProtocol.validateIdentifier(leaseId, name: "leaseId")
        try HostProtocol.validateIdentifier(runId, name: "runId")
        try HostProtocol.validateIdentifier(resources[0].resourceId, name: "resourceId")
        try HostProtocol.validateLowerHex(inputHash, length: 64, name: "inputHash")
        try HostProtocol.validateLowerHex(resources[0].fingerprint, length: 64, name: "fingerprint")
          let encodedInput = try HostProtocol.canonicalJSONEncoder().encode(input)
          guard HostProtocol.sha256Hex(encodedInput) == inputHash else { throw HostProtocolError.unsupportedCommand }
    }
}

public struct HostLeaseSnapshot: Codable, Equatable, Sendable {
    public let leaseId: String
    public let leaseVersion: Int64
    public let runId: String
    public let state: String
    public let localAttemptId: String?
    public let resources: [HostLeaseResource]

    public init(leaseId: String, leaseVersion: Int64, runId: String, state: String, localAttemptId: String?, resources: [HostLeaseResource]) {
        self.leaseId = leaseId
        self.leaseVersion = leaseVersion
        self.runId = runId
        self.state = state
        self.localAttemptId = localAttemptId
        self.resources = resources
    }
}

public struct HostPollResponse: Codable, Equatable, Sendable {
    public let serverTime: String
    public let nextPollAfterMs: Int
    public let lease: HostLeaseSnapshot?
    public let command: HostCommand?
}

public struct HostLeaseAckRequest: Codable, Equatable, Sendable {
    public let leaseVersion: Int64
    public let localAttemptId: String
    public let accepted: Bool
    public let rejectionCode: String?
}

public struct HostResourceVersion: Codable, Equatable, Sendable {
    public let version: Int64
}

public struct HostLeaseEventInput: Codable, Equatable, Sendable {
    public let eventId: String
    public let sequence: Int64
    public let type: String
    public let occurredAt: String
    public let summary: String
    public let data: [String: String]
}

public struct HostLeaseEventsRequest: Codable, Equatable, Sendable {
    public let leaseVersion: Int64
    public let localAttemptId: String
    public let events: [HostLeaseEventInput]
}

public struct HostLeaseCompleteRequest: Codable, Equatable, Sendable {
    public let leaseVersion: Int64
    public let outcome: String
    public let output: String?
    public let outputSha256: String?
    public let truncated: Bool
    public let localAttemptId: String
}

public struct HostLeaseReconcileRequest: Codable, Equatable, Sendable {
    public let leaseVersion: Int64
    public let localAttemptId: String
    public let observedState: HostAttemptState
    public let outputSha256: String?
}

public enum HostLeaseReconcileResolution: String, Codable, Equatable, Sendable {
    case resume = "RESUME"
    case requeued = "REQUEUED"
    case reconciliationRequired = "RECONCILIATION_REQUIRED"
}

public struct HostLeaseReconcileResponse: Codable, Equatable, Sendable {
    public let resolution: HostLeaseReconcileResolution
    public let lease: HostLeaseSnapshot?

    public init(resolution: HostLeaseReconcileResolution, lease: HostLeaseSnapshot?) {
        self.resolution = resolution
        self.lease = lease
    }
}

public struct HostLeaseArtifactRequest: Codable, Equatable, Sendable {
    public let leaseVersion: Int64
    public let localAttemptId: String
    public let artifactId: String
    public let kind: String
    public let mediaType: String
    public let summary: String
    public let declaredSize: Int
    public let declaredSha256: String
    public let retention: String
    public let textContent: String
}

public struct IgnoredHostResponse: Decodable, Sendable {
    public init(from decoder: Decoder) throws {
        _ = try decoder.container(keyedBy: IgnoredCodingKey.self)
    }

    private struct IgnoredCodingKey: CodingKey {
        var stringValue: String
        var intValue: Int?
        init?(stringValue: String) { self.stringValue = stringValue }
        init?(intValue: Int) { self.stringValue = String(intValue); self.intValue = intValue }
    }
}

public struct RepositoryIdentity: Codable, Equatable, Sendable {
    public let branch: String?
    public let commit: String
    public let resourceFingerprint: String

    public init(branch: String?, commit: String, resourceFingerprint: String) {
        self.branch = branch
        self.commit = commit
        self.resourceFingerprint = resourceFingerprint
    }
}