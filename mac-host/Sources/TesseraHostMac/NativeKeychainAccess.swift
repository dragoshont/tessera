import Foundation
import Security

enum NativeKeychainAccess {
    private static let suffix = "ro.hont.tessera.host.shared"

    static let group: String? = {
        guard let executableURL = Bundle.main.executableURL else { return nil }
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(executableURL as CFURL, [], &code) == errSecSuccess,
              let code
        else { return nil }
        var information: CFDictionary?
        guard SecCodeCopySigningInformation(code, [], &information) == errSecSuccess,
              let values = information as? [CFString: Any],
              let team = values[kSecCodeInfoTeamIdentifier] as? String,
              !team.isEmpty
        else { return nil }
        return "\(team).\(suffix)"
    }()

    static func apply(to query: inout [CFString: Any]) {
        if let group { query[kSecAttrAccessGroup] = group }
    }
}