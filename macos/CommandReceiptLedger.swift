import Foundation
import CryptoKit
import Security

/// Durable, local-only idempotency receipts for opt-in shell command sessions.
///
/// This is NOT a subprocess supervisor and never claims to reattach an existing
/// process. Any unfinished receipt observed after a restart is marked
/// "interrupted" and a request ID may not be replayed automatically.
///
/// No command text, full working-directory path, output or credentials are
/// persisted; request IDs are hashed with a per-installation random salt.
final class CommandReceiptLedger {
    enum LedgerError: LocalizedError {
        case invalidStore
        case duplicateRequest
        case receiptLimit
        case unsafePath
        case missingReceipt
        case receiptNotOwned

        var errorDescription: String? {
            switch self {
            case .invalidStore: return "Command receipt store is invalid; refusing commands to prevent accidental retry"
            case .duplicateRequest: return "Request ID already recorded by an earlier runtime; inspect actual effects before choosing a new ID"
            case .receiptLimit: return "Command receipt ledger capacity reached; safely archive it before accepting new commands"
            case .unsafePath: return "Command receipt store path is unsafe"
            case .missingReceipt: return "Command receipt was not found"
            case .receiptNotOwned: return "Command receipt not found or unavailable to this authenticated client"
            }
        }
    }

    private struct Entry: Codable {
        var identity: String
        var state: String
        var startedAt: Date
        var updatedAt: Date
        var exitCode: Int32?
        var ownerHash: String? = nil
    }

    private struct Store: Codable {
        var schema: Int
        var salt: String
        var entries: [Entry]
    }

    private let lock = NSLock()
    private let url: URL
    private var store: Store

    init(fileURL: URL) throws {
        url = fileURL.standardizedFileURL
        let folder = url.deletingLastPathComponent()
        try Self.ensurePrivateDirectory(folder)
        try Self.rejectSymbolicLink(at: url)
        if FileManager.default.fileExists(atPath: url.path) {
            guard let blob = try? Data(contentsOf: url),
                  blob.count <= 3_000_000,
                  let loaded = try? JSONDecoder().decode(Store.self, from: blob),
                  loaded.schema == 1,
                  loaded.salt.count == 64,
                  loaded.salt.allSatisfy({ $0.isHexDigit }),
                  loaded.entries.count <= 8_192,
                  Set(loaded.entries.map(\.identity)).count == loaded.entries.count,
                  loaded.entries.allSatisfy({ entry in
                      entry.identity.count == 64 && entry.identity.allSatisfy(\.isHexDigit)
                          && ["starting", "interrupted", "exited", "cancelled", "timed_out", "launch_failed"].contains(entry.state)
                          && (entry.ownerHash == nil || (entry.ownerHash!.count == 64 && entry.ownerHash!.allSatisfy(\.isHexDigit)))
                  }) else {
                throw LedgerError.invalidStore
            }
            store = loaded
        } else {
            var random = [UInt8](repeating: 0, count: 32)
            guard SecRandomCopyBytes(kSecRandomDefault, random.count, &random) == errSecSuccess else {
                throw LedgerError.invalidStore
            }
            store = Store(schema: 1, salt: random.map { String(format: "%02x", $0) }.joined(), entries: [])
            try persist()
        }
        // Nothing gets restarted or attached automatically. We cannot prove
        // a previous process completed when the app died or the server reset.
        var changed = false
        for i in store.entries.indices where store.entries[i].state == "starting" {
            store.entries[i].state = "interrupted"
            store.entries[i].updatedAt = Date()
            changed = true
        }
        if changed { try persist() }
    }

