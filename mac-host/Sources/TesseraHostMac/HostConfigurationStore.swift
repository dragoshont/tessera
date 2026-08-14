import Foundation
import Security
import TesseraHostCore

public struct HostRuntimeConfiguration: Codable, Equatable, Sendable {
    public let serverURL: String
    public let hostId: String

    public init(serverURL: String, hostId: String) {
        self.serverURL = serverURL
        self.hostId = hostId
    }
}

public actor HostConfigurationStore {
    private let service: String
    private let account = "runtime"

    public init(service: String = "ro.hont.tessera.host.configuration") {
        self.service = service
    }

    public func save(_ configuration: HostRuntimeConfiguration) throws {
        guard let url = URL(string: configuration.serverURL) else { throw HostChannelError.invalidServerDescriptor }
        _ = try HostServerDescriptor(baseURL: url)
        try HostProtocol.validateIdentifier(configuration.hostId, name: "hostId")
        let data = try HostProtocol.canonicalJSONEncoder().encode(configuration)
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

    public func load() throws -> HostRuntimeConfiguration {
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
        if status == errSecItemNotFound { throw RepositoryStoreError.notFound }
        guard status == errSecSuccess, let data = result as? Data else { throw RepositoryStoreError.keychain(status) }
        return try JSONDecoder().decode(HostRuntimeConfiguration.self, from: data)
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