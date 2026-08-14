import Foundation
import Security
import TesseraHostCore

public enum SecurityDeviceKeyError: Error, Equatable {
    case accessControl(OSStatus)
    case keyGeneration(OSStatus)
    case keyLookup(OSStatus)
    case publicKeyUnavailable
    case publicKeyEncoding
    case signing(OSStatus)
    case signatureEncoding
    case protectionMetadata(OSStatus)
}

public enum DeviceKeyPolicy: Sendable {
    case preferSecureEnclave
    case keychainOnly
}

public final class DeviceSigningKey: HostRequestSigner, @unchecked Sendable {
    public let protection: HostKeyProtection
    public let publicJWK: P256PublicJWK
    private let privateKey: SecKey

    init(privateKey: SecKey, protection: HostKeyProtection) throws {
        guard let publicKey = SecKeyCopyPublicKey(privateKey) else {
            throw SecurityDeviceKeyError.publicKeyUnavailable
        }
        var error: Unmanaged<CFError>?
        guard let external = SecKeyCopyExternalRepresentation(publicKey, &error) as Data? else {
            throw SecurityDeviceKeyError.publicKeyEncoding
        }
        guard external.count == 65, external[0] == 0x04 else {
            throw SecurityDeviceKeyError.publicKeyEncoding
        }
        self.privateKey = privateKey
        self.protection = protection
        self.publicJWK = P256PublicJWK(
            x: HostProtocol.base64URL(external.subdata(in: 1..<33)),
            y: HostProtocol.base64URL(external.subdata(in: 33..<65))
        )
    }

    public func signCanonicalRequest(_ canonicalRequest: Data) throws -> Data {
        var error: Unmanaged<CFError>?
        guard let der = SecKeyCreateSignature(
            privateKey,
            .ecdsaSignatureMessageX962SHA256,
            canonicalRequest as CFData,
            &error
        ) as Data? else {
            let status = (error?.takeRetainedValue() as Error?)?._code ?? Int(errSecInternalError)
            throw SecurityDeviceKeyError.signing(OSStatus(status))
        }
        return try P256SignatureCodec.derToLowSRaw(der)
    }

    func publicKeyForTesting() -> SecKey? { SecKeyCopyPublicKey(privateKey) }
}

public actor SecurityDeviceKeyStore {
    private let tag: Data
    private let metadataService: String

    public init(tag: String = "ro.hont.tessera.host.device-key") {
        self.tag = Data(tag.utf8)
        self.metadataService = "\(tag).protection"
    }

    public func loadOrCreate(policy: DeviceKeyPolicy = .preferSecureEnclave) throws -> DeviceSigningKey {
        if let key = try loadExisting() { return key }
        if policy == .preferSecureEnclave, let key = try? create(secureEnclave: true) { return key }
        return try create(secureEnclave: false)
    }

    public func delete() throws {
        var keyQuery: [CFString: Any] = [
            kSecClass: kSecClassKey,
            kSecAttrApplicationTag: tag,
            kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
        ]
        NativeKeychainAccess.apply(to: &keyQuery)
        let keyStatus = SecItemDelete(keyQuery as CFDictionary)
        if keyStatus != errSecSuccess, keyStatus != errSecItemNotFound {
            throw SecurityDeviceKeyError.keyLookup(keyStatus)
        }
        var metadataQuery: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: metadataService,
        ]
        NativeKeychainAccess.apply(to: &metadataQuery)
        let metadataStatus = SecItemDelete(metadataQuery as CFDictionary)
        if metadataStatus != errSecSuccess, metadataStatus != errSecItemNotFound {
            throw SecurityDeviceKeyError.protectionMetadata(metadataStatus)
        }
    }

    private func loadExisting() throws -> DeviceSigningKey? {
        var query: [CFString: Any] = [
            kSecClass: kSecClassKey,
            kSecAttrApplicationTag: tag,
            kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecReturnRef: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        NativeKeychainAccess.apply(to: &query)
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let key = result else {
            throw SecurityDeviceKeyError.keyLookup(status)
        }
        let protection = try readProtection()
        return try DeviceSigningKey(privateKey: key as! SecKey, protection: protection)
    }

    private func create(secureEnclave: Bool) throws -> DeviceSigningKey {
        var privateAttributes: [CFString: Any] = [
            kSecAttrIsPermanent: true,
            kSecAttrApplicationTag: tag,
        ]
        NativeKeychainAccess.apply(to: &privateAttributes)
        if secureEnclave {
            var accessError: Unmanaged<CFError>?
            guard let access = SecAccessControlCreateWithFlags(
                nil,
                kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
                .privateKeyUsage,
                &accessError
            ) else {
                let status = (accessError?.takeRetainedValue() as Error?)?._code ?? Int(errSecInternalError)
                throw SecurityDeviceKeyError.accessControl(OSStatus(status))
            }
            privateAttributes[kSecAttrAccessControl] = access
        } else {
            privateAttributes[kSecAttrAccessible] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        }
        var attributes: [CFString: Any] = [
            kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits: 256,
            kSecPrivateKeyAttrs: privateAttributes,
        ]
        if secureEnclave { attributes[kSecAttrTokenID] = kSecAttrTokenIDSecureEnclave }
        var error: Unmanaged<CFError>?
        guard let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else {
            let status = (error?.takeRetainedValue() as Error?)?._code ?? Int(errSecInternalError)
            throw SecurityDeviceKeyError.keyGeneration(OSStatus(status))
        }
        let protection: HostKeyProtection = secureEnclave ? .secureEnclave : .keychainThisDeviceOnly
        do {
            try writeProtection(protection)
        } catch {
            var query: [CFString: Any] = [
                kSecClass: kSecClassKey,
                kSecAttrApplicationTag: tag,
                kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            ]
            NativeKeychainAccess.apply(to: &query)
            SecItemDelete(query as CFDictionary)
            throw error
        }
        return try DeviceSigningKey(privateKey: key, protection: protection)
    }

    private func readProtection() throws -> HostKeyProtection {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: metadataService,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        NativeKeychainAccess.apply(to: &query)
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess,
              let data = result as? Data,
              let value = String(data: data, encoding: .utf8),
              let protection = HostKeyProtection(rawValue: value)
        else { throw SecurityDeviceKeyError.protectionMetadata(status) }
        return protection
    }

    private func writeProtection(_ protection: HostKeyProtection) throws {
        var query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: metadataService,
            kSecValueData: Data(protection.rawValue.utf8),
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        NativeKeychainAccess.apply(to: &query)
        var deleteQuery: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: metadataService,
        ]
        NativeKeychainAccess.apply(to: &deleteQuery)
        SecItemDelete(deleteQuery as CFDictionary)
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw SecurityDeviceKeyError.protectionMetadata(status) }
    }
}

