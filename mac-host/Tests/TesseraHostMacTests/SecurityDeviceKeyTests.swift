import Foundation
import Security
import XCTest
@testable import TesseraHostCore
@testable import TesseraHostMac

final class SecurityDeviceKeyTests: XCTestCase {
    private var store: SecurityDeviceKeyStore!
    private var tag: String!

    override func setUp() async throws {
        tag = "ro.hont.tessera.tests.\(UUID().uuidString.lowercased())"
        store = SecurityDeviceKeyStore(tag: tag)
    }

    override func tearDown() async throws {
        try? await store.delete()
    }

    func testSoftwareKeychainKeySignsLowSJoseAndVerifies() async throws {
        let key = try await store.loadOrCreate(policy: .keychainOnly)
        XCTAssertEqual(key.protection, .keychainThisDeviceOnly)
        XCTAssertEqual(key.publicJWK.kty, "EC")
        XCTAssertEqual(key.publicJWK.crv, "P-256")
        XCTAssertFalse(key.publicJWK.x.contains("="))
        XCTAssertFalse(key.publicJWK.y.contains("="))

        let message = Data("signed-host-request".utf8)
        let raw = try key.signCanonicalRequest(message)
        XCTAssertEqual(raw.count, 64)
        let halfOrder = Data(hex: "7FFFFFFF800000007FFFFFFFFFFFFFFFDE737D56D38BCF4279DCE5617E3192A8")
        let s = raw.subdata(in: 32..<64)
        XCTAssertTrue(s == halfOrder || s.lexicographicallyPrecedes(halfOrder))

        let der = try P256SignatureCodec.rawToDER(raw)
        var error: Unmanaged<CFError>?
        XCTAssertTrue(SecKeyVerifySignature(
            try XCTUnwrap(key.publicKeyForTesting()),
            .ecdsaSignatureMessageX962SHA256,
            message as CFData,
            der as CFData,
            &error
        ))
    }

    func testKeychainFallbackPublicSurfaceOmitsPrivateMaterialAndReloads() async throws {
        let created = try await store.loadOrCreate(policy: .keychainOnly)
        let reloaded = try await store.loadOrCreate(policy: .keychainOnly)
        XCTAssertEqual(created.publicJWK, reloaded.publicJWK)
        let publicJSON = try JSONEncoder().encode(created.publicJWK)
        XCTAssertFalse(String(decoding: publicJSON, as: UTF8.self).contains("\"d\""))

        var result: CFTypeRef?
        let status = SecItemCopyMatching([
            kSecClass: kSecClassKey,
            kSecAttrApplicationTag: Data(tag.utf8),
            kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecReturnAttributes: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ] as CFDictionary, &result)
        XCTAssertEqual(status, errSecSuccess)
        let attributes = try XCTUnwrap(result as? [CFString: Any])
        XCTAssertNil(attributes[kSecValueData])
        XCTAssertNil(UserDefaults.standard.data(forKey: tag))
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