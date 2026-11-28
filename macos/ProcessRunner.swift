import Foundation
import Darwin

struct ProcessResult {
    let exitCode: Int32
    let stdout: String
    let stderr: String
    let timedOut: Bool
    var stdoutTruncated = false
    var stderrTruncated = false
}

enum ProcessRunnerError: LocalizedError {
    case invalidInput(String)
    case spawnFailed(String, Int32)
    case pipeFailed(Int32)
    case pipeWriteTimeout

    var errorDescription: String? {
        switch self {
        case let .invalidInput(message):
            return message
        case let .spawnFailed(executable, code):
            return "Could not launch \(executable) (errno \(code): \(String(cString: strerror(code))))."
        case let .pipeFailed(code):
            return "Process pipe failed (errno \(code): \(String(cString: strerror(code))))."
        case .pipeWriteTimeout:
            return "Process pipe write timed out."
        }
    }
}

private final class BoundedDataBuffer {
    private let limit: Int
    private let lock = NSLock()
    private var data = Data()
    private var omittedBytes = 0
    private var incomplete = false

    init(limit: Int) {
        self.limit = max(1, limit)
    }

    func append(_ chunk: Data) {
        lock.lock()
        defer { lock.unlock() }

        let remaining = max(0, limit - data.count)
        if remaining > 0 {
            data.append(chunk.prefix(remaining))
        }
        if chunk.count > remaining {
            omittedBytes += chunk.count - remaining
        }
    }

    func markIncomplete() {
        lock.lock()
        incomplete = true
        lock.unlock()
    }

    var truncated: Bool {
        lock.lock()
        defer { lock.unlock() }
        return omittedBytes > 0 || incomplete
    }

    func string() -> String {
        lock.lock()
        defer { lock.unlock() }

        var text = String(decoding: data, as: UTF8.self)
        if omittedBytes > 0 {
            text += "\n\n[...truncated \(omittedBytes) bytes...]"
        }
        if incomplete {
            text += "\n\n[...output drain ended before EOF...]"
        }
        return text
    }
}

// A waiter may ask readers to stop, but it must never close a pipe that
// another thread is reading. Each reader owns its handle until it exits.
fileprivate final class PipeDrainControl {
    private let lock = NSLock()
    private var stopped = false

    var isStopped: Bool {
        lock.lock(); defer { lock.unlock() }
        return stopped
    }

    func stop() {
        lock.lock(); stopped = true; lock.unlock()
    }
}

private struct SpawnHandles {
    let pid: pid_t
    let stdoutRead: FileHandle
    let stderrRead: FileHandle
}

final class ProcessRunner {
    static let maxCommandTimeoutSeconds = 120
    static let defaultCommandTimeoutSeconds = 30
    static let defaultOutputLimitBytes = 1_000_000

    fileprivate static let readerQueue = DispatchQueue(
        label: "com.filemcp.process-reader",
        qos: .utility,
        attributes: .concurrent
    )

    static func run(
        executable: String,
        arguments: [String],
        cwd: String? = nil,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        timeoutSeconds: Int,
        outputLimitBytes: Int = defaultOutputLimitBytes,
        maxTimeoutSeconds: Int = maxCommandTimeoutSeconds,
        shouldCancel: (() -> Bool)? = nil,
        onOutput: ((Data, Bool) -> Void)? = nil
    ) throws -> ProcessResult {
        // Cancellation can arrive during setup, before a child exists. Do
        // not start a subprocess that is already known to be cancelled.
        if shouldCancel?() == true {
            return ProcessResult(exitCode: 130, stdout: "", stderr: "", timedOut: false)
        }
        let handles = try spawn(
            executable: executable,
            arguments: arguments,
            cwd: cwd,
            environment: environment
        )

        let stdoutBuffer = BoundedDataBuffer(limit: outputLimitBytes)
        let stderrBuffer = BoundedDataBuffer(limit: outputLimitBytes)
        let readers = DispatchGroup()
        let drainControl = PipeDrainControl()
        drain(handles.stdoutRead, into: stdoutBuffer, group: readers, control: drainControl, onOutput: { onOutput?($0, false) })
        drain(handles.stderrRead, into: stderrBuffer, group: readers, control: drainControl, onOutput: { onOutput?($0, true) })

        let timeout = max(1, min(timeoutSeconds, maxTimeoutSeconds))
        let deadline = Date().addingTimeInterval(TimeInterval(timeout))
        var status: Int32 = 0
        var timedOut = false
        var didExit = false
        var cancellationRequested = false

        while Date() < deadline {
            if shouldCancel?() == true {
                cancellationRequested = true
                break
            }

            let result = waitpid(handles.pid, &status, WNOHANG)
            if result == handles.pid {
                didExit = true
                break
            }
            if result == -1 && errno == ECHILD {
                didExit = true
                break
            }
            usleep(20_000)
        }

        if !didExit {
            timedOut = !cancellationRequested
            terminateProcessGroup(handles.pid, graceSeconds: 1.0)
            while true {
                let result = waitpid(handles.pid, &status, 0)
                if result == handles.pid || (result == -1 && errno == ECHILD) {
                    break
                }
                if result == -1 && errno != EINTR {
                    break
                }
            }
        } else {
            // A shell can exit successfully while background descendants keep the
            // process group (and our stdout/stderr pipes) alive. Tool execution is
            // one-shot, so do not leak those descendants beyond the tool call.
            terminateProcessGroup(handles.pid, graceSeconds: 0.25)
        }

        finishReaders(readers, control: drainControl, graceSeconds: 2)

        return ProcessResult(
            exitCode: decodeExitCode(status),
            stdout: stdoutBuffer.string(),
            stderr: stderrBuffer.string(),
            timedOut: timedOut,
            stdoutTruncated: stdoutBuffer.truncated,
            stderrTruncated: stderrBuffer.truncated
        )
    }

