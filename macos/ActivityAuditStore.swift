import Foundation
import CryptoKit

struct ActivityAuditEvent: Codable, Equatable {
    let id: UUID
    let time: Date
    let tool: String
    let workspace: String?
    let client: String?
    let result: String
    let durationMs: Int
    let responseBytes: Int
}

/// Local-only bounded audit; never records request arguments, response text,
/// credentials, shell contents or client-supplied display names.
final class ActivityAuditStore {
    private let lock = NSLock()
    private let directory: URL
    static let segmentLimit = 1_000_000

    static func directory(for profile: String) -> URL {
        let hash = SHA256.hash(data: Data(profile.utf8)).map { String(format: "%02x", $0) }.joined()
        return CommandReceiptLedger.applicationStoreURL().deletingLastPathComponent()
            .appendingPathComponent("activity-audit-v1", isDirectory: true)
            .appendingPathComponent(hash, isDirectory: true)
    }

    init(directory: URL) throws {
        self.directory = directory
        try CommandReceiptLedger.ensurePrivateDirectory(directory.deletingLastPathComponent())
        try CommandReceiptLedger.ensurePrivateDirectory(directory)
        for name in ["current.jsonl", "previous.jsonl"] {
            try CommandReceiptLedger.rejectSymbolicLink(at: directory.appendingPathComponent(name))
        }
    }

    func record(tool: String, workspace: String?, client: String?, result: String,
                durationMs: Int, responseBytes: Int) throws {
        let event = ActivityAuditEvent(id: UUID(), time: Date(), tool: tool, workspace: workspace,
            client: client, result: result, durationMs: max(0, durationMs), responseBytes: max(0, responseBytes))
        var data = try JSONEncoder().encode(event)
        data.append(10)
        guard data.count <= 2048 else { throw CommandReceiptLedger.LedgerError.invalidStore }
        lock.lock(); defer { lock.unlock() }
        let active = directory.appendingPathComponent("current.jsonl")
        let previous = directory.appendingPathComponent("previous.jsonl")
        try CommandReceiptLedger.rejectSymbolicLink(at: active)
        try CommandReceiptLedger.rejectSymbolicLink(at: previous)
        let fm = FileManager.default
        if fm.fileExists(atPath: active.path) {
            let size = try active.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max
            if size > Self.segmentLimit - data.count {
                if fm.fileExists(atPath: previous.path) { try fm.removeItem(at: previous) }
                try fm.moveItem(at: active, to: previous)
            }
        }
        if !fm.fileExists(atPath: active.path) {
            guard fm.createFile(atPath: active.path, contents: nil,
                attributes: [.posixPermissions: NSNumber(value: 0o600)]) else {
                throw CommandReceiptLedger.LedgerError.invalidStore
            }
        }
        let file = try FileHandle(forWritingTo: active)
        defer { try? file.close() }
        try file.seekToEnd()
        try file.write(contentsOf: data)
        try file.synchronize()
    }

    static func read(directory: URL) throws -> [ActivityAuditEvent] {
        try CommandReceiptLedger.rejectSymbolicLink(at: directory.deletingLastPathComponent())
        try CommandReceiptLedger.rejectSymbolicLink(at: directory)
        var records: [ActivityAuditEvent] = []
        for name in ["previous.jsonl", "current.jsonl"] {
            let url = directory.appendingPathComponent(name)
            try CommandReceiptLedger.rejectSymbolicLink(at: url)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            do {
                guard (try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max) <= segmentLimit else {
                    throw CommandReceiptLedger.LedgerError.invalidStore
                }
                let data = try Data(contentsOf: url)
                // An interrupted final write or concurrent append may leave one incomplete line.
                for line in data.split(separator: 10) {
                    if let item = try? JSONDecoder().decode(ActivityAuditEvent.self, from: Data(line)) {
                        records.append(item)
                    }
                }
            } catch let error as CocoaError where error.code == .fileReadNoSuchFile { continue }
        }
        var seen = Set<UUID>()
        return records.filter { seen.insert($0.id).inserted }.sorted { $0.time > $1.time }
    }
}
