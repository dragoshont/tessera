import Foundation
import Security
import TesseraHostCore

public struct HostPairingInput: Codable, Equatable, Sendable {
    public let serverURL: String
    public let pairingId: String
    public let claimSecret: String
    public let resourceId: String
    public let replaceIdentity: Bool

    public init(serverURL: String, pairingId: String, claimSecret: String, resourceId: String, replaceIdentity: Bool = false) {
        self.serverURL = serverURL
        self.pairingId = pairingId
        self.claimSecret = claimSecret
        self.resourceId = resourceId
        self.replaceIdentity = replaceIdentity
    }
}

public struct HostPairingResult: Codable, Equatable, Sendable {
    public let pairingId: String
    public let state: String
    public let expiresAt: String
    public let version: Int64
    public let confirmationCode: String
    public let protection: HostKeyProtection
    public let resourceId: String
    public let resourceFingerprint: String
}

public protocol HostPairingClaiming: Sendable {
    func claim(pairingId: String, idempotencyKey: String, request: HostPairingClaimRequest) async throws -> HostPairingClaimResponse
}

public struct PendingHostPairingClaim: Codable, Equatable, Sendable {
    public let serverURL: String
    public let pairingId: String
    public let idempotencyKey: String
    public let resourceId: String
    public let resourceFingerprint: String
    public let request: HostPairingClaimRequest
}

public actor HostPairingAttemptStore {
    private let service: String
    private let account = "pending"

    public init(service: String = "ro.hont.tessera.host.pairing-attempt") {
        self.service = service
    }

    public func save(_ attempt: PendingHostPairingClaim) throws {
        let data = try HostProtocol.canonicalJSONEncoder().encode(attempt)
        var key: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
        ]
        NativeKeychainAccess.apply(to: &key)
        let values: [CFString: Any] = [
            kSecValueData: data,
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecAttrSynchronizable: false,
        ]
        let update = SecItemUpdate(key as CFDictionary, values as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw RepositoryStoreError.keychain(update) }
        var create = key
        values.forEach { create[$0.key] = $0.value }
        let status = SecItemAdd(create as CFDictionary, nil)
        guard status == errSecSuccess else { throw RepositoryStoreError.keychain(status) }
    }

    public func load() throws -> PendingHostPairingClaim? {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        NativeKeychainAccess.apply(to: &query)
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw RepositoryStoreError.keychain(status)
        }
        return try JSONDecoder().decode(PendingHostPairingClaim.self, from: data)
    }

    public func delete() throws {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
        ]
        NativeKeychainAccess.apply(to: &query)
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw RepositoryStoreError.keychain(status)
        }
    }
}

public final class HostPairingHTTPClient: NSObject, HostPairingClaiming, URLSessionTaskDelegate, @unchecked Sendable {
    private let descriptor: HostServerDescriptor
    private let session: URLSession

    public init(descriptor: HostServerDescriptor, configuration: URLSessionConfiguration = .ephemeral) {
        self.descriptor = descriptor
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 25
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        self.session = URLSession(configuration: configuration, delegate: nil, delegateQueue: nil)
        super.init()
    }

    public func claim(pairingId: String, idempotencyKey: String, request: HostPairingClaimRequest) async throws -> HostPairingClaimResponse {
        try HostProtocol.validateIdentifier(pairingId, name: "pairingId")
        try HostProtocol.validateIdentifier(idempotencyKey, name: "idempotencyKey")
        let path = "/api/v1/host-pairings/\(pairingId)/claim"
        guard let url = URL(string: path, relativeTo: descriptor.baseURL)?.absoluteURL else {
            throw HostChannelError.invalidPath
        }
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.httpBody = try HostProtocol.canonicalJSONEncoder().encode(request)
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
          urlRequest.setValue(idempotencyKey, forHTTPHeaderField: "Idempotency-Key")
          let (data, response) = try await BoundedHTTP.read(
            session: session,
            request: urlRequest,
            delegate: self,
            maximumBytes: 64 * 1024
          )
          guard let http = response as? HTTPURLResponse,
              http.url?.host == descriptor.baseURL.host,
              http.url?.scheme == "https",
              BoundedHTTP.hasIdentityEncoding(http),
              BoundedHTTP.baseContentType(http) == "application/json",
              http.statusCode == 202
        else { throw HostChannelError.invalidResponse }
        return try JSONDecoder().decode(HostPairingClaimResponse.self, from: data)
    }

    public func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) { completionHandler(nil) }
}