    static func startManaged(
        executable: String,
        arguments: [String],
        cwd: String? = nil,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        sensitiveValues: [String] = [],
        onOutput: @escaping (String) -> Void,
        onExit: @escaping (Int32) -> Void
    ) throws -> ManagedProcess {
        let handles = try spawn(
            executable: executable,
            arguments: arguments,
            cwd: cwd,
            environment: environment
        )
        return ManagedProcess(handles: handles, sensitiveValues: sensitiveValues, onOutput: onOutput, onExit: onExit)
    }

    private static func spawn(
        executable: String,
        arguments: [String],
        cwd: String?,
        environment: [String: String]
    ) throws -> SpawnHandles {
        try validatePOSIXStrings(executable: executable, arguments: arguments, cwd: cwd, environment: environment)

        let stdoutPipe = try makePipe()
        let stderrPipe = try makePipe()
        let stdoutReadFD = stdoutPipe.read.fileDescriptor
        let stdoutWriteFD = stdoutPipe.write.fileDescriptor
        let stderrReadFD = stderrPipe.read.fileDescriptor
        let stderrWriteFD = stderrPipe.write.fileDescriptor
        defer {
            try? stdoutPipe.write.close()
            try? stderrPipe.write.close()
        }

        func requireSpawnAction(_ code: Int32) throws {
            guard code == 0 else { throw ProcessRunnerError.spawnFailed(executable, code) }
        }
        var actions: posix_spawn_file_actions_t? = nil
        try requireSpawnAction(posix_spawn_file_actions_init(&actions))
        defer { posix_spawn_file_actions_destroy(&actions) }

        try requireSpawnAction(posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0))
        try requireSpawnAction(posix_spawn_file_actions_adddup2(&actions, stdoutWriteFD, STDOUT_FILENO))
        try requireSpawnAction(posix_spawn_file_actions_adddup2(&actions, stderrWriteFD, STDERR_FILENO))
        try requireSpawnAction(posix_spawn_file_actions_addclose(&actions, stdoutReadFD))
        try requireSpawnAction(posix_spawn_file_actions_addclose(&actions, stderrReadFD))
        try requireSpawnAction(posix_spawn_file_actions_addclose(&actions, stdoutWriteFD))
        try requireSpawnAction(posix_spawn_file_actions_addclose(&actions, stderrWriteFD))
        if let cwd, !cwd.isEmpty {
            let result = addWorkingDirectoryAction(&actions, cwd: cwd)
            guard result == 0 else {
                throw ProcessRunnerError.spawnFailed(executable, result)
            }
        }

