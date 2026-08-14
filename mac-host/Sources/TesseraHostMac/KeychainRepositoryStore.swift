import Foundation
import Security
import TesseraHostCore

public enum RepositoryStoreError: Error, Equatable, Sendable {
    case notFound
    case keychain(OSStatus)
    case invalidRecord
    case fingerprintMismatch
}

public actor KeychainRepositoryStore {
    private let service: String

    public init(service: String = "ro.hont.tessera.host.repositories") {
        self.service = service
    }

    public func save(_ record: RepositoryResourceRecord) throws {
        try HostProtocol.validateIdentifier(record.resourceId, name: "resourceId")
        let data = try HostProtocol.canonicalJSONEncoder().encode(record)
        var key: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: record.resourceId,
        ]
        NativeKeychainAccess.apply(to: &key)
        let attributes: [CFString: Any] = [
            kSecValueData: data,
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecAttrSynchronizable: false,
        ]
        let update = SecItemUpdate(key as CFDictionary, attributes as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw RepositoryStoreError.keychain(update) }
        var create = key
        for (name, value) in attributes { create[name] = value }
        let status = SecItemAdd(create as CFDictionary, nil)
        guard status == errSecSuccess else { throw RepositoryStoreError.keychain(status) }
    }

    public func load(resourceId: String) throws -> RepositoryResourceRecord {
        try HostProtocol.validateIdentifier(resourceId, name: "resourceId")
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: resourceId,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        NativeKeychainAccess.apply(to: &query)
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { throw RepositoryStoreError.notFound }
        guard status == errSecSuccess, let data = result as? Data else {
            throw RepositoryStoreError.keychain(status)
        }
        do {
            let record = try JSONDecoder().decode(RepositoryResourceRecord.self, from: data)
            guard record.resourceId == resourceId else { throw RepositoryStoreError.invalidRecord }
            return record
        } catch let error as RepositoryStoreError {
            throw error
        } catch {
            throw RepositoryStoreError.invalidRecord
        }
    }

    public func delete(resourceId: String) throws {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: resourceId,
        ]
        NativeKeychainAccess.apply(to: &query)
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw RepositoryStoreError.keychain(status)
        }
    }
}

public actor StoredRepositoryIdentityProvider: HostRepositoryIdentityProvider {
    private let store: KeychainRepositoryStore
    private let reader: DescriptorRepositoryReader

    public init(store: KeychainRepositoryStore, reader: DescriptorRepositoryReader = .init()) {
        self.store = store
        self.reader = reader
    }

    public func identity(for resource: HostLeaseResource) async throws -> RepositoryIdentity {
        let record = try await store.load(resourceId: resource.resourceId)
        guard record.fingerprint == resource.fingerprint else { throw RepositoryStoreError.fingerprintMismatch }
        return try reader.readIdentity(record: record)
    }
}