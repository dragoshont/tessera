import CryptoKit
import Darwin
import Foundation
import TesseraHostCore

public enum RepositoryProfileError: Error, Equatable, Sendable {
    case invalidPath
    case invalidResourceId
    case openFailed(Int32)
    case metadataFailed(Int32)
    case wrongOwner
    case wrongType(String, UInt16)
    case identityChanged
    case volumeIdentityUnavailable
    case gitfileUnsupported
    case linkedRepositoryUnsupported
    case alternatesUnsupported
    case contentOverflow
    case contentChanged
    case invalidHead
    case invalidRef
}

public struct RepositoryResourceRecord: Codable, Equatable, Sendable {
    public let resourceId: String
    public let displayName: String
    public let displayPath: String
    public let volumeUUID: String
    public let rootDevice: UInt64
    public let rootInode: UInt64
    public let gitDevice: UInt64
    public let gitInode: UInt64
    public let fingerprint: String

    public init(
        resourceId: String,
        displayName: String,
        displayPath: String,
        volumeUUID: String,
        rootDevice: UInt64,
        rootInode: UInt64,
        gitDevice: UInt64,
        gitInode: UInt64,
        fingerprint: String
    ) {
        self.resourceId = resourceId
        self.displayName = displayName
        self.displayPath = displayPath
        self.volumeUUID = volumeUUID
        self.rootDevice = rootDevice
        self.rootInode = rootInode
        self.gitDevice = gitDevice
        self.gitInode = gitInode
        self.fingerprint = fingerprint
    }
}

public struct DescriptorRepositoryReader: Sendable {
    public init() {}

    public func grant(path: String, resourceId: String, displayName: String) throws -> RepositoryResourceRecord {
        try HostProtocol.validateIdentifier(resourceId, name: "resourceId")
        guard !displayName.isEmpty, displayName.utf8.count <= 128 else {
            throw RepositoryProfileError.invalidResourceId
        }
        let opened = try openRepository(path: path)
        defer { opened.close() }
        let fingerprint = Self.fingerprint(
            volumeUUID: opened.volumeUUID,
            rootDevice: opened.rootIdentity.device,
            rootInode: opened.rootIdentity.inode
        )
        return RepositoryResourceRecord(
            resourceId: resourceId,
            displayName: displayName,
            displayPath: path,
            volumeUUID: opened.volumeUUID,
            rootDevice: opened.rootIdentity.device,
            rootInode: opened.rootIdentity.inode,
            gitDevice: opened.gitIdentity.device,
            gitInode: opened.gitIdentity.inode,
            fingerprint: fingerprint
        )
    }

    public func readIdentity(record: RepositoryResourceRecord) throws -> RepositoryIdentity {
        let opened = try openRepository(path: record.displayPath)
        defer { opened.close() }
        guard opened.volumeUUID == record.volumeUUID,
              opened.rootIdentity.device == record.rootDevice,
              opened.rootIdentity.inode == record.rootInode,
              opened.gitIdentity.device == record.gitDevice,
              opened.gitIdentity.inode == record.gitInode,
              Self.fingerprint(
                volumeUUID: opened.volumeUUID,
                rootDevice: opened.rootIdentity.device,
                rootInode: opened.rootIdentity.inode
              ) == record.fingerprint
        else { throw RepositoryProfileError.identityChanged }

        let headBytes = try readStableFile(parentFD: opened.gitFD.value, name: "HEAD")
        let head = try parseSingleLine(headBytes, error: .invalidHead)
        let branch: String?
        let commit: String
        if Self.isObjectID(head) {
            branch = nil
            commit = head
        } else {
            let prefix = "ref: refs/heads/"
            guard head.hasPrefix(prefix) else { throw RepositoryProfileError.invalidHead }
            let branchValue = String(head.dropFirst(prefix.count))
            let components = try validateRef(branchValue)
            branch = branchValue
            let refs = try openDirectory(parentFD: opened.gitFD.value, name: "refs")
            defer { refs.close() }
            let heads = try openDirectory(parentFD: refs.value, name: "heads")
            defer { heads.close() }
            var parent = heads
            var ownedParents: [ManagedFileDescriptor] = []
            for component in components.dropLast() {
                let next = try openDirectory(parentFD: parent.value, name: component)
                ownedParents.append(next)
                parent = next
            }
            defer { ownedParents.forEach { $0.close() } }
            let refBytes = try readStableFile(parentFD: parent.value, name: components.last!)
            let objectID = try parseSingleLine(refBytes, error: .invalidRef)
            guard Self.isObjectID(objectID) else { throw RepositoryProfileError.invalidRef }
            commit = objectID
        }

        let finalRoot = try metadata(fd: opened.rootFD.value)
        let finalGit = try metadata(fd: opened.gitFD.value)
        guard finalRoot == opened.rootIdentity, finalGit == opened.gitIdentity else {
            throw RepositoryProfileError.identityChanged
        }
        return RepositoryIdentity(branch: branch, commit: commit, resourceFingerprint: record.fingerprint)
    }

