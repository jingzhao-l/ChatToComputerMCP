import Foundation
import CryptoKit
import Darwin

/// Durable, locally opted-in long-job supervision. Only metadata and bounded
/// output are persisted; command text is transferred through the child's stdin.
struct SupervisedCommandRecord: Codable, Equatable {
    let sessionID: String
    let requestID: String
    let scopeID: String
    let ownerID: String
    let root: String
    let cwd: String
    let shell: String
    let image: String?
    let timeout: Int
    let queueIfBusy: Bool
    let commandDigest: String
    let historyID: UUID
    let createdAt: Date

    static func digest(_ command: String) -> String {
        SHA256.hash(data: Data(command.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}

struct SupervisedOutputChunk: Codable, Equatable {
    let cursor: Int
    let text: String
}

struct SupervisedCommandStatus: Codable, Equatable {
    var state: String
    var exitCode: Int32?
    var chunks: [SupervisedOutputChunk]
    var lastCursor: Int
    var updatedAt: Date

    var terminal: Bool { !["starting", "running", "stopping"].contains(state) }
    static func initial() -> Self {
        Self(state: "starting", exitCode: nil, chunks: [], lastCursor: 0, updatedAt: Date())
    }
}

final class SupervisedCommandJob {
    let directory: URL
    let record: SupervisedCommandRecord

    init(directory: URL, record: SupervisedCommandRecord) {
        self.directory = directory; self.record = record
    }

    private var statusURL: URL { directory.appendingPathComponent("status.json") }
    private var lockURL: URL { directory.appendingPathComponent("worker.lock") }
    private var cancelURL: URL { directory.appendingPathComponent("cancel") }

    static func privateWrite<T: Encodable>(_ value: T, to url: URL) throws {
        try CommandReceiptLedger.rejectSymbolicLink(at: url)
        try JSONEncoder().encode(value).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: NSNumber(value: 0o600)], ofItemAtPath: url.path)
    }

    static func privateRead<T: Decodable>(_ type: T.Type, from url: URL, limit: Int) throws -> T {
        try CommandReceiptLedger.rejectSymbolicLink(at: url)
        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max
        guard size <= limit else { throw CommandReceiptLedger.LedgerError.invalidStore }
        return try JSONDecoder().decode(type, from: Data(contentsOf: url))
    }

    func publish(_ status: SupervisedCommandStatus) throws {
        try Self.privateWrite(status, to: statusURL)
    }

    func readStatus() throws -> SupervisedCommandStatus {
        let status = try Self.privateRead(SupervisedCommandStatus.self, from: statusURL, limit: 1_600_000)
        let allowed = ["starting", "running", "stopping", "exited", "cancelled", "timed_out", "launch_failed", "interrupted"]
        guard allowed.contains(status.state), status.lastCursor >= 0, status.chunks.count <= 256,
              status.chunks.reduce(0, { $0 + $1.text.utf8.count }) <= 262_144,
              status.chunks.allSatisfy({ $0.cursor > 0 && $0.cursor <= status.lastCursor }),
              zip(status.chunks, status.chunks.dropFirst()).allSatisfy({ pair in pair.0.cursor + 1 == pair.1.cursor }),
              status.chunks.last.map({ $0.cursor == status.lastCursor }) ?? (status.lastCursor == 0)
        else { throw CommandReceiptLedger.LedgerError.invalidStore }
        if !status.terminal, Date().timeIntervalSince(status.updatedAt) > 8, try !workerAlive() {
            // Never replay a job after a supervisor crash or OS reboot.
            var interrupted = status
            interrupted.state = "interrupted"; interrupted.exitCode = nil
            return interrupted
        }
        return status
    }

    func workerAlive() throws -> Bool {
        try CommandReceiptLedger.rejectSymbolicLink(at: lockURL)
        let fd = open(lockURL.path, O_RDWR | O_CLOEXEC | O_NOFOLLOW)
        if fd < 0 {
            if errno == ENOENT { return false }
            throw CommandReceiptLedger.LedgerError.unsafePath
        }
        defer { close(fd) }
        if flock(fd, LOCK_EX | LOCK_NB) == 0 { flock(fd, LOCK_UN); return false }
        if errno == EWOULDBLOCK || errno == EAGAIN { return true }
        throw CommandReceiptLedger.LedgerError.unsafePath
    }

    func requestCancel() throws {
        try Self.privateWrite(true, to: cancelURL)
    }

    func cancellationRequested() -> Bool {
        FileManager.default.fileExists(atPath: cancelURL.path)
    }

    /// Wait for an actual supervisor acknowledgement before returning a job.
    func launch(command: String, executable: URL) throws {
        let pipe = Pipe()
        let process = Process()
        process.executableURL = executable
        process.arguments = ["--command-worker", directory.path]
        process.standardInput = pipe
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        pipe.fileHandleForReading.closeFile()
        do { try pipe.fileHandleForWriting.write(contentsOf: Data(command.utf8)) }
        catch { try? pipe.fileHandleForWriting.close(); throw error }
        try pipe.fileHandleForWriting.close()
        let deadline = Date().addingTimeInterval(8)
        while Date() < deadline {
            let status = try readStatus()
            if status.state != "starting" { return }
            if !process.isRunning { break }
            Thread.sleep(forTimeInterval: 0.025)
        }
        try? requestCancel()
        throw CommandReceiptLedger.LedgerError.invalidStore
    }

    static func runWorker(directory: URL) throws {
        try CommandReceiptLedger.rejectSymbolicLink(at: directory.deletingLastPathComponent())
        try CommandReceiptLedger.ensurePrivateDirectory(directory)
        let record = try privateRead(SupervisedCommandRecord.self,
            from: directory.appendingPathComponent("record.json"), limit: 65_536)
        guard directory.lastPathComponent == record.sessionID,
              UUID(uuidString: record.sessionID) != nil, (1...3600).contains(record.timeout)
        else { throw CommandReceiptLedger.LedgerError.invalidStore }
        let job = SupervisedCommandJob(directory: directory, record: record)
        let descriptor = open(job.lockURL.path, O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw CommandReceiptLedger.LedgerError.unsafePath }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else { throw CommandReceiptLedger.LedgerError.duplicateRequest }
        defer { flock(descriptor, LOCK_UN) }
        // A worker must never launch twice, including direct re-invocation.
        guard try job.readStatus().state == "starting" else { throw CommandReceiptLedger.LedgerError.duplicateRequest }
        let commandData = FileHandle.standardInput.readDataToEndOfFile()
        guard commandData.count <= 8_000_000, let command = String(data: commandData, encoding: .utf8),
              SupervisedCommandRecord.digest(command) == record.commandDigest else {
            var failed = SupervisedCommandStatus.initial(); failed.state = "launch_failed"
            try job.publish(failed); return
        }
        let state = WorkerOutput()
        if job.cancellationRequested() {
            state.finish(nil, reason: "cancelled")
            try job.publish(state.snapshot())
            return
        }
        do {
            let canonicalRoot = URL(fileURLWithPath: record.root).resolvingSymlinksInPath().standardizedFileURL.path
            let canonicalCwd = URL(fileURLWithPath: record.cwd).resolvingSymlinksInPath().standardizedFileURL.path
            guard canonicalRoot == record.root, canonicalCwd == record.cwd,
                  canonicalCwd == canonicalRoot || canonicalCwd.hasPrefix(canonicalRoot + "/") else {
                throw CommandReceiptLedger.LedgerError.unsafePath
            }
            let plan = try ShellExecutionPlan.build(root: record.root, cwd: record.cwd, command: command,
                shell: record.shell, timeout: record.timeout, image: record.image)
            defer { plan.cleanup?() }
            let child = try ProcessRunner.startManaged(executable: plan.executable,
                arguments: plan.arguments, cwd: plan.cwd, environment: plan.environment,
                onOutput: { state.append($0) }, onExit: { state.finish($0, reason: nil) })
            defer { child.stopSynchronously() }
            state.running()
            try job.publish(state.snapshot())
            let deadline = Date().addingTimeInterval(TimeInterval(record.timeout))
            var requestedStop = false
            var previous: SupervisedCommandStatus?
            while true {
                let snapshot = state.snapshot()
                if snapshot != previous { try job.publish(snapshot); previous = snapshot }
                if snapshot.terminal { break }
                if !requestedStop && (job.cancellationRequested() || Date() >= deadline) {
                    requestedStop = true
                    state.stopping(reason: job.cancellationRequested() ? "cancelled" : "timed_out")
                    child.stop()
                }
                Thread.sleep(forTimeInterval: 0.08)
            }
        } catch {
            state.finish(nil, reason: "launch_failed")
            try job.publish(state.snapshot())
            throw error
        }
    }
}

private final class WorkerOutput {
    private let lock = NSLock()
    private var status = SupervisedCommandStatus.initial()
    private var bytes = 0
    private var stopReason: String?

