import Foundation
import CryptoKit

/// Local, bounded command history. Separate from the replay-prevention ledger.
/// Only the desktop UI reads this store; it is never published as an MCP tool.
struct CommandHistoryRecord: Codable, Equatable {
    let id: UUID
    let workspaceID: String
    let workspaceName: String
    let cwd: String
    let kind: String
    let requestID: String?
    let clientID: String?
    let startedAt: Date
    var updatedAt: Date
    var state: String
    var exitCode: Int32?
    let command: String?
    var output: String?
    var truncated: Bool

    var isTerminal: Bool { state != "running" && state != "queued" }
}

final class CommandHistoryStore {
    static let maxCompleted = 200
    static let retentionDays = 30
    static let contentLimit = 65_536
    private static let states = Set(["queued", "running", "exited", "cancelled", "timed_out", "launch_failed", "interrupted"])
    let captureContent: Bool
    private let directory: URL
    private let lock = NSLock()
    private var recordsByID: [UUID: CommandHistoryRecord] = [:]

    static func directory(for profile: String) -> URL {
        let hash = SHA256.hash(data: Data(profile.utf8)).map { String(format: "%02x", $0) }.joined()
        return CommandReceiptLedger.applicationStoreURL().deletingLastPathComponent()
            .appendingPathComponent("command-history-v1", isDirectory: true)
            .appendingPathComponent(hash, isDirectory: true)
    }

    init(directory: URL, captureContent: Bool) throws {
        self.directory = directory
        self.captureContent = captureContent
        try CommandReceiptLedger.ensurePrivateDirectory(directory.deletingLastPathComponent())
        try CommandReceiptLedger.ensurePrivateDirectory(directory)
        for record in try Self.read(directory: directory) {
            guard recordsByID[record.id] == nil else { throw CommandReceiptLedger.LedgerError.invalidStore }
            recordsByID[record.id] = record
        }
        // Called only after the owning runtime acquires its profile lock.
        for var record in recordsByID.values where !record.isTerminal {
            record.state = "interrupted"
            record.updatedAt = Date()
            try persist(record)
            recordsByID[record.id] = record
        }
        try prune()
    }

    @discardableResult
    func begin(id: UUID = UUID(), workspaceID: String, workspaceName: String,
               cwd: String, kind: String, requestID: String?, clientID: String?,
               command: String, queued: Bool = false) throws -> UUID {
        lock.lock(); defer { lock.unlock() }
        guard recordsByID[id] == nil else { throw CommandReceiptLedger.LedgerError.duplicateRequest }
        let now = Date()
        let text = captureContent ? Self.bounded(command) : nil
        let record = CommandHistoryRecord(id: id, workspaceID: workspaceID,
            workspaceName: workspaceName, cwd: cwd, kind: kind, requestID: requestID,
            clientID: clientID == "__local_admin__" ? nil : clientID,
            startedAt: now, updatedAt: now, state: queued ? "queued" : "running", exitCode: nil,
            command: text?.0, output: nil, truncated: text?.1 ?? false)
        try persist(record)
        recordsByID[id] = record
        return id
    }

    func markRunning(id: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        guard var record = recordsByID[id], record.state == "queued" else { throw CommandReceiptLedger.LedgerError.missingReceipt }
        record.state = "running"; record.updatedAt = Date()
        try persist(record); recordsByID[id] = record
    }

    func finish(id: UUID, state: String, exitCode: Int32?, output: String = "",
                truncated: Bool = false) throws {
        lock.lock(); defer { lock.unlock() }
        guard Self.states.contains(state), state != "running", state != "queued",
              var record = recordsByID[id], !record.isTerminal else {
            throw CommandReceiptLedger.LedgerError.missingReceipt
        }
        record.state = state
        record.exitCode = exitCode
        record.updatedAt = Date()
        if captureContent {
            let text = Self.bounded(output)
            record.output = text.0
            record.truncated = record.truncated || truncated || text.1
        }
        try persist(record)
        recordsByID[id] = record
        try prune()
    }

    private static func bounded(_ text: String) -> (String, Bool) {
        let bytes = text.utf8
        guard bytes.count > contentLimit else { return (text, false) }
        // Decode the bounded prefix, dropping any partial final UTF-8 sequence.
        var prefix = Data(bytes.prefix(contentLimit))
        while String(data: prefix, encoding: .utf8) == nil && !prefix.isEmpty { prefix.removeLast() }
        return (String(decoding: prefix, as: UTF8.self), true)
    }

    func records() -> [CommandHistoryRecord] {
        lock.lock(); defer { lock.unlock() }
        return recordsByID.values.sorted { $0.startedAt > $1.startedAt }
    }

    static func read(directory: URL) throws -> [CommandHistoryRecord] {
        try CommandReceiptLedger.rejectSymbolicLink(at: directory.deletingLastPathComponent())
        try CommandReceiptLedger.rejectSymbolicLink(at: directory)
        guard FileManager.default.fileExists(atPath: directory.path) else { return [] }
        let files = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.fileSizeKey])
        var records: [CommandHistoryRecord] = []
        for file in files where file.pathExtension == "json" {
            guard let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent) else { continue }
            try CommandReceiptLedger.rejectSymbolicLink(at: file)
            // A concurrent local writer may have pruned this record.
            guard FileManager.default.fileExists(atPath: file.path) else { continue }
            guard (try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max) <= 1_000_000 else {
                throw CommandReceiptLedger.LedgerError.invalidStore
            }
            let record = try JSONDecoder().decode(CommandHistoryRecord.self, from: Data(contentsOf: file))
            guard record.id == id, states.contains(record.state),
                  record.command?.utf8.count ?? 0 <= contentLimit,
                  record.output?.utf8.count ?? 0 <= contentLimit else {
                throw CommandReceiptLedger.LedgerError.invalidStore
            }
            records.append(record)
        }
        return records.sorted { $0.startedAt > $1.startedAt }
    }

    private func file(_ id: UUID) -> URL {
        directory.appendingPathComponent(id.uuidString.lowercased() + ".json")
    }

    private func persist(_ record: CommandHistoryRecord) throws {
        let url = file(record.id)
        try CommandReceiptLedger.rejectSymbolicLink(at: url)
        try JSONEncoder().encode(record).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o600)], ofItemAtPath: url.path)
    }

    private func prune() throws {
        let finished = recordsByID.values.filter(\.isTerminal).sorted { $0.updatedAt > $1.updatedAt }
        let cutoff = Date().addingTimeInterval(-Double(Self.retentionDays) * 86_400)
        for (index, record) in finished.enumerated() where index >= Self.maxCompleted || record.updatedAt < cutoff {
            let url = file(record.id)
            try CommandReceiptLedger.rejectSymbolicLink(at: url)
            try FileManager.default.removeItem(at: url)
            recordsByID.removeValue(forKey: record.id)
        }
    }
}
