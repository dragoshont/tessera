import AppKit
import Foundation
import ServiceManagement
import TesseraHostCore
import TesseraHostMac

private let loginItemIdentifier = "ro.hont.tessera.host"
private let maximumInputBytes = 64 * 1024

private struct ConfigureInput: Codable { let serverURL: String; let hostId: String }
private struct GrantInput: Codable { let resourceId: String; let displayName: String }
private struct GrantOutput: Codable { let resourceId: String; let displayName: String; let fingerprint: String }
private struct StatusOutput: Codable { let available: Bool; let state: String; let bundleIdentifier: String }

@main
enum TesseraHostControl {
    static func main() async {
        do {
            guard CommandLine.arguments.count == 2 else { throw HostChannelError.invalidPath }
            switch CommandLine.arguments[1] {
            case "status":
                try output(StatusOutput(available: true, state: serviceStatus(), bundleIdentifier: loginItemIdentifier))
            case "register":
                try SMAppService.loginItem(identifier: loginItemIdentifier).register()
                try output(StatusOutput(available: true, state: serviceStatus(), bundleIdentifier: loginItemIdentifier))
            case "unregister":
                try await SMAppService.loginItem(identifier: loginItemIdentifier).unregister()
                try output(StatusOutput(available: true, state: serviceStatus(), bundleIdentifier: loginItemIdentifier))
            case "configure":
                let input: ConfigureInput = try readInput()
                try await HostConfigurationStore().save(.init(serverURL: input.serverURL, hostId: input.hostId))
                try output(StatusOutput(available: true, state: serviceStatus(), bundleIdentifier: loginItemIdentifier))
            case "select-repository":
                let input: GrantInput = try readInput()
                let panel = NSOpenPanel()
                panel.canChooseDirectories = true
                panel.canChooseFiles = false
                panel.allowsMultipleSelection = false
                panel.prompt = "Grant read-only repository access"
                guard panel.runModal() == .OK, let url = panel.url else { throw CancellationError() }
                let record = try DescriptorRepositoryReader().grant(path: url.path, resourceId: input.resourceId, displayName: input.displayName)
                try await KeychainRepositoryStore().save(record)
                try output(GrantOutput(resourceId: record.resourceId, displayName: record.displayName, fingerprint: record.fingerprint))
            case "pair":
                let input: HostPairingInput = try readInput()
                let result = try await HostPairingCoordinator(keys: .init(), resources: .init()).claim(input: input)
                let alert = NSAlert()
                alert.messageText = "Confirm this code in Tessera"
                alert.informativeText = result.confirmationCode
                alert.addButton(withTitle: "OK")
                alert.runModal()
                try output(result)
            default:
                throw HostChannelError.invalidPath
            }
        } catch {
            FileHandle.standardError.write(Data("Tessera Host control failed.\n".utf8))
            Foundation.exit(1)
        }
    }

    private static func serviceStatus() -> String {
        switch SMAppService.loginItem(identifier: loginItemIdentifier).status {
        case .enabled: "ENABLED"
        case .requiresApproval: "REQUIRES_APPROVAL"
        case .notFound: "NOT_FOUND"
        case .notRegistered: "DISABLED"
        @unknown default: "UNAVAILABLE"
        }
    }

    private static func readInput<T: Decodable>() throws -> T {
        var data = Data()
        while true {
            let chunk = try FileHandle.standardInput.read(upToCount: min(4096, maximumInputBytes + 1 - data.count)) ?? Data()
            if chunk.isEmpty { break }
            data.append(chunk)
            guard data.count <= maximumInputBytes else { throw HostChannelError.responseTooLarge }
        }
        guard !data.isEmpty else { throw HostChannelError.invalidResponse }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private static func output<T: Encodable>(_ value: T) throws {
        var data = try HostProtocol.canonicalJSONEncoder().encode(value)
        guard data.count <= maximumInputBytes else { throw HostChannelError.responseTooLarge }
        data.append(0x0a)
        FileHandle.standardOutput.write(data)
    }
}