    static func applicationStoreURL() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("ChatToComputerMCP", isDirectory: true)
            .appendingPathComponent("command-receipts-v1.json")
    }

    /// Authoritative fail-closed admission: persist a tombstone BEFORE launch.
    func reserve(scopeID: String, requestID: String, ownerID: String? = nil) throws {
        lock.lock()
        defer { lock.unlock() }
        let identity = digest(scopeID: scopeID, requestID: requestID)
        if store.entries.contains(where: { $0.identity == identity }) {
            throw LedgerError.duplicateRequest
        }
        guard store.entries.count < 8_192 else { throw LedgerError.receiptLimit }
        let old = store
        let now = Date()
        store.entries.append(Entry(identity: identity, state: "starting",
                                   startedAt: now, updatedAt: now, exitCode: nil,
                                   ownerHash: ownerID.map { digestOwner($0) }))
        do { try persist() } catch { store = old; throw error }
    }

    func finish(scopeID: String, requestID: String, state: String, exitCode: Int32?) throws {
        guard ["exited", "cancelled", "timed_out", "launch_failed"].contains(state) else {
            throw LedgerError.invalidStore
        }
        lock.lock()
        defer { lock.unlock() }
        let identity = digest(scopeID: scopeID, requestID: requestID)
        guard let i = store.entries.firstIndex(where: { $0.identity == identity }) else {
            throw LedgerError.missingReceipt
        }
        let old = store
        store.entries[i].state = state
        store.entries[i].exitCode = exitCode
        store.entries[i].updatedAt = Date()
        do { try persist() } catch { store = old; throw error }
    }

    /// Only the original, authenticated owner may inspect durable state.
    /// No active processes are reattached; legacy unowned receipts fail closed.
    func ownReceipt(scopeID: String, requestID: String, ownerID: String) throws -> [String: Any] {
        guard !ownerID.isEmpty else { throw LedgerError.receiptNotOwned }
        lock.lock()
        defer { lock.unlock() }
        let identity = digest(scopeID: scopeID, requestID: requestID)
        guard let entry = store.entries.first(where: { $0.identity == identity }),
              let owner = entry.ownerHash,
              owner == digestOwner(ownerID) else { throw LedgerError.receiptNotOwned }
        return ["state": entry.state, "exit_code": entry.exitCode.map { $0 as Any } ?? NSNull(),
                "started_at_unix_ms": Int64(entry.startedAt.timeIntervalSince1970 * 1000),
                "updated_at_unix_ms": Int64(entry.updatedAt.timeIntervalSince1970 * 1000),
                "reattachable": false]
    }

    private func digestOwner(_ ownerID: String) -> String {
        let payload = store.salt + ":client:" + String(ownerID.utf8.count) + ":" + ownerID
        return SHA256.hash(data: Data(payload.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// Local GUI/status only; never expose other clients' hashed request IDs.
    func summary() -> [String: Int] {
        lock.lock()
        defer { lock.unlock() }
        var counts: [String: Int] = [:]
        for item in store.entries { counts[item.state, default: 0] += 1 }
        return counts
    }

    private func digest(scopeID: String, requestID: String) -> String {
        // Length separation prevents concatenation ambiguity; salt prevents
        // offline rainbow-table recovery of easy-to-guess request IDs.
        let identity = store.salt + ":" + String(scopeID.utf8.count) + ":" + scopeID
            + ":" + String(requestID.utf8.count) + ":" + requestID
        let bytes = SHA256.hash(data: Data(identity.utf8))
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    private func persist() throws {
        try Self.rejectSymbolicLink(at: url)
        let data = try JSONEncoder().encode(store)
        // Data.write(.atomic) uses an adjacent temp and rename. File mode is
        // set afterwards; directory itself remains 0700 during replacement.
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o600)], ofItemAtPath: url.path)
    }

    private static func ensurePrivateDirectory(_ directory: URL) throws {
        let fm = FileManager.default
        try rejectSymbolicLink(at: directory)
        if !fm.fileExists(atPath: directory.path) {
            try fm.createDirectory(at: directory, withIntermediateDirectories: true,
                                   attributes: [.posixPermissions: NSNumber(value: 0o700)])
        }
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: directory.path, isDirectory: &isDir),
              isDir.boolValue else { throw LedgerError.unsafePath }
        try fm.setAttributes([.posixPermissions: NSNumber(value: 0o700)], ofItemAtPath: directory.path)
    }

    private static func rejectSymbolicLink(at url: URL) throws {
        var statInfo = stat()
        if lstat(url.path, &statInfo) == 0 && (statInfo.st_mode & S_IFMT) == S_IFLNK {
            throw LedgerError.unsafePath
        }
    }
}