public actor HostPairingCoordinator {
    private static let schemaHash = HostProtocol.sha256Hex(Data("{branch:string|null,commit:hex40|hex64,resourceFingerprint:hex64}".utf8))
    private let keys: SecurityDeviceKeyStore
    private let resources: KeychainRepositoryStore
    private let attempts: HostPairingAttemptStore
    private let keyPolicy: DeviceKeyPolicy
    private let makeClient: @Sendable (HostServerDescriptor) -> any HostPairingClaiming

    public init(
        keys: SecurityDeviceKeyStore,
        resources: KeychainRepositoryStore,
        attempts: HostPairingAttemptStore = .init(),
        keyPolicy: DeviceKeyPolicy = .preferSecureEnclave,
        makeClient: @escaping @Sendable (HostServerDescriptor) -> any HostPairingClaiming = {
            HostPairingHTTPClient(descriptor: $0)
        }
    ) {
        self.keys = keys
        self.resources = resources
        self.attempts = attempts
        self.keyPolicy = keyPolicy
        self.makeClient = makeClient
    }

    public func claim(input: HostPairingInput) async throws -> HostPairingResult {
        _ = try HostProtocol.decodeCanonicalBase64URL(input.claimSecret, expectedBytes: 32)
        try HostProtocol.validateIdentifier(input.pairingId, name: "pairingId")
        guard let serverURL = URL(string: input.serverURL) else { throw HostChannelError.invalidServerDescriptor }
        let descriptor = try HostServerDescriptor(baseURL: serverURL)
        let idempotencyMaterial = Data("\(input.pairingId)\u{0}\(input.claimSecret)".utf8)
        let idempotencyKey = "claim-\(HostProtocol.sha256Hex(idempotencyMaterial).prefix(56))"
        let key: DeviceSigningKey
        let request: HostPairingClaimRequest
        let resourceFingerprint: String
        if let pending = try await attempts.load() {
            guard pending.serverURL == descriptor.baseURL.absoluteString,
                  pending.pairingId == input.pairingId,
                  pending.idempotencyKey == idempotencyKey,
                  pending.resourceId == input.resourceId
            else { throw HostChannelError.terminal("pairing-attempt-pending") }
            key = try await keys.loadOrCreate(policy: keyPolicy)
            guard key.publicJWK == pending.request.publicKeyJwk,
                  key.protection == pending.request.protection
            else { throw HostChannelError.invalidJournal }
            request = pending.request
            resourceFingerprint = pending.resourceFingerprint
        } else {
            let resource = try await resources.load(resourceId: input.resourceId)
            if input.replaceIdentity { try await keys.delete() }
            key = try await keys.loadOrCreate(policy: keyPolicy)
            request = HostPairingClaimRequest(
                claimSecret: input.claimSecret,
                publicKeyJwk: key.publicJWK,
                protection: key.protection,
                platform: "macOS",
                architecture: Self.architecture(),
                agentVersion: "1.0.0",
                protocolVersion: "1",
                requestedCapabilities: [
                    .init(capabilityId: "host.repo.identity", capabilityVersion: "1", schemaHash: Self.schemaHash, sideEffectClass: "READ_ONLY"),
                ],
                requestedResources: [
                    .init(resourceId: resource.resourceId, type: "REPOSITORY", displayName: resource.displayName, fingerprint: resource.fingerprint, state: "AVAILABLE"),
                ]
            )
            resourceFingerprint = resource.fingerprint
            try await attempts.save(.init(
                serverURL: descriptor.baseURL.absoluteString,
                pairingId: input.pairingId,
                idempotencyKey: idempotencyKey,
                resourceId: resource.resourceId,
                resourceFingerprint: resource.fingerprint,
                request: request
            ))
        }
        let response = try await makeClient(descriptor).claim(
            pairingId: input.pairingId,
            idempotencyKey: idempotencyKey,
            request: request
        )
        guard response.pairingId == input.pairingId,
              response.state == "CLAIMED",
              response.version >= 1,
              Self.parseDate(response.expiresAt) != nil
        else { throw HostChannelError.invalidResponse }
        try await attempts.delete()
        return HostPairingResult(
            pairingId: response.pairingId,
            state: response.state,
            expiresAt: response.expiresAt,
            version: response.version,
            confirmationCode: try HostProtocol.pairingConfirmationCode(pairingId: input.pairingId, publicJWK: key.publicJWK),
            protection: key.protection,
            resourceId: input.resourceId,
            resourceFingerprint: resourceFingerprint
        )
    }

    private static func architecture() -> String {
        var value = utsname()
        uname(&value)
        return withUnsafePointer(to: &value.machine) {
            $0.withMemoryRebound(to: CChar.self, capacity: 1) { String(cString: $0) }
        }
    }

    private static func parseDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: value) ?? ISO8601DateFormatter().date(from: value)
    }
}