enum P256SignatureCodec {
    private static let order = Array(Data(hex: "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"))
    private static let halfOrder = Array(Data(hex: "7FFFFFFF800000007FFFFFFFFFFFFFFFDE737D56D38BCF4279DCE5617E3192A8"))

    static func derToLowSRaw(_ der: Data) throws -> Data {
        var cursor = 0
        guard readByte(der, &cursor) == 0x30 else { throw SecurityDeviceKeyError.signatureEncoding }
        let sequenceLength = try readLength(der, &cursor)
        guard sequenceLength == der.count - cursor else { throw SecurityDeviceKeyError.signatureEncoding }
        let r = try readInteger(der, &cursor)
        var s = try readInteger(der, &cursor)
        guard cursor == der.count else { throw SecurityDeviceKeyError.signatureEncoding }
        if compare(s, halfOrder) == .orderedDescending { s = subtract(order, s) }
        return Data(r + s)
    }

    static func rawToDER(_ raw: Data) throws -> Data {
        guard raw.count == 64 else { throw SecurityDeviceKeyError.signatureEncoding }
        let r = encodeInteger(Array(raw[..<32]))
        let s = encodeInteger(Array(raw[32...]))
        let payload = [0x02, UInt8(r.count)] + r + [0x02, UInt8(s.count)] + s
        return Data([0x30, UInt8(payload.count)] + payload)
    }

    private static func readInteger(_ data: Data, _ cursor: inout Int) throws -> [UInt8] {
        guard readByte(data, &cursor) == 0x02 else { throw SecurityDeviceKeyError.signatureEncoding }
        let length = try readLength(data, &cursor)
        guard length > 0, cursor + length <= data.count else { throw SecurityDeviceKeyError.signatureEncoding }
        var value = Array(data[cursor..<(cursor + length)])
        cursor += length
        if value.count == 33 {
            guard value[0] == 0, value[1] & 0x80 != 0 else {
                throw SecurityDeviceKeyError.signatureEncoding
            }
            value.removeFirst()
        } else if value.first.map({ $0 & 0x80 != 0 }) ?? true {
            throw SecurityDeviceKeyError.signatureEncoding
        }
        guard value.count <= 32, value.contains(where: { $0 != 0 }) else {
            throw SecurityDeviceKeyError.signatureEncoding
        }
        return Array(repeating: 0, count: 32 - value.count) + value
    }

    private static func readLength(_ data: Data, _ cursor: inout Int) throws -> Int {
        guard cursor < data.count else { throw SecurityDeviceKeyError.signatureEncoding }
        let value = Int(data[cursor]); cursor += 1
        guard value < 128 else { throw SecurityDeviceKeyError.signatureEncoding }
        return value
    }

    private static func readByte(_ data: Data, _ cursor: inout Int) -> UInt8? {
        guard cursor < data.count else { return nil }
        defer { cursor += 1 }
        return data[cursor]
    }

    private static func encodeInteger(_ value: [UInt8]) -> [UInt8] {
        var stripped = Array(value.drop(while: { $0 == 0 }))
        if stripped.isEmpty { stripped = [0] }
        if stripped[0] & 0x80 != 0 { stripped.insert(0, at: 0) }
        return stripped
    }

    private static func compare(_ lhs: [UInt8], _ rhs: [UInt8]) -> ComparisonResult {
        for (left, right) in zip(lhs, rhs) where left != right {
            return left < right ? .orderedAscending : .orderedDescending
        }
        return .orderedSame
    }

    private static func subtract(_ lhs: [UInt8], _ rhs: [UInt8]) -> [UInt8] {
        var result = Array(repeating: UInt8(0), count: lhs.count)
        var borrow = 0
        for index in stride(from: lhs.count - 1, through: 0, by: -1) {
            var value = Int(lhs[index]) - Int(rhs[index]) - borrow
            if value < 0 { value += 256; borrow = 1 } else { borrow = 0 }
            result[index] = UInt8(value)
        }
        return result
    }
}

private extension Data {
    init(hex: String) {
        self.init(stride(from: 0, to: hex.count, by: 2).map { offset in
            let start = hex.index(hex.startIndex, offsetBy: offset)
            return UInt8(hex[start..<hex.index(start, offsetBy: 2)], radix: 16)!
        })
    }
}