    private func openRepository(path: String) throws -> OpenedRepository {
        let rootFD = try walkAbsoluteDirectory(path)
        do {
            let rootIdentity = try metadata(fd: rootFD.value)
            let volumeUUID = try volumeUUID(fd: rootFD.value)
            var gitStat = stat()
            guard fstatat(rootFD.value, ".git", &gitStat, AT_SYMLINK_NOFOLLOW) == 0 else {
                throw RepositoryProfileError.metadataFailed(errno)
            }
            guard Self.isDirectory(gitStat) else { throw RepositoryProfileError.gitfileUnsupported }
            try requireOwner(gitStat)
            let gitFD = try openDirectory(parentFD: rootFD.value, name: ".git")
            do {
                let gitIdentity = try metadata(fd: gitFD.value)
                if try exists(parentFD: gitFD.value, name: "commondir") {
                    throw RepositoryProfileError.linkedRepositoryUnsupported
                }
                try rejectAlternates(gitFD: gitFD.value)
                return OpenedRepository(
                    rootFD: rootFD,
                    gitFD: gitFD,
                    rootIdentity: rootIdentity,
                    gitIdentity: gitIdentity,
                    volumeUUID: volumeUUID
                )
            } catch {
                gitFD.close()
                throw error
            }
        } catch {
            rootFD.close()
            throw error
        }
    }

