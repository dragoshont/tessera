import SwiftUI
import TesseraHostCore
import TesseraHostMac

@MainActor
final class HostStatusModel: ObservableObject {
    @Published var summary = "Not configured"
    @Published var running = false
    private var task: Task<Void, Never>?

    func start() {
        guard task == nil else { return }
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let configuration = try await HostConfigurationStore().load()
                guard let serverURL = URL(string: configuration.serverURL) else {
                    throw HostChannelError.invalidServerDescriptor
                }
                let descriptor = try HostServerDescriptor(baseURL: serverURL)
                let key = try await SecurityDeviceKeyStore().loadOrCreate()
                let stateURL = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                    .appendingPathComponent("TesseraHost", isDirectory: true)
                    .appendingPathComponent("state.json")
                let journal = FileHostSessionJournal(fileURL: stateURL)
                let channel = try ReliableHostChannel(
                    hostId: configuration.hostId,
                    signer: key,
                    journal: journal,
                    executor: URLSessionHostHTTPExecutor(descriptor: descriptor)
                )
                let agent = HostAgent(
                    channel: channel,
                    journal: journal,
                    repository: StoredRepositoryIdentityProvider(store: .init())
                )
                running = true
                summary = "Connected as \(configuration.hostId)"
                var failureCount = 0
                let backoff = try ExponentialBackoff()
                while !Task.isCancelled {
                    do {
                        try await agent.runOneCycle()
                        failureCount = 0
                    } catch HostChannelError.unconsumedResponse(_, let code) {
                        if HostChannelError.canRefreshTimestampAfterUnconsumedProblem(code) {
                            try? await channel.clearUnconsumedPending()
                        } else if HostChannelError.requiresOperatorForUnconsumedProblem(code) {
                            try? await channel.clearUnconsumedPending()
                            summary = code == "host_revoked" ? "Revoked in Tessera" : "Host identity needs attention"
                            running = false
                            return
                        }
                        failureCount += 1
                        summary = "Waiting to reconnect"
                    } catch HostChannelError.invalidResponse, HostChannelError.terminal {
                        summary = "Host protocol needs attention"
                        running = false
                        return
                    } catch {
                        failureCount += 1
                        summary = "Waiting to reconnect"
                    }
                    let delay = backoff.delayMilliseconds(attempt: failureCount, jitterUnit: Double.random(in: 0...1))
                    try? await Task.sleep(for: .milliseconds(delay))
                }
            } catch {
                summary = "Pair this Mac in Tessera"
                running = false
            }
        }
    }

    func stop() { task?.cancel(); task = nil; running = false }
}

@main
struct TesseraHostLoginItemApp: App {
    @StateObject private var model = HostStatusModel()

    var body: some Scene {
        MenuBarExtra("Tessera Host", systemImage: model.running ? "checkmark.shield" : "shield.slash") {
            Text(model.summary)
            Divider()
            Button("Quit Tessera Host") { NSApplication.shared.terminate(nil) }
        }
        .menuBarExtraStyle(.menu)
        .onChange(of: model.running, initial: true) { _, _ in model.start() }
    }
}