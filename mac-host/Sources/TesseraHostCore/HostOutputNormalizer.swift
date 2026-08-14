import Foundation

public struct NormalizedHostOutput: Equatable, Sendable {
    public let text: String
    public let redacted: Bool
    public let truncated: Bool
    public let sha256: String
    public let sizeBytes: Int
}

public enum HostOutputNormalizationError: Error, Equatable, Sendable {
    case invalidUTF8
    case invalidLimit
}

public enum HostOutputNormalizer {
    private static let pathRoots = [
        "/Users/", "/home/", "/Volumes/", "/System/", "/Network/", "/Developer/",
        "/private/", "/tmp/", "/var/", "/opt/", "/usr/", "/bin/", "/sbin/",
        "/dev/", "/etc/", "/Applications/", "/Library/", "/workspace/", "/root/",
    ]

    public static func normalize(_ input: Data, limitBytes: Int) throws -> NormalizedHostOutput {
        guard (1...(256 * 1024)).contains(limitBytes) else { throw HostOutputNormalizationError.invalidLimit }
        guard var value = String(data: input, encoding: .utf8) else { throw HostOutputNormalizationError.invalidUTF8 }
        value = value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        value.unicodeScalars.removeAll { scalar in
            scalar != "\n" && scalar != "\t" && CharacterSet.controlCharacters.contains(scalar)
        }
        var redacted = false
        value = redactPEM(value, redacted: &redacted)
        value = redactSensitiveAssignments(value, redacted: &redacted)
        value = redactAbsolutePaths(value, redacted: &redacted)

        let bytes = Data(value.utf8)
        let truncated = bytes.count > limitBytes
        var length = min(bytes.count, limitBytes)
        var persisted = bytes.prefix(length)
        while String(data: persisted, encoding: .utf8) == nil, length > 0 {
            length -= 1
            persisted = bytes.prefix(length)
        }
        let data = Data(persisted)
        guard let text = String(data: data, encoding: .utf8) else { throw HostOutputNormalizationError.invalidUTF8 }
        return NormalizedHostOutput(
            text: text,
            redacted: redacted,
            truncated: truncated,
            sha256: HostProtocol.sha256Hex(data),
            sizeBytes: data.count
        )
    }

    private static func redactPEM(_ input: String, redacted: inout Bool) -> String {
        var value = input
        while let begin = value.range(of: "-----BEGIN ") {
            let tail = value[begin.lowerBound...]
            let end = tail.range(of: "-----END ")?.upperBound ?? value.endIndex
            value.replaceSubrange(begin.lowerBound..<end, with: "[REDACTED]")
            redacted = true
        }
        return value
    }

    private static func redactSensitiveAssignments(_ input: String, redacted: inout Bool) -> String {
        let sensitive = ["authorization", "apikey", "token", "password", "secret", "signature", "privatekey", "publickey", "path", "command", "argv", "environment", "env"]
        return input.split(separator: "\n", omittingEmptySubsequences: false).map { rawLine in
            var line = String(rawLine)
            guard let delimiter = line.firstIndex(where: { $0 == ":" || $0 == "=" }) else { return line }
            let key = line[..<delimiter].lowercased().unicodeScalars.filter(CharacterSet.alphanumerics.contains)
            guard sensitive.contains(String(key)) else { return line }
            let prefix = line[...delimiter]
            line = "\(prefix) [REDACTED]"
            redacted = true
            return line
        }.joined(separator: "\n")
    }

    private static func redactAbsolutePaths(_ input: String, redacted: inout Bool) -> String {
        var value = input
        for root in pathRoots {
            var search = value.startIndex
            while let range = value.range(of: root, range: search..<value.endIndex) {
                var end = range.upperBound
                while end < value.endIndex,
                      !value[end].isWhitespace,
                      ![",", ";", "\"", "'"].contains(value[end]) {
                    end = value.index(after: end)
                }
                value.replaceSubrange(range.lowerBound..<end, with: "[REDACTED]")
                search = value.index(range.lowerBound, offsetBy: "[REDACTED]".count)
                redacted = true
            }
        }
        return value
    }
}