    private func walkAbsoluteDirectory(_ path: String) throws -> ManagedFileDescriptor {
        guard path.hasPrefix("/"), !path.utf8.contains(0) else { throw RepositoryProfileError.invalidPath }
        let components = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard !components.isEmpty,
              components.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." && !$0.contains("/") })
        else { throw RepositoryProfileError.invalidPath }
        let initial = Darwin.open("/", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        guard initial >= 0 else { throw RepositoryProfileError.openFailed(errno) }
        var current = ManagedFileDescriptor(initial)
        do {
            for (index, component) in components.enumerated() {
                let next = try openDirectory(
                    parentFD: current.value,
                    name: component,
                    allowRootOwner: index < components.count - 1
                )
                current.close()
                current = next
            }
            return current
        } catch {
            current.close()
            throw error
        }
    }

    private func openDirectory(parentFD: Int32, name: String, allowRootOwner: Bool = false) throws -> ManagedFileDescriptor {
        var item = stat()
        guard fstatat(parentFD, name, &item, AT_SYMLINK_NOFOLLOW) == 0 else {
            throw RepositoryProfileError.metadataFailed(errno)
        }
        guard Self.isDirectory(item) else { throw RepositoryProfileError.wrongType(name, item.st_mode) }
        try requireOwner(item, allowRootOwner: allowRootOwner)
        let fd = openat(parentFD, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw RepositoryProfileError.openFailed(errno) }
        let descriptor = ManagedFileDescriptor(fd)
        do {
            let opened = try metadata(fd: fd)
            guard opened.device == UInt64(item.st_dev), opened.inode == UInt64(item.st_ino) else {
                throw RepositoryProfileError.identityChanged
            }
            return descriptor
        } catch {
            descriptor.close()
            throw error
        }
    }

    private func readStableFile(parentFD: Int32, name: String) throws -> Data {
        var pathMetadata = stat()
        guard fstatat(parentFD, name, &pathMetadata, AT_SYMLINK_NOFOLLOW) == 0 else {
            throw RepositoryProfileError.metadataFailed(errno)
        }
        guard Self.isRegularFile(pathMetadata) else { throw RepositoryProfileError.wrongType(name, pathMetadata.st_mode) }
        try requireOwner(pathMetadata)
        guard pathMetadata.st_size <= 256 else { throw RepositoryProfileError.contentOverflow }
        let fd = openat(parentFD, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw RepositoryProfileError.openFailed(errno) }
        let descriptor = ManagedFileDescriptor(fd)
        defer { descriptor.close() }
        var before = stat()
        guard fstat(fd, &before) == 0 else { throw RepositoryProfileError.metadataFailed(errno) }
        guard Self.sameFile(pathMetadata, before) else { throw RepositoryProfileError.identityChanged }
        var bytes = [UInt8](repeating: 0, count: 257)
        let count = pread(fd, &bytes, bytes.count, 0)
        guard count >= 0 else { throw RepositoryProfileError.openFailed(errno) }
        guard count < 257 else { throw RepositoryProfileError.contentOverflow }
        var after = stat()
        guard fstat(fd, &after) == 0 else { throw RepositoryProfileError.metadataFailed(errno) }
        guard Self.sameStableFile(before, after), count == before.st_size else {
            throw RepositoryProfileError.contentChanged
        }
        return Data(bytes.prefix(Int(count)))
    }

    private func rejectAlternates(gitFD: Int32) throws {
        guard try exists(parentFD: gitFD, name: "objects") else { return }
        let objects = try openDirectory(parentFD: gitFD, name: "objects")
        defer { objects.close() }
        guard try exists(parentFD: objects.value, name: "info") else { return }
        let info = try openDirectory(parentFD: objects.value, name: "info")
        defer { info.close() }
        if try exists(parentFD: info.value, name: "alternates") {
            throw RepositoryProfileError.alternatesUnsupported
        }
    }

    private func exists(parentFD: Int32, name: String) throws -> Bool {
        var item = stat()
        if fstatat(parentFD, name, &item, AT_SYMLINK_NOFOLLOW) == 0 { return true }
        if errno == ENOENT { return false }
        throw RepositoryProfileError.metadataFailed(errno)
    }

    private func metadata(fd: Int32) throws -> FileIdentity {
        var item = stat()
        guard fstat(fd, &item) == 0 else { throw RepositoryProfileError.metadataFailed(errno) }
        return FileIdentity(device: UInt64(item.st_dev), inode: UInt64(item.st_ino))
    }

    private func requireOwner(_ item: stat, allowRootOwner: Bool = false) throws {
        guard item.st_uid == geteuid() || allowRootOwner && item.st_uid == 0 else {
            throw RepositoryProfileError.wrongOwner
        }
    }

    private func volumeUUID(fd: Int32) throws -> String {
        var attributes = attrlist()
        attributes.bitmapcount = UInt16(ATTR_BIT_MAP_COUNT)
        attributes.volattr = UInt32(ATTR_VOL_UUID)
        var buffer = VolumeUUIDBuffer()
        let status = withUnsafeMutablePointer(to: &attributes) { listPointer in
            withUnsafeMutablePointer(to: &buffer) { bufferPointer in
                fgetattrlist(fd, listPointer, bufferPointer, MemoryLayout<VolumeUUIDBuffer>.size, 0)
            }
        }
        guard status == 0 else { throw RepositoryProfileError.volumeIdentityUnavailable }
        return withUnsafePointer(to: &buffer.uuid) { pointer in
            pointer.withMemoryRebound(to: UInt8.self, capacity: 16) { bytes in
                let data = Data(bytes: bytes, count: 16)
                return data.map { String(format: "%02x", $0) }.joined()
            }
        }
    }

    private func parseSingleLine(_ data: Data, error: RepositoryProfileError) throws -> String {
        guard !data.isEmpty, data.allSatisfy({ $0 <= 0x7f && $0 != 0 }) else { throw error }
        var bytes = Array(data)
        if bytes.last == 0x0a { bytes.removeLast() }
        if bytes.last == 0x0d { bytes.removeLast() }
        guard !bytes.isEmpty, !bytes.contains(0x0a), !bytes.contains(0x0d),
              let value = String(bytes: bytes, encoding: .utf8)
        else { throw error }
        return value
    }

    private func validateRef(_ value: String) throws -> [String] {
        guard value.utf8.count <= 200 else { throw RepositoryProfileError.invalidRef }
        let components = value.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard !components.isEmpty, components.allSatisfy({ component in
            guard (1...64).contains(component.utf8.count),
                  component != ".", component != "..",
                  !component.hasPrefix("."), !component.hasSuffix("."),
                  !component.hasSuffix(".lock"), !component.contains("..")
            else { return false }
            return component.utf8.allSatisfy {
                ($0 >= 65 && $0 <= 90) || ($0 >= 97 && $0 <= 122) ||
                ($0 >= 48 && $0 <= 57) || $0 == 45 || $0 == 46 || $0 == 95
            }
        }) else { throw RepositoryProfileError.invalidRef }
        return components
    }

    private static func fingerprint(volumeUUID: String, rootDevice: UInt64, rootInode: UInt64) -> String {
        HostProtocol.sha256Hex(Data("\(volumeUUID)\n\(rootDevice)\n\(rootInode)".utf8))
    }

    private static func isObjectID(_ value: String) -> Bool {
        (value.utf8.count == 40 || value.utf8.count == 64) && value.utf8.allSatisfy {
            ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102)
        }
    }