        var attributes: posix_spawnattr_t? = nil
        try requireSpawnAction(posix_spawnattr_init(&attributes))
        defer { posix_spawnattr_destroy(&attributes) }
        // Never inherit unrelated gateway sockets or other commands' pipes,
        // even when another thread is between pipe() and setting CLOEXEC.
        let flags = Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT)
        try requireSpawnAction(posix_spawnattr_setflags(&attributes, flags))
        try requireSpawnAction(posix_spawnattr_setpgroup(&attributes, 0))

        var argv: [UnsafeMutablePointer<CChar>?] = ([executable] + arguments).map { strdup($0) }
        argv.append(nil)
        defer {
            for pointer in argv {
                if let pointer { free(pointer) }
            }
        }

        let environmentStrings = environment
            .map { "\($0.key)=\($0.value)" }
            .sorted()
        var envp: [UnsafeMutablePointer<CChar>?] = environmentStrings.map { strdup($0) }
        envp.append(nil)
        defer {
            for pointer in envp {
                if let pointer { free(pointer) }
            }
        }

        var pid: pid_t = 0
        let result = argv.withUnsafeMutableBufferPointer { argvBuffer in
            envp.withUnsafeMutableBufferPointer { envBuffer in
                posix_spawn(
                    &pid,
                    executable,
                    &actions,
                    &attributes,
                    argvBuffer.baseAddress,
                    envBuffer.baseAddress
                )
            }
        }

        guard result == 0 else {
            throw ProcessRunnerError.spawnFailed(executable, result)
        }

        return SpawnHandles(pid: pid, stdoutRead: stdoutPipe.read, stderrRead: stderrPipe.read)
    }

    private static func makePipe() throws -> (read: FileHandle, write: FileHandle) {
        var descriptors: [Int32] = [-1, -1]
        guard pipe(&descriptors) == 0 else { throw ProcessRunnerError.pipeFailed(errno) }
        // Unlike Foundation Pipe(), an exhausted descriptor table is a
        // recoverable tool error. Own both FDs immediately on success.
        let read = FileHandle(fileDescriptor: descriptors[0], closeOnDealloc: true)
        let write = FileHandle(fileDescriptor: descriptors[1], closeOnDealloc: true)
        for fd in descriptors {
            guard fcntl(fd, F_SETFD, FD_CLOEXEC) == 0 else {
                throw ProcessRunnerError.pipeFailed(errno)
            }
        }
        return (read, write)
    }

    /// A child that exits before reading stdin must fail the request, not
    /// deliver SIGPIPE to the gateway. A nonreading child gets a deadline.
    static func writePipe(_ handle: FileHandle, data: Data, timeoutSeconds: TimeInterval,
                          onProgress: () -> Void = {}) throws {
        let fd = handle.fileDescriptor
        let flags = fcntl(fd, F_GETFL)
        guard flags >= 0, fcntl(fd, F_SETNOSIGPIPE, 1) == 0,
              fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 else {
            throw ProcessRunnerError.pipeFailed(errno)
        }
        let deadline = ProcessInfo.processInfo.systemUptime + timeoutSeconds
        var offset = 0
        while offset < data.count {
            guard ProcessInfo.processInfo.systemUptime < deadline else {
                throw ProcessRunnerError.pipeWriteTimeout
            }
            onProgress()
            var event = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
            let ready = poll(&event, 1, 50)
            if ready < 0 && errno == EINTR { continue }
            if ready < 0 { throw ProcessRunnerError.pipeFailed(errno) }
            if ready == 0 { continue }
            if event.revents & Int16(POLLERR | POLLHUP | POLLNVAL) != 0 {
                throw ProcessRunnerError.pipeFailed(EPIPE)
            }
            let count = data.withUnsafeBytes { bytes in
                Darwin.write(fd, bytes.baseAddress!.advanced(by: offset), data.count - offset)
            }
            if count > 0 { offset += count; continue }
            if count < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK) { continue }
            throw ProcessRunnerError.pipeFailed(count == 0 ? EPIPE : errno)
        }
    }

    // The standardized symbol arrived with macOS 26 SDK. Resolving it at
    // runtime also builds with earlier SDKs (including Intel macOS 15),
    // avoids referencing a deprecated declaration with a newer SDK, and
    // keeps cwd selection atomic within posix_spawn (never process-wide chdir).
    private typealias SpawnChdirAction = @convention(c) (
        UnsafeMutablePointer<posix_spawn_file_actions_t?>, UnsafePointer<CChar>
    ) -> Int32

    private static let spawnChdirAction: SpawnChdirAction? = {
        guard let library = dlopen("/usr/lib/libSystem.B.dylib", RTLD_LAZY | RTLD_LOCAL) else {
            return nil
        }
        // Keep the handle alive for the lifetime of this process, since
        // the cached function pointer is used by concurrent launches.
        guard let symbol = dlsym(library, "posix_spawn_file_actions_addchdir")
            ?? dlsym(library, "posix_spawn_file_actions_addchdir_np") else {
            dlclose(library)
            return nil
        }
        return unsafeBitCast(symbol, to: SpawnChdirAction.self)
    }()

    private static func addWorkingDirectoryAction(
        _ actions: inout posix_spawn_file_actions_t?, cwd: String
    ) -> Int32 {
        guard let action = spawnChdirAction else { return ENOSYS }
        return cwd.withCString { action(&actions, $0) }
    }

    private static func validatePOSIXStrings(
        executable: String,
        arguments: [String],
        cwd: String?,
        environment: [String: String]
    ) throws {
        guard !executable.utf8.contains(0) else {
            throw ProcessRunnerError.invalidInput("Executable path contains a NUL byte.")
        }
        for argument in arguments where argument.utf8.contains(0) {
            throw ProcessRunnerError.invalidInput("Process argument contains a NUL byte.")
        }
        if let cwd, cwd.utf8.contains(0) {
            throw ProcessRunnerError.invalidInput("Working directory contains a NUL byte.")
        }
        for (key, value) in environment {
            guard !key.isEmpty, !key.contains("="), !key.utf8.contains(0) else {
                throw ProcessRunnerError.invalidInput("Environment variable name is not valid for POSIX process execution.")
            }
            guard !value.utf8.contains(0) else {
                throw ProcessRunnerError.invalidInput("Environment variable value contains a NUL byte.")
            }
        }
    }

    /// Polling gives a reader with an inherited writer a bounded opportunity
    /// to observe cancellation, even if EOF never arrives. Only this function
    /// accesses or closes the handle, so neither a Foundation exception nor
    /// a POSIX descriptor-reuse race can arise from a concurrent close.
    fileprivate static func readPipe(_ handle: FileHandle, control: PipeDrainControl,
                                     onChunk: (Data) -> Void) -> Bool {
        defer { try? handle.close() }
        let fd = handle.fileDescriptor
        let flags = fcntl(fd, F_GETFL)
        guard flags >= 0, fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 else { return false }
        var bytes = [UInt8](repeating: 0, count: 16_384)
        while !control.isStopped {
            var event = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&event, 1, 100)
            if ready < 0 && errno == EINTR { continue }
            if ready < 0 { return false }
            if ready == 0 { continue }
            if control.isStopped { break }
            if event.revents & Int16(POLLNVAL) != 0 { return false }
            let count = Darwin.read(fd, &bytes, bytes.count)
            if count < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK) { continue }
            if count < 0 { return false }
            if count == 0 { return true }
            onChunk(Data(bytes.prefix(count)))
        }
        return false
    }

    fileprivate static func finishReaders(_ readers: DispatchGroup, control: PipeDrainControl,
                                         graceSeconds: TimeInterval) {
        if readers.wait(timeout: .now() + graceSeconds) == .timedOut {
            control.stop()
            // The poll interval bounds idle-pipe cancellation. Let an already
            // entered internal output callback finish before terminal delivery.
            // Callbacks must not synchronously wait on process completion.
            readers.wait()
        }
    }

    private static func drain(
        _ handle: FileHandle,
        into buffer: BoundedDataBuffer,
        group: DispatchGroup,
        control: PipeDrainControl,
        onOutput: ((Data) -> Void)? = nil
    ) {
        group.enter()
        readerQueue.async {
            defer { group.leave() }
            let complete = readPipe(handle, control: control) { chunk in
                buffer.append(chunk)
                onOutput?(chunk)
            }
            if !complete { buffer.markIncomplete() }
        }
    }

    static func terminateProcessGroup(_ pid: pid_t, graceSeconds: TimeInterval = 2.0) {
        guard pid > 0 else { return }
        if killpg(pid, SIGTERM) != 0 && errno == ESRCH {
            return
        }

        let deadline = Date().addingTimeInterval(graceSeconds)
        while Date() < deadline {
            if killpg(pid, 0) != 0 && errno == ESRCH {
                return
            }
            usleep(50_000)
        }
        _ = killpg(pid, SIGKILL)
    }

    fileprivate static func decodeExitCode(_ status: Int32) -> Int32 {
        if (status & 0x7f) == 0 {
            return (status >> 8) & 0xff
        }
        let signal = status & 0x7f
        return signal == 0 ? status : 128 + signal
    }
}

