import Foundation
import XCTest
@testable import TesseraHostCore
@testable import TesseraHostMac

final class DescriptorRepositoryTests: XCTestCase {
    private var directory: URL!
    private let reader = DescriptorRepositoryReader()
    private let commit = String(repeating: "a", count: 40)

    override func setUpWithError() throws {
        let base = try XCTUnwrap(realpath(FileManager.default.temporaryDirectory.path, nil))
        defer { free(base) }
        directory = URL(fileURLWithPath: String(cString: base), isDirectory: true)
            .appendingPathComponent("tessera-host-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    func testReadsDescriptorBoundBranchAndCommit() throws {
        let repository = try makeRepository()
        let record = try reader.grant(path: repository.path, resourceId: "repo-main", displayName: "Tessera")
        let identity = try reader.readIdentity(record: record)
        XCTAssertEqual(identity.branch, "main")
        XCTAssertEqual(identity.commit, commit)
        XCTAssertEqual(identity.resourceFingerprint, record.fingerprint)
    }

    func testReadsCanonicalDetachedHead() throws {
        let repository = try makeRepository(head: "\(commit)\n")
        let record = try reader.grant(path: repository.path, resourceId: "repo-main", displayName: "Tessera")
        let identity = try reader.readIdentity(record: record)
        XCTAssertNil(identity.branch)
        XCTAssertEqual(identity.commit, commit)
    }

    func testRejectsSymlinkPathComponent() throws {
        let repository = try makeRepository()
        let link = directory.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: repository)
        XCTAssertThrowsError(try reader.grant(path: link.path, resourceId: "repo-main", displayName: "Tessera"))
    }

    func testRejectsGitfileCommondirAndAlternates() throws {
        let gitfileRepository = directory.appendingPathComponent("gitfile")
        try FileManager.default.createDirectory(at: gitfileRepository, withIntermediateDirectories: true)
        try Data("gitdir: elsewhere\n".utf8).write(to: gitfileRepository.appendingPathComponent(".git"))
        XCTAssertThrowsError(try reader.grant(path: gitfileRepository.path, resourceId: "repo-one", displayName: "One"))

        let commonRepository = try makeRepository(name: "common")
        try Data("../common\n".utf8).write(to: commonRepository.appendingPathComponent(".git/commondir"))
        XCTAssertThrowsError(try reader.grant(path: commonRepository.path, resourceId: "repo-two", displayName: "Two"))

        let alternateRepository = try makeRepository(name: "alternate")
        let info = alternateRepository.appendingPathComponent(".git/objects/info")
        try FileManager.default.createDirectory(at: info, withIntermediateDirectories: true)
        try Data("elsewhere\n".utf8).write(to: info.appendingPathComponent("alternates"))
        XCTAssertThrowsError(try reader.grant(path: alternateRepository.path, resourceId: "repo-three", displayName: "Three"))
    }

    func testRejectsOversizedHeadAndRefTraversal() throws {
        let oversized = try makeRepository(name: "oversized", head: String(repeating: "a", count: 257))
        let oversizedRecord = try reader.grant(path: oversized.path, resourceId: "repo-one", displayName: "One")
        XCTAssertThrowsError(try reader.readIdentity(record: oversizedRecord))

        let traversal = try makeRepository(name: "traversal", head: "ref: refs/heads/../secret\n")
        let traversalRecord = try reader.grant(path: traversal.path, resourceId: "repo-two", displayName: "Two")
        XCTAssertThrowsError(try reader.readIdentity(record: traversalRecord))
    }

    func testRejectsHeadAndFinalRefSymlinks() throws {
        let headRepository = try makeRepository(name: "head-link")
        let head = headRepository.appendingPathComponent(".git/HEAD")
        try FileManager.default.removeItem(at: head)
        try FileManager.default.createSymbolicLink(atPath: head.path, withDestinationPath: "/dev/null")
        let headRecord = try reader.grant(path: headRepository.path, resourceId: "repo-one", displayName: "One")
        XCTAssertThrowsError(try reader.readIdentity(record: headRecord))

        let refRepository = try makeRepository(name: "ref-link")
        let ref = refRepository.appendingPathComponent(".git/refs/heads/main")
        try FileManager.default.removeItem(at: ref)
        try FileManager.default.createSymbolicLink(atPath: ref.path, withDestinationPath: "/dev/null")
        let refRecord = try reader.grant(path: refRepository.path, resourceId: "repo-two", displayName: "Two")
        XCTAssertThrowsError(try reader.readIdentity(record: refRecord))
    }

    func testRejectsPathReplacementAfterGrant() throws {
        let repository = try makeRepository(name: "replace")
        let record = try reader.grant(path: repository.path, resourceId: "repo-main", displayName: "Tessera")
        let moved = directory.appendingPathComponent("moved")
        try FileManager.default.moveItem(at: repository, to: moved)
        _ = try makeRepository(name: "replace")
        XCTAssertThrowsError(try reader.readIdentity(record: record))
    }

    private func makeRepository(name: String = "repository", head: String = "ref: refs/heads/main\n") throws -> URL {
        let repository = directory.appendingPathComponent(name)
        let heads = repository.appendingPathComponent(".git/refs/heads")
        try FileManager.default.createDirectory(at: heads, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: repository.appendingPathComponent(".git/objects"), withIntermediateDirectories: true)
        try Data(head.utf8).write(to: repository.appendingPathComponent(".git/HEAD"))
        try Data("\(commit)\n".utf8).write(to: heads.appendingPathComponent("main"))
        return repository
    }
}