    private static func isDirectory(_ item: stat) -> Bool { item.st_mode & S_IFMT == S_IFDIR }
    private static func isRegularFile(_ item: stat) -> Bool { item.st_mode & S_IFMT == S_IFREG }
    private static func sameFile(_ lhs: stat, _ rhs: stat) -> Bool {
        lhs.st_dev == rhs.st_dev && lhs.st_ino == rhs.st_ino && lhs.st_uid == rhs.st_uid &&
        lhs.st_mode == rhs.st_mode && lhs.st_size == rhs.st_size
    }
    private static func sameStableFile(_ lhs: stat, _ rhs: stat) -> Bool {
        sameFile(lhs, rhs) && lhs.st_mtimespec.tv_sec == rhs.st_mtimespec.tv_sec &&
        lhs.st_mtimespec.tv_nsec == rhs.st_mtimespec.tv_nsec &&
        lhs.st_ctimespec.tv_sec == rhs.st_ctimespec.tv_sec &&
        lhs.st_ctimespec.tv_nsec == rhs.st_ctimespec.tv_nsec
    }
}

private struct FileIdentity: Equatable {
    let device: UInt64
    let inode: UInt64
}

private final class ManagedFileDescriptor {
    private(set) var value: Int32
    init(_ value: Int32) { self.value = value }
    func close() {
        if value >= 0 { Darwin.close(value); value = -1 }
    }
    deinit { close() }
}

private struct OpenedRepository {
    let rootFD: ManagedFileDescriptor
    let gitFD: ManagedFileDescriptor
    let rootIdentity: FileIdentity
    let gitIdentity: FileIdentity
    let volumeUUID: String
    func close() { gitFD.close(); rootFD.close() }
}

private struct VolumeUUIDBuffer {
    var length: UInt32 = UInt32(MemoryLayout<VolumeUUIDBuffer>.size)
    var uuid: uuid_t = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
}