final class ManagedProcess {
    private let pid: pid_t
    private let stdoutRead: FileHandle
    private let stderrRead: FileHandle
    private let stateLock = NSLock()
    private let readerGroup = DispatchGroup()
    private let drainControl = PipeDrainControl()
    private var running = true
    private let sensitiveValues: [String]
    private let onOutput: (String) -> Void
    private let onExit: (Int32) -> Void

    fileprivate init(
        handles: SpawnHandles,
        sensitiveValues: [String],
        onOutput: @escaping (String) -> Void,
        onExit: @escaping (Int32) -> Void
    ) {
        pid = handles.pid
        stdoutRead = handles.stdoutRead
        stderrRead = handles.stderrRead
        self.sensitiveValues = sensitiveValues
        self.onOutput = onOutput
        self.onExit = onExit
        startReaders()
        startWaiter()
    }

    var isRunning: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return running
    }

    func stop() {
        stateLock.lock()
        let shouldStop = running
        stateLock.unlock()
        guard shouldStop else { return }

        let targetPID = pid
        DispatchQueue.global(qos: .userInitiated).async {
            ProcessRunner.terminateProcessGroup(targetPID, graceSeconds: 2.0)
        }
    }

    func stopSynchronously() {
        stateLock.lock()
        let shouldStop = running
        stateLock.unlock()
        guard shouldStop else { return }
        ProcessRunner.terminateProcessGroup(pid, graceSeconds: 1.0)
    }

    private func startReaders() {
        for handle in [stdoutRead, stderrRead] {
            readerGroup.enter()
            ProcessRunner.readerQueue.async { [readerGroup, drainControl, onOutput, sensitiveValues] in
                defer { readerGroup.leave() }
                var pending = Data()
                let redactor = StreamingSecretRedactor(secrets: sensitiveValues)
                let complete = ProcessRunner.readPipe(handle, control: drainControl) { chunk in
                    pending.append(chunk)
                    // Hold an incomplete UTF-8 suffix for the next chunk. Invalid
                    // sequences elsewhere still use the normal replacement character.
                    var end = pending.count
                    var lead = end - 1
                    while lead > 0 && (pending[lead] & 0xc0) == 0x80 { lead -= 1 }
                    let byte = pending[lead]
                    let width = byte >= 0xc2 && byte <= 0xdf ? 2 :
                        (byte >= 0xe0 && byte <= 0xef ? 3 : (byte >= 0xf0 && byte <= 0xf4 ? 4 : 1))
                    if width > end - lead { end = lead }
                    if end > 0 {
                        let safe = redactor.append(String(decoding: pending.prefix(end), as: UTF8.self))
                        if !safe.isEmpty { onOutput(safe) }
                        pending = Data(pending.dropFirst(end))
                    }
                }
                // Only a genuine EOF can flush an incomplete UTF-8 suffix or
                // potential secret prefix. A cancelled drain discards that tail.
                if complete {
                    let tail = redactor.append(String(decoding: pending, as: UTF8.self), final: true)
                    if !tail.isEmpty { onOutput(tail) }
                }
            }
        }
    }

    private func startWaiter() {
        let targetPID = pid
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var status: Int32 = 0
            while true {
                let result = waitpid(targetPID, &status, 0)
                if result == targetPID || (result == -1 && errno == ECHILD) {
                    break
                }
                if result == -1 && errno != EINTR {
                    break
                }
            }

            guard let self else { return }
            // A managed parent can exit while descendants still hold inherited pipe
            // descriptors. End the whole process group, give readers a chance to
            // observe EOF, then close any remaining descriptors.
            ProcessRunner.terminateProcessGroup(targetPID, graceSeconds: 0.25)
            ProcessRunner.finishReaders(self.readerGroup, control: self.drainControl, graceSeconds: 1)
            self.stateLock.lock()
            self.running = false
            self.stateLock.unlock()
            self.onExit(ProcessRunner.decodeExitCode(status))
        }
    }
}

/// One instance per pipe: a secret may span arbitrary reads, but never two streams.
final class StreamingSecretRedactor {
    private let secrets: [String]
    private var pending = ""

    init(secrets: [String]) {
        self.secrets = secrets.filter { !$0.isEmpty }.sorted { $0.count > $1.count }
    }

    func append(_ text: String, final: Bool = false) -> String {
        guard !secrets.isEmpty else { return text }
        pending += text
        var output = ""
        var cursor = pending.startIndex
        while cursor < pending.endIndex {
            let remaining = pending[cursor...]
            if let secret = secrets.first(where: { remaining.hasPrefix($0) }) {
                output += "[REDACTED]"
                cursor = pending.index(cursor, offsetBy: secret.count)
            } else if !final && secrets.contains(where: { $0.hasPrefix(remaining) }) {
                break
            } else {
                output.append(pending[cursor])
                cursor = pending.index(after: cursor)
            }
        }
        pending = String(pending[cursor...])
        return output
    }
}
