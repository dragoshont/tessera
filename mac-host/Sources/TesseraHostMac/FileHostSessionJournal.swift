import Darwin
import Foundation
import TesseraHostCore

public actor FileHostSessionJournal: HostSessionJournal {
    private let fileURL: URL
    private let maximumBytes = 2 * 1024 * 1024

    public init(fileURL: URL) {
        self.fileURL = fileURL
    }

    public func load() async throws -> HostSessionState {
        let fd = Darwin.open(fileURL.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        if fd < 0 {
            if errno == ENOENT { return HostSessionState() }
            throw HostChannelError.invalidJournal
        }
        defer { Darwin.close(fd) }
        var item = stat()
        guard fstat(fd, &item) == 0,
              item.st_uid == geteuid(),
              item.st_mode & S_IFMT == S_IFREG,
              item.st_size >= 0,
              item.st_size <= maximumBytes,
              item.st_mode & 0o077 == 0
        else { throw HostChannelError.invalidJournal }
        var data = Data(count: Int(item.st_size))
        let count = data.withUnsafeMutableBytes { buffer in
            pread(fd, buffer.baseAddress, buffer.count, 0)
        }
        guard count == item.st_size else { throw HostChannelError.invalidJournal }
        let state = try JSONDecoder().decode(HostSessionState.self, from: data)
        guard state.schemaVersion == 1, state.lastAcceptedSequence >= 0 else {
            throw HostChannelError.invalidJournal
        }
        return state
    }

    public func save(_ state: HostSessionState) async throws {
        guard state.schemaVersion == 1, state.lastAcceptedSequence >= 0 else {
            throw HostChannelError.invalidJournal
        }
        let directory = fileURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        chmod(directory.path, 0o700)
        let data = try HostProtocol.canonicalJSONEncoder().encode(state)
        guard data.count <= maximumBytes else { throw HostChannelError.invalidJournal }
        let temporary = directory.appendingPathComponent(".\(fileURL.lastPathComponent).\(UUID().uuidString.lowercased()).tmp")
        let fd = Darwin.open(temporary.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw HostChannelError.invalidJournal }
        do {
            try data.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    let written = Darwin.write(fd, buffer.baseAddress?.advanced(by: offset), buffer.count - offset)
                    if written < 0 && errno == EINTR { continue }
                    guard written > 0 else { throw HostChannelError.invalidJournal }
                    offset += written
                }
            }
            guard fsync(fd) == 0 else { throw HostChannelError.invalidJournal }
            Darwin.close(fd)
            guard rename(temporary.path, fileURL.path) == 0 else { throw HostChannelError.invalidJournal }
            chmod(fileURL.path, 0o600)
            let directoryFD = Darwin.open(directory.path, O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            if directoryFD >= 0 { fsync(directoryFD); Darwin.close(directoryFD) }
        } catch {
            Darwin.close(fd)
            unlink(temporary.path)
            throw error
        }
    }
}