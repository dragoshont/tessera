import CryptoKit
import Foundation

public enum HostProtocolError: Error, Equatable, Sendable {
    case invalidIdentifier(String)
    case invalidLowerHex(String)
    case outOfBounds(String)
    case invalidSignature
    case unsupportedCommand
}

public protocol HostRequestSigner: Sendable {
    func signCanonicalRequest(_ canonicalRequest: Data) throws -> Data
}

public struct PreparedHostRequest: Equatable, Sendable {
    public let body: Data
    public let canonicalRequest: Data
    public let requestHash: String
    public let headers: [String: String]

    public init(body: Data, canonicalRequest: Data, requestHash: String, headers: [String: String]) {
        self.body = body
        self.canonicalRequest = canonicalRequest
        self.requestHash = requestHash
        self.headers = headers
    }
}

public enum HostProtocol {
    public static let canonicalPrefix = "TESSERA-HOST-V1"
    public static let protocolVersion: Int64 = 1
    public static let keyVersion: Int64 = 1

    public static func prepareSignedRequest<T: Encodable>(
        operation: HostOperation,
        targetId: String,
        hostId: String,
        messageId: String,
        sequence: Int64,
        timestamp: Int64,
        body: T,
        signer: any HostRequestSigner,
        encoder: JSONEncoder = canonicalJSONEncoder()
    ) throws -> PreparedHostRequest {
        let bodyData = try encoder.encode(body)
        return try prepareSignedRequest(
            operation: operation,
            targetId: targetId,
            hostId: hostId,
            messageId: messageId,
            sequence: sequence,
            timestamp: timestamp,
            bodyData: bodyData,
            signer: signer
        )
    }

    public static func prepareSignedRequest(
        operation: HostOperation,
        targetId: String,
        hostId: String,
        messageId: String,
        sequence: Int64,
        timestamp: Int64,
        bodyData: Data,
        signer: any HostRequestSigner
    ) throws -> PreparedHostRequest {
        try validateIdentifier(hostId, name: "hostId")
        try validateIdentifier(messageId, name: "messageId")
        if operation == .poll {
            guard targetId == "-" else { throw HostProtocolError.invalidIdentifier("targetId") }
        } else {
            try validateIdentifier(targetId, name: "targetId")
        }
        guard sequence >= 1 else { throw HostProtocolError.outOfBounds("sequence") }
        guard timestamp >= 0, timestamp <= 253_402_300_799 else { throw HostProtocolError.outOfBounds("timestamp") }

        let bodyHash = sha256Hex(bodyData)
        let canonical = canonicalSigningInput(
            operation: operation,
            targetId: targetId,
            hostId: hostId,
            messageId: messageId,
            sequence: sequence,
            timestamp: timestamp,
            bodySha256: bodyHash
        )
        let signature = try signer.signCanonicalRequest(canonical)
        guard signature.count == 64 else { throw HostProtocolError.invalidSignature }
        let requestHash = sha256Hex(canonical)
        return PreparedHostRequest(
            body: bodyData,
            canonicalRequest: canonical,
            requestHash: requestHash,
            headers: [
                "X-Tessera-Host-Id": hostId,
                "X-Tessera-Host-Protocol-Version": String(protocolVersion),
                "X-Tessera-Host-Key-Version": String(keyVersion),
                "X-Tessera-Host-Operation": operation.rawValue,
                "X-Tessera-Host-Target-Id": targetId,
                "X-Tessera-Host-Message-Id": messageId,
                "X-Tessera-Host-Sequence": String(sequence),
                "X-Tessera-Host-Timestamp": String(timestamp),
                "X-Tessera-Host-Body-SHA256": bodyHash,
                "X-Tessera-Host-Signature": base64URL(signature),
            ]
        )
    }

    public static func canonicalSigningInput(
        operation: HostOperation,
        targetId: String,
        hostId: String,
        messageId: String,
        sequence: Int64,
        timestamp: Int64,
        bodySha256: String
    ) -> Data {
        Data([
            canonicalPrefix,
            "POST",
            operation.rawValue,
            targetId,
            hostId,
            String(protocolVersion),
            String(keyVersion),
            messageId,
            String(sequence),
            String(timestamp),
            bodySha256,
        ].joined(separator: "\n").utf8)
    }

    public static func validateIdentifier(_ value: String, name: String) throws {
        guard (1...64).contains(value.count),
              value.utf8.first.map({ ($0 >= 97 && $0 <= 122) || ($0 >= 48 && $0 <= 57) }) == true,
              value.utf8.allSatisfy({ ($0 >= 97 && $0 <= 122) || ($0 >= 48 && $0 <= 57) || $0 == 45 })
        else { throw HostProtocolError.invalidIdentifier(name) }
    }

    public static func validateLowerHex(_ value: String, length: Int, name: String) throws {
        guard value.utf8.count == length,
              value.utf8.allSatisfy({ ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102) })
        else { throw HostProtocolError.invalidLowerHex(name) }
    }

    public static func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    public static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    public static func decodeCanonicalBase64URL(_ value: String, expectedBytes: Int) throws -> Data {
        guard !value.contains("="), value.utf8.allSatisfy({
            ($0 >= 65 && $0 <= 90) || ($0 >= 97 && $0 <= 122) ||
            ($0 >= 48 && $0 <= 57) || $0 == 45 || $0 == 95
        }) else { throw HostProtocolError.outOfBounds("base64url") }
        var encoded = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        encoded += String(repeating: "=", count: (4 - encoded.count % 4) % 4)
        guard let data = Data(base64Encoded: encoded),
              data.count == expectedBytes,
              base64URL(data) == value
        else { throw HostProtocolError.outOfBounds("base64url") }
        return data
    }

    public static func pairingConfirmationCode(pairingId: String, publicJWK: P256PublicJWK) throws -> String {
        try validateIdentifier(pairingId, name: "pairingId")
        let canonical = try canonicalJSONEncoder().encode(publicJWK)
        let thumbprint = Data(SHA256.hash(data: canonical))
        var material = Data(pairingId.utf8)
        material.append(0)
        material.append(thumbprint)
        let digest = SHA256.hash(data: material)
        let value = digest.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) } % 1_000_000
        return String(format: "%06u", value)
    }

    public static func canonicalJSONEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return encoder
    }
}