    func running() { lock.lock(); defer { lock.unlock() }; if !status.terminal { status.state = "running" } }
    func append(_ text: String) {
        lock.lock(); defer { lock.unlock() }
        status.lastCursor += 1
        status.chunks.append(SupervisedOutputChunk(cursor: status.lastCursor, text: text))
        bytes += text.utf8.count
        while bytes > 262_144 || status.chunks.count > 256 { bytes -= status.chunks.removeFirst().text.utf8.count }
        status.updatedAt = Date()
    }
    func stopping(reason: String) {
        lock.lock(); defer { lock.unlock() }
        if !status.terminal { stopReason = reason; status.state = "stopping"; status.updatedAt = Date() }
    }
    func finish(_ code: Int32?, reason: String?) {
        lock.lock(); defer { lock.unlock() }
        guard !status.terminal else { return }
        status.state = stopReason ?? reason ?? "exited"
        status.exitCode = code; status.updatedAt = Date()
    }
    func snapshot() -> SupervisedCommandStatus { lock.lock(); defer { lock.unlock() }; return status }
}

final class CommandSupervisor {
    let directory: URL
    let executable: URL
    let allowsNew: Bool
    init(directory: URL, executable: URL = Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0]), allowsNew: Bool = true) throws {
        self.directory = directory; self.executable = executable; self.allowsNew = allowsNew
        try CommandReceiptLedger.ensurePrivateDirectory(directory.deletingLastPathComponent())
        try CommandReceiptLedger.ensurePrivateDirectory(directory)
    }
    static func directory(for profile: String) -> URL {
        let hash = SupervisedCommandRecord.digest(profile)
        return CommandReceiptLedger.applicationStoreURL().deletingLastPathComponent()
            .appendingPathComponent("supervised-jobs-v1").appendingPathComponent(hash)
    }
    func create(record: SupervisedCommandRecord, command: String) throws -> SupervisedCommandJob {
        guard allowsNew, UUID(uuidString: record.sessionID) != nil else { throw CommandReceiptLedger.LedgerError.invalidStore }
        let location = directory.appendingPathComponent(record.sessionID)
        try FileManager.default.createDirectory(at: location, withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700])
        let job = SupervisedCommandJob(directory: location, record: record)
        try SupervisedCommandJob.privateWrite(record, to: location.appendingPathComponent("record.json"))
        try job.publish(.initial())
        try job.launch(command: command, executable: executable)
        return job
    }
    func jobs() throws -> [SupervisedCommandJob] {
        var jobs: [SupervisedCommandJob] = []
        for location in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            guard UUID(uuidString: location.lastPathComponent) != nil else { continue }
            try CommandReceiptLedger.rejectSymbolicLink(at: location)
            let record = try SupervisedCommandJob.privateRead(SupervisedCommandRecord.self,
                from: location.appendingPathComponent("record.json"), limit: 65_536)
            guard record.sessionID == location.lastPathComponent else { throw CommandReceiptLedger.LedgerError.invalidStore }
            jobs.append(SupervisedCommandJob(directory: location, record: record))
        }
        let completed = try jobs.filter { try $0.readStatus().terminal }.sorted { $0.record.createdAt > $1.record.createdAt }
        let old = completed.dropFirst(200).filter { (try? $0.workerAlive()) == false }
        for job in old { try FileManager.default.removeItem(at: job.directory) }
        let removed = Set(old.map { $0.record.sessionID })
        return jobs.filter { !removed.contains($0.record.sessionID) }.sorted { $0.record.createdAt < $1.record.createdAt }
    }
}
