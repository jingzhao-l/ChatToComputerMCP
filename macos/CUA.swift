import Foundation
import AppKit
import CoreGraphics
import CoreFoundation
import ApplicationServices
import CryptoKit
import Darwin

/// CUA is a host capability, never a workspace capability. Policy is written
/// locally by the computer owner and is absent/disabled by default.
struct CUAFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ message: String) { self.message = message }
}

struct CUAPolicy: Decodable {
    struct Grant: Decodable {
        let application: String
        let clients: [String]
        let launchClients: [String]?
    }
    struct ExtensionRuntime: Codable {
        let node: String
        let nodeSHA256: String
        let worker: String
        let workerSHA256: String
    }
    struct ExtensionProfile: Codable {
        let id: String
        let kind: String
        let clients: [String]
        let origins: [String]?
        let port: Int?
        let tokenFile: String?
        let allowFiles: Bool?
    }
    let extensionRuntime: ExtensionRuntime?
    let profiles: [ExtensionProfile]?
    let enabled: Bool
    let requireVerifiedClient: Bool?
    let driver: String
    let driverSHA256: String
    let grants: [Grant]
    static let sharedOwner = "shared-connection"
    static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ChatToComputerMCP/cua-policy.json")
    }
    func permitsLaunch(_ app: String, owner: String) -> Bool {
        grants.contains { $0.application == app && $0.clients.contains(owner) && ($0.launchClients ?? []).contains(owner) }
    }
    func permits(_ app: String, owner: String) -> Bool {
        grants.contains { $0.application == app && $0.clients.contains(owner) }
    }
}

struct CUATarget: Equatable {
    let pid: Int32
    let window: Int64
    let application: String
    let launched: Date
}

final class CUAWorker {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let life = NSLock()
    private var closed = false
    private var pending = Data()
    private var nextID = 0
    private var initialized = false
    private let manifestURL: URL
    private let isExtension: Bool

    init(driver: String, application: String, launch: Bool = false, extensionRuntime: CUAPolicy.ExtensionRuntime? = nil, extensionProfile: CUAPolicy.ExtensionProfile? = nil) throws {
        isExtension = extensionRuntime != nil
        // A private, direct worker per lease. No connection to a preexisting
        // unrestricted daemon, no independently exposed network listener.
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ctcmcp-cua-" + UUID().uuidString)
        try FileManager.default.createDirectory(at:directory, withIntermediateDirectories:false, attributes:[.posixPermissions:0o700])
        manifestURL = directory.appendingPathComponent("capability.json")
        let manifest: [String:Any] = ["version":3,"expires_after":"2h","idle_timeout":"10m",
            "allow":["tools":["check_permissions","get_window_state","click","type_text","press_key","scroll","drag","set_value","invoke_menu","verify_state","set_window_frame"] + (launch ? ["launch_app"] : [])],
            "resources":["apps":[["bundle_id":application,"launch":launch,"windows":"all"]],"desktop":["display":false]]]
        do {
            let data = try extensionProfile.map { try JSONEncoder().encode($0) } ?? JSONSerialization.data(withJSONObject:manifest)
            guard FileManager.default.createFile(atPath:manifestURL.path,contents:data,attributes:[.posixPermissions:0o600]) else {
                throw CUAFailure("cua_manifest_unavailable")
            }
            process.executableURL = URL(fileURLWithPath: extensionRuntime?.node ?? driver)
            process.arguments = extensionRuntime.map { [$0.worker] } ?? ["mcp","--direct"]
            process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
            let envKeys = Set(["HOME","USER","LOGNAME","PATH","TMPDIR","LANG","LC_ALL","LC_CTYPE","TZ"])
            var env = ProcessInfo.processInfo.environment.filter { envKeys.contains($0.key) }
            env["CUA_DRIVER_PERMISSION_MODE"] = "bounded"
            env["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"] = manifestURL.path
            env["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] = "1"
            env["DO_NOT_TRACK"] = "1"
            env["CUA_DRIVER_RS_TELEMETRY_ENABLED"] = "false"
            if let runtime = extensionRuntime {
                env["CTCMCP_CUA_EXTENSION_MANIFEST"] = manifestURL.path
                env["PLAYWRIGHT_BROWSERS_PATH"] = URL(fileURLWithPath:runtime.worker).deletingLastPathComponent().appendingPathComponent("browsers").path
            }
            process.environment = env
        } catch {
            try? FileManager.default.removeItem(at:directory)
            throw error
        }
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { try? FileManager.default.removeItem(at:manifestURL.deletingLastPathComponent()); throw error }
        // Never let a dead pipe terminate the entire MCP gateway.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        for fd in [input.fileHandleForWriting.fileDescriptor, output.fileHandleForReading.fileDescriptor] {
            _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        }
    }

    func close() {
        life.lock(); defer { life.unlock() }
        guard !closed else { return }
        closed = true
        if isExtension && process.isRunning {
            process.terminate()
            let end = ProcessInfo.processInfo.systemUptime + 2.0
            while process.isRunning && ProcessInfo.processInfo.systemUptime < end { usleep(20000) }
        }
        if process.isRunning { Darwin.kill(process.processIdentifier, SIGKILL); process.waitUntilExit() }
        try? FileManager.default.removeItem(at:manifestURL.deletingLastPathComponent())
    }
    deinit { close() }

    private func check(_ deadline: TimeInterval, _ allowed: () -> Bool) throws {
        life.lock(); let stopped = closed; life.unlock()
        guard !stopped, allowed() else { throw CUAFailure("cua_session_revoked") }
        guard ProcessInfo.processInfo.systemUptime < deadline else { throw CUAFailure("cua_driver_timeout: action outcome may be unknown; observe again in a new session") }
    }

    private func send(_ message: [String: Any], deadline: TimeInterval, allowed: () -> Bool) throws {
        var data = try JSONSerialization.data(withJSONObject: message)
        data.append(10)
        let fd = input.fileHandleForWriting.fileDescriptor
        var offset = 0
        while offset < data.count {
            try check(deadline, allowed)
            var p = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
            if poll(&p, 1, 50) <= 0 { continue }
            let count = data.withUnsafeBytes { Darwin.write(fd, $0.baseAddress!.advanced(by: offset), data.count - offset) }
            if count > 0 { offset += count }
            else if count == 0 || (errno != EAGAIN && errno != EINTR) { throw CUAFailure("cua_driver_disconnected") }
        }
    }

    private func receive(id: Int, deadline: TimeInterval, allowed: () -> Bool) throws -> [String: Any] {
        let fd = output.fileHandleForReading.fileDescriptor
        while true {
            try check(deadline, allowed)
            if let newline = pending.firstIndex(of: 10) {
                let line = Data(pending[..<newline])
                pending.removeSubrange(...newline)
                guard let reply = try JSONSerialization.jsonObject(with: line) as? [String: Any] else {
                    throw CUAFailure("cua_driver_protocol_error")
                }
                if reply["id"] as? Int == id {
                    guard let result = reply["result"] as? [String: Any] else { throw CUAFailure("cua_driver_rpc_error") }
                    return result
                }
                // Ignore notifications only, never accidentally consume
                // an unexpected server-side request or mismatched response.
                guard reply["id"] == nil else { throw CUAFailure("cua_driver_protocol_error") }
                continue
            }
            var p = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            if poll(&p, 1, 50) <= 0 { continue }
            var chunk = [UInt8](repeating: 0, count: 65536)
            let n = chunk.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress!, $0.count) }
            if n > 0 {
                guard pending.count + n <= 12_000_000 else { throw CUAFailure("cua_driver_response_too_large") }
                pending.append(contentsOf: chunk.prefix(n))
            } else if n == 0 || (errno != EAGAIN && errno != EINTR) {
                throw CUAFailure("cua_driver_disconnected")
            }
        }
    }

    private func rpc(_ method: String, _ args: [String: Any], deadline: TimeInterval, allowed: () -> Bool) throws -> [String: Any] {
        nextID += 1
        try send(["jsonrpc":"2.0", "id":nextID, "method":method, "params":args], deadline:deadline, allowed:allowed)
        return try receive(id:nextID, deadline:deadline, allowed:allowed)
    }

    func call(_ tool: String, _ args: [String: Any], allowed: () -> Bool) throws -> [String: Any] {
        let deadline = ProcessInfo.processInfo.systemUptime + 10
        if !initialized {
            let initResult = try rpc("initialize", ["protocolVersion":"2025-06-18", "capabilities":[:],
                "clientInfo":["name":"ChatToComputerMCP", "version":"1"]], deadline:deadline, allowed:allowed)
            guard initResult["protocolVersion"] as? String == "2025-06-18",
                  let info = initResult["serverInfo"] as? [String:Any], info["name"] as? String == (isExtension ? "ctcmcp-cua-extension" : "cua-driver"),
                  info["version"] as? String == (isExtension ? "1.0.0" : "0.61.0") else { throw CUAFailure("cua_driver_protocol_or_version_mismatch") }
            try send(["jsonrpc":"2.0", "method":"notifications/initialized"], deadline:deadline, allowed:allowed)
            initialized = true
        }
        return try rpc("tools/call", ["name":tool, "arguments":args], deadline:deadline, allowed:allowed)
    }
}

/// Single runtime authority. Session tokens are unguessable bearer
/// capabilities, additionally bound to a verified OAuth identity when present.
struct CUASnapshot {
    let id: String
    let revision: Int?
    let captureID: String?
    let width: Int?
    let height: Int?
    let tokens: Set<String>
    let observedAt = ProcessInfo.processInfo.systemUptime

    init(structured: [String:Any], blocks: [[String:Any]]) throws {
        guard let id = structured["snapshot_id"] as? String,
              id.range(of:"^s[0-9a-f]{8}$",options:.regularExpression) != nil else {
            throw CUAFailure("cua_missing_snapshot_id")
        }
        self.id = id
        self.revision = structured["ui_tree_revision"] as? Int
        let elements = structured["elements"] as? [[String:Any]] ?? []
        self.tokens = Set(elements.compactMap { $0["element_token"] as? String }.filter {
            $0.hasPrefix(id + ":") && $0.range(of:"^s[0-9a-f]{8}:[0-9]+$",options:.regularExpression) != nil
        })
        // Use the actual PNG dimensions rather than untrusted caller geometry.
        let image = blocks.first { $0["type"] as? String == "image" && $0["mimeType"] as? String == "image/png" }
        if let b64 = image?["data"] as? String, let bytes = Data(base64Encoded:b64),
           bytes.count >= 24, Array(bytes.prefix(8)) == [137,80,78,71,13,10,26,10],
           let capture = structured["capture_id"] as? String, !capture.isEmpty {
            let dims = Array(bytes[16..<24])
            width = dims.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
            height = dims.suffix(4).reduce(0) { ($0 << 8) | Int($1) }
            captureID = capture
        } else {
            width = nil; height = nil; captureID = nil
        }
    }
    func validate(_ args: [String:Any]) throws {
        guard ProcessInfo.processInfo.systemUptime - observedAt <= 30 else { throw CUAFailure("cua_snapshot_expired") }
        if let token = args["element_token"] as? String, !tokens.contains(token) {
            throw CUAFailure("cua_stale_or_foreign_element_token")
        }
        let coordinatePairs = [("x","y"),("from_x","from_y"),("to_x","to_y")]
        for (xk,yk) in coordinatePairs where args[xk] != nil || args[yk] != nil {
            guard let captureID, args["capture_id"] as? String == captureID,
                  let width, let height, width > 0, height > 0,
                  let x = args[xk] as? NSNumber, let y = args[yk] as? NSNumber,
                  x.doubleValue >= 0, x.doubleValue < Double(width),
                  y.doubleValue >= 0, y.doubleValue < Double(height) else {
                throw CUAFailure("cua_stale_capture_or_point_outside_window")
            }
        }
        if args["capture_id"] != nil && args["capture_id"] as? String != captureID {
            throw CUAFailure("cua_stale_capture")
        }
    }
}

final class CUAService {
    private final class Session {
        let id = UUID().uuidString + UUID().uuidString
        let owner: String
        let target: CUATarget
        let policyBytes: Data
        let worker: CUAWorker
        var last = ProcessInfo.processInfo.systemUptime
        var busy = false
        var snapshot: CUASnapshot?
        init(owner: String, target: CUATarget, policyBytes: Data, worker: CUAWorker) {
            self.owner = owner; self.target = target; self.policyBytes = policyBytes; self.worker = worker
        }
    }
    private final class ExtensionSession {
        let id = UUID().uuidString + UUID().uuidString
        let owner: String
        let profile: CUAPolicy.ExtensionProfile
        let policyBytes: Data
        let worker: CUAWorker
        var last = ProcessInfo.processInfo.systemUptime
        var busy = true
        init(owner: String, profile: CUAPolicy.ExtensionProfile, policyBytes: Data, worker: CUAWorker) {
            self.owner=owner; self.profile=profile; self.policyBytes=policyBytes; self.worker=worker
        }
    }
    private let gate = NSRecursiveLock()
    private var runtimeLockFD: Int32 = -1
    private var extensions: [String:ExtensionSession] = [:]
    private let guestMode: Bool
    private let relayOnly: Bool
    private var sessions: [String: Session] = [:]
    private var revoked = Set<String>()
    private var stopped = false
    private var launching = Set<String>()
    private var launchWorkers: [String: (owner: String, worker: CUAWorker)] = [:]
    private var receiptLedger: CommandReceiptLedger?
    private let policyURL: URL
    private var timer: DispatchSourceTimer?
    private static let actions: [String: Set<String>] = [
        "click":["element_token","x","y","capture_id","button","count","action"],
        "type_text":["element_token","text","x","y","capture_id"],
        "press_key":["key","modifiers","element_token"],
        "scroll":["element_token","direction","amount","x","y","capture_id"],
        "drag":["from_x","from_y","to_x","to_y","duration_ms","capture_id"],
        "set_value":["element_token","value"],
        "invoke_menu":["path"],
        "set_window_frame":["x","y","width","height"]
    ]
    init(policyURL: URL = CUAPolicy.defaultURL, guestMode: Bool = false, relayOnly: Bool = false) {
        self.policyURL = policyURL
        self.guestMode = guestMode
        self.relayOnly = relayOnly
        let timer = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos:.utility))
        timer.schedule(deadline:.now() + 1, repeating:1)
        timer.setEventHandler { [weak self] in self?.sweep() }
        timer.resume()
        self.timer = timer
    }
    deinit { timer?.cancel(); stop() }

    static func listenerAllows(_ name: String, guest: Bool, relay: Bool) -> Bool {
        if guest { return name.hasPrefix("cua_") && !name.hasPrefix("cua_extension_") }
        if relay { return ["cua_capabilities","cua_extension_list","cua_extension_open","cua_extension_call","cua_extension_close","cua_receipt_status"].contains(name) }
        return true
    }
    static func definitions() -> [[String: Any]] {
        let string: [String:Any] = ["type":"string"]
        let integer: [String:Any] = ["type":"integer","minimum":1]
        let boolean: [String:Any] = ["type":"boolean"]
        let items: [(String,String,[String:Any],[String],Bool)] = [
            ("cua_extension_list","List locally approved isolated browser and guest profiles.",[:],[],true),
            ("cua_extension_open","Open a new isolated browser or a locally registered guest execution lease.",["profile_id":string,"request_id":string],["profile_id","request_id"],false),
            ("cua_extension_call","Operate the leased browser or guest using its typed operations. No arbitrary code.",["session_id":string,"operation":string,"parameters":["type":"object","additionalProperties":true],"request_id":string],["session_id","operation","parameters","request_id"],false),
            ("cua_extension_close","Release the isolated browser or guest lease.",["session_id":string],["session_id"],false),
            ("cua_capabilities","Report local opt-in and background-only CUA capabilities.",[:],[],true),
            ("cua_launch_app","Launch an application with a separate locally granted launch permission; no arbitrary arguments.",["application":string,"request_id":string],["application","request_id"],false),
            ("cua_receipt_status","Read an action dispatch receipt using its unique request ID. Dispatch is not verification.",["request_id":string],["request_id"],true),
            ("cua_list_apps","List running applications explicitly granted to this connection.",[:],[],true),
            ("cua_list_windows","List windows of an approved running application.",["pid":integer],["pid"],true),
            ("cua_create_session","Acquire an exclusive background window session. Keep its bearer ID private.",["pid":integer,"window_id":integer],["pid","window_id"],false),
            ("cua_list_sessions","List own session status. Shared connections cannot enumerate session IDs.",[:],[],true),
            ("cua_observe","Observe the exact leased window, including its accessibility tree and image.",["session_id":string,"include_screenshot":boolean],["session_id"],false),
            ("cua_act","Perform one background-only action on the leased window. Observe again to verify. No foreground fallback.",
                ["session_id":string,"request_id":string,"operation":["type":"string","enum":actions.keys.sorted()],"parameters":["type":"object","additionalProperties":true]],["session_id","request_id","operation","parameters"],false),
            ("cua_verify","Verify bounded element predicates in the leased window; unknown is not success.",["session_id":string,"expect":["type":"array","minItems":1,"maxItems":8,"items":["type":"object"]],"timeout_ms":["type":"integer","minimum":0,"maximum":6000]],["session_id","expect"],true),
            ("cua_stop_session","Revoke a leased session; interrupted action outcomes may be unknown.",["session_id":string],["session_id"],false)
        ]
        return items.map { name, description, props, required, read in
            ["name":name,"description":description,"inputSchema":["type":"object","properties":props,"required":required,"additionalProperties":false],
             "outputSchema":["type":"object","additionalProperties":true],
             "annotations":["readOnlyHint":read,"destructiveHint":!read,"openWorldHint":true]]
        }
    }
    private func loadPolicy() throws -> (CUAPolicy,Data) {
        // No remote policy setters. Reject symlinks and files readable/writable
        // by other local users. Same-user shell is not a security sandbox.
        guard #available(macOS 14.0, *) else { throw CUAFailure("cua_unsupported_os: macOS 14 or later required") }
        var info = stat()
        guard lstat(policyURL.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
              info.st_uid == getuid(), info.st_mode & 0o077 == 0,
              info.st_size > 0, info.st_size <= 65536 else { throw CUAFailure("cua_disabled: local private policy required") }
        let data = try Data(contentsOf:policyURL)
        let policy = try JSONDecoder().decode(CUAPolicy.self, from:data)
        guard policy.enabled, policy.driver.hasPrefix("/"), policy.grants.count <= 64, (policy.profiles ?? []).count <= 32,
              Set((policy.profiles ?? []).map(\.id)).count == (policy.profiles ?? []).count,
              policy.driverSHA256.range(of:"^[0-9a-fA-F]{64}$",options:.regularExpression) != nil else {
            throw CUAFailure("cua_disabled: invalid or disabled local policy")
        }
        if relayOnly {
            guard policy.grants.isEmpty, policy.requireVerifiedClient != true,
                  let profiles = policy.profiles, profiles.count == 1, profiles[0].kind == "guest",
                  profiles[0].clients == [CUAPolicy.sharedOwner] else {
                throw CUAFailure("cua_relay_requires_one_dedicated_guest_no_host_grants")
            }
        }
        return (policy,data)
    }
    static func allowedApplication(_ bundle: String) -> Bool {
        !bundle.lowercased().contains("chat-to-computer-mcp") &&
        !["com.trycua.driver","com.apple.systempreferences","com.apple.securityagent","com.apple.loginwindow","com.apple.systemuiserver","com.localfilesmcp.app","com.apple.windowserver","com.apple.screensaver.engine"].contains(bundle.lowercased())
    }
    private func canonicalRegularFile(_ path: String) -> Bool {
        guard let raw = realpath(path,nil) else {return false}
        defer {free(raw)}
        var info=stat()
        return String(cString:raw)==path && lstat(path,&info)==0 && (info.st_mode&S_IFMT)==S_IFREG
    }
    private func checkedDriver(_ policy: CUAPolicy) throws -> String {
        let driverURL = URL(fileURLWithPath:policy.driver)
        guard canonicalRegularFile(policy.driver), FileManager.default.isExecutableFile(atPath:policy.driver),
              let fileSize = try? driverURL.resourceValues(forKeys:[.fileSizeKey]).fileSize,
              fileSize > 0, fileSize <= 200_000_000 else { throw CUAFailure("cua_driver_unavailable") }
        let hash = SHA256.hash(data:try Data(contentsOf:driverURL,options:.mappedIfSafe)).map { String(format:"%02x",$0) }.joined()
        guard hash == policy.driverSHA256.lowercased() else { throw CUAFailure("cua_driver_integrity_mismatch") }
        return policy.driver
    }
    // Single state authority across processes as well as threads. Prevent a
    // second listener with the same private receipt store from replaying actions.
    private func acquireRuntimeLock() throws {
        gate.lock(); defer { gate.unlock() }
        guard !stopped else { throw CUAFailure("cua_service_stopped") }
        if runtimeLockFD >= 0 { return }
        let folder=policyURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let lockPath=folder.appendingPathComponent("cua-runtime.lock").path
        let fd=Darwin.open(lockPath,O_CREAT|O_RDWR|O_NOFOLLOW|O_CLOEXEC,0o600)
        guard fd>=0 else { throw CUAFailure("cua_runtime_lock_unavailable") }
        var info=stat()
        guard fstat(fd,&info)==0,(info.st_mode&S_IFMT)==S_IFREG,info.st_uid==getuid(),info.st_mode&0o077==0,
              flock(fd,LOCK_EX|LOCK_NB)==0 else { Darwin.close(fd);throw CUAFailure("cua_runtime_already_owned_or_unsafe") }
        runtimeLockFD=fd
    }
    private func reserveReceipt(_ receipt:CommandReceiptLedger,request:String,owner:String) throws {
        gate.lock();defer{gate.unlock()}
        guard !stopped,!revoked.contains(owner),runtimeLockFD>=0 else {throw CUAFailure("cua_service_stopped_or_revoked")}
        try receipt.reserve(scopeID:"cua",requestID:request,ownerID:owner)
    }
    private func finishReceipt(_ receipt:CommandReceiptLedger,request:String,state:String) throws {
        gate.lock();defer{gate.unlock()}
        guard !stopped,runtimeLockFD>=0 else {throw CUAFailure("cua_action_outcome_unknown")}
        try receipt.finish(scopeID:"cua",requestID:request,state:state,exitCode:nil)
    }
    private func ledger() throws -> CommandReceiptLedger {
        gate.lock(); defer { gate.unlock() }
        try acquireRuntimeLock()
        if let receiptLedger { return receiptLedger }
        let ledger = try CommandReceiptLedger(fileURL:policyURL.deletingLastPathComponent().appendingPathComponent("cua-receipts-v1.json"))
        receiptLedger = ledger
        return ledger
    }
    static func requestID(_ raw: Any?) throws -> String {
        guard let id = raw as? String, id.range(of:"^[A-Za-z0-9_-]{32,128}$",options:.regularExpression) != nil else {
            throw CUAFailure("cua_request_id_required: use a unique unpredictable 32-128 character ID")
        }
        return id
    }
    private func approvedApp(_ pid: Int32, policy: CUAPolicy, owner: String) throws -> (String,Date) {
        guard let app = NSRunningApplication(processIdentifier:pid), !app.isTerminated,
              let bundle = app.bundleIdentifier, let launched = app.launchDate,
              policy.permits(bundle,owner:owner), pid != getpid(),
              Self.allowedApplication(bundle) else { throw CUAFailure("cua_application_not_approved") }
        return (bundle,launched)
    }
    private func windows(_ pid: Int32) -> [[String:Any]] {
        let rows = CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String:Any]] ?? []
        return rows.compactMap { row in
            guard (row[kCGWindowOwnerPID as String] as? Int32) == pid,
                  (row[kCGWindowLayer as String] as? Int) == 0,
                  let id = row[kCGWindowNumber as String] as? Int64, id > 0 else { return nil }
            return ["window_id":id,"title":row[kCGWindowName as String] as? String ?? "", "bounds":row[kCGWindowBounds as String] as? [String:Any] ?? [:]]
        }
    }
    private func target(_ pid: Int32, _ window: Int64, policy: CUAPolicy, owner: String) throws -> CUATarget {
        let (app,launch) = try approvedApp(pid,policy:policy,owner:owner)
        guard windows(pid).contains(where: { $0["window_id"] as? Int64 == window }) else {
            throw CUAFailure("cua_window_not_owned_or_unavailable")
        }
        return CUATarget(pid:pid,window:window,application:app,launched:launch)
    }
    private func stillAllowed(_ s: Session, mutation: Bool) -> Bool {
        gate.lock(); let live = !stopped && !revoked.contains(s.owner) && sessions[s.id] === s; gate.unlock()
        guard live, let (policy,data) = try? loadPolicy(), data == s.policyBytes,
              (try? target(s.target.pid,s.target.window,policy:policy,owner:s.owner)) == s.target else { return false }
        if mutation && !guestMode {
            guard let foreground = NSWorkspace.shared.frontmostApplication, foreground.processIdentifier != s.target.pid else { return false }
        }
        return true
    }
    private func sweep() {
        gate.lock(); let all = Array(sessions.values); let extended = Array(extensions.values); gate.unlock()
        for ext in extended {
            gate.lock(); let expired = !ext.busy && ProcessInfo.processInfo.systemUptime - ext.last > 300; gate.unlock()
            if expired || !extensionAllowed(ext) { retireExtension(ext.id) }
        }
        for s in all {
            gate.lock(); let expired = !s.busy && ProcessInfo.processInfo.systemUptime - s.last > 300; gate.unlock()
            if expired || !stillAllowed(s,mutation:false) { retire(s.id) }
        }
    }
    private func retire(_ id: String) {
        gate.lock(); let s = sessions[id]; s?.worker.close(); sessions.removeValue(forKey:id); gate.unlock()
    }
    func revoke(owner: String) {
        gate.lock(); revoked.insert(owner); let ids = sessions.values.filter { $0.owner == owner }.map(\.id); let ext = extensions.values.filter { $0.owner == owner }.map(\.id); let workers = launchWorkers.values.filter { $0.owner == owner }.map(\.worker); gate.unlock()
        for worker in workers { worker.close() }
        for id in ext { retireExtension(id) }
        for id in ids { retire(id) }
    }
    func stop() {
        gate.lock(); guard !stopped else {gate.unlock();return}; stopped = true; let all = Array(sessions.values); let workers = launchWorkers.values.map(\.worker); let ext = Array(extensions.values); extensions.removeAll(); sessions.removeAll(); gate.unlock()
        for e in ext { e.worker.close() }
        for worker in workers { worker.close() }
        for s in all { s.worker.close() }
        gate.lock(); if runtimeLockFD>=0 {Darwin.close(runtimeLockFD);runtimeLockFD = -1};gate.unlock()
    }
    private func int(_ raw: Any?, max: Int64) throws -> Int64 {
        guard let n = raw as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(),
              n.doubleValue.isFinite, n.doubleValue.rounded() == n.doubleValue,
              n.doubleValue >= 1, n.doubleValue <= Double(max) else { throw CUAFailure("cua_invalid_integer") }
        return n.int64Value
    }
    private func output(_ value: [String:Any]) throws -> ([[String:Any]],[String:Any]) {
        let bytes = try JSONSerialization.data(withJSONObject:value,options:[.sortedKeys])
        return ([["type":"text","text":String(decoding:bytes,as:UTF8.self)]],value)
    }
    static func validateAction(_ name: String, _ args: [String:Any]) throws {
        guard let allowed = Self.actions[name], Set(args.keys).isSubset(of:allowed),
              JSONSerialization.isValidJSONObject(args),
              try JSONSerialization.data(withJSONObject:args).count <= 32768 else {
            throw CUAFailure("cua_invalid_action_arguments")
        }
        if name == "set_window_frame" {
            guard Set(args.keys) == ["x","y","width","height"] else { throw CUAFailure("cua_invalid_window_frame") }
            for (key,raw) in args {
                guard let n = raw as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite,
                    (key == "width" || key == "height" ? (1...32768).contains(n.doubleValue) : (-32768...32768).contains(n.doubleValue)) else { throw CUAFailure("cua_invalid_window_frame") }
            }
            return
        }
        for (key,value) in args {
            switch key {
            case "x","y","from_x","from_y","to_x","to_y","count","amount","duration_ms":
                guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(),
                      n.doubleValue.isFinite, n.doubleValue >= 0, n.doubleValue <= 32768 else { throw CUAFailure("cua_invalid_coordinate_or_amount") }
            case "modifiers","path":
                guard let a = value as? [String], a.count <= 16,
                      a.allSatisfy({ !$0.isEmpty && $0.utf8.count <= 256 }) else { throw CUAFailure("cua_invalid_array") }
            default:
                guard let s = value as? String, s.utf8.count <= (key == "text" || key == "value" ? 16000 : 1024) else { throw CUAFailure("cua_invalid_string") }
            }
        }
        if let count = args["count"] as? NSNumber, !(1...3).contains(count.intValue) || count.doubleValue != Double(count.intValue) { throw CUAFailure("cua_invalid_click_count") }
        if let amount = args["amount"] as? NSNumber, !(1...50).contains(amount.intValue) || amount.doubleValue != Double(amount.intValue) { throw CUAFailure("cua_invalid_scroll_amount") }
        if let duration = args["duration_ms"] as? NSNumber, duration.doubleValue != Double(duration.intValue) || !(0...3000).contains(duration.intValue) { throw CUAFailure("cua_invalid_drag_duration") }
        if let button = args["button"] as? String, !["left","right"].contains(button) { throw CUAFailure("cua_invalid_button") }
        if let action = args["action"] as? String, !["press","show_menu","pick","confirm","cancel","open","show_alternate_ui","show_default_ui"].contains(action) { throw CUAFailure("cua_invalid_element_action") }
        let token = args["element_token"] != nil
        let xy = args["x"] != nil && args["y"] != nil
        if token && (args["x"] != nil || args["y"] != nil || args["capture_id"] != nil || args["count"] != nil) { throw CUAFailure("cua_ambiguous_action_target") }
        if (args["x"] != nil) != (args["y"] != nil) { throw CUAFailure("cua_incomplete_coordinates") }
        switch name {
        case "click":
            guard token != xy, token ? args["x"] == nil && args["y"] == nil : args["capture_id"] != nil else { throw CUAFailure("cua_click_requires_snapshot_token_or_capture_bound_coordinates") }
        case "type_text": guard token != xy, args["text"] != nil else { throw CUAFailure("cua_text_requires_snapshot_target") }
        case "set_value": guard token, args["value"] != nil else { throw CUAFailure("cua_value_requires_element_token") }
        case "press_key": guard args["key"] != nil else { throw CUAFailure("cua_key_required") }
        case "scroll": guard ["up","down","left","right"].contains(args["direction"] as? String ?? ""), token != xy else { throw CUAFailure("cua_scroll_requires_target") }
        case "drag": guard ["from_x","from_y","to_x","to_y","capture_id"].allSatisfy({ args[$0] != nil }) else { throw CUAFailure("cua_drag_requires_coordinates") }
        case "invoke_menu": guard let path = args["path"] as? [String], !path.isEmpty else { throw CUAFailure("cua_menu_path_required") }
        default: throw CUAFailure("cua_invalid_action")
        }
    }

    static func verifyPredicates(_ raw: Any?) throws -> [[String:Any]] {
        guard let predicates = raw as? [[String:Any]], (1...8).contains(predicates.count) else { throw CUAFailure("cua_invalid_verify_predicates") }
        for predicate in predicates {
            guard Set(predicate.keys) == ["element"], let element = predicate["element"] as? [String:Any],
                  Set(element.keys).isSubset(of:["selector","exists","enabled","selected","value_equals"]),
                  let selector = element["selector"] as? [String:Any], !selector.isEmpty,
                  Set(selector.keys).isSubset(of:["role","label_contains"]),
                  selector.values.allSatisfy({ if let s = $0 as? String { return !s.isEmpty && s.utf8.count <= 256 }; return false }),
                  element.keys.count > 1 else { throw CUAFailure("cua_invalid_verify_predicate") }
            for key in ["exists","enabled","selected"] where element[key] != nil {
                guard let b = element[key] as? NSNumber, CFGetTypeID(b) == CFBooleanGetTypeID(), key != "exists" || b.boolValue else { throw CUAFailure("cua_invalid_verify_boolean") }
            }
            if let rawValue = element["value_equals"] {
                guard let value = rawValue as? String, value.utf8.count <= 16000 else { throw CUAFailure("cua_invalid_verify_value") }
            }
        }
        return predicates
    }
    private func extensionAllowed(_ e: ExtensionSession) -> Bool {
        gate.lock(); let live = !stopped && !revoked.contains(e.owner) && extensions[e.id] === e; gate.unlock()
        return live && (try? loadPolicy().1) == e.policyBytes
    }
    private func retireExtension(_ id: String) {
        gate.lock(); let e = extensions[id]; gate.unlock()
        e?.worker.close()
        gate.lock(); extensions.removeValue(forKey:id); gate.unlock()
    }
    private func checkFile(_ path: String, hash: String, executable: Bool) throws {
        let url = URL(fileURLWithPath:path)
        guard canonicalRegularFile(path), (!executable || FileManager.default.isExecutableFile(atPath:path)),
              let size = try? url.resourceValues(forKeys:[.fileSizeKey]).fileSize, size > 0, size <= 200_000_000,
              SHA256.hash(data:try Data(contentsOf:url,options:.mappedIfSafe)).map({String(format:"%02x",$0)}).joined() == hash.lowercased()
        else { throw CUAFailure("cua_extension_integrity_mismatch") }
    }
    private func extensionResult(_ reply: [String:Any], id: String) throws -> ([[String:Any]],[String:Any]) {
        guard reply["isError"] as? Bool != true else {
            throw CUAFailure("cua_extension_refused: " + (((reply["content"] as? [[String:Any]])?.first?["text"] as? String ?? "unknown").prefix(1000)))
        }
        guard let content = reply["content"] as? [[String:Any]], let structured = reply["structuredContent"] as? [String:Any],
              try JSONSerialization.data(withJSONObject:structured).count <= 2_000_000 else { throw CUAFailure("invalid_extension_response") }
        var safe: [[String:Any]] = []
        for block in content {
            if block["type"] as? String == "text", let text = block["text"] as? String, text.utf8.count <= 2_000_000 {
                safe.append(["type":"text","text":text])
            } else if block["type"] as? String == "image", let mime = block["mimeType"] as? String, ["image/png","image/jpeg"].contains(mime),
                      let data = block["data"] as? String, data.utf8.count <= 8_000_000, Data(base64Encoded:data) != nil {
                safe.append(["type":"image","mimeType":mime,"data":data])
            }
        }
        guard !safe.isEmpty else { throw CUAFailure("invalid_extension_content") }
        let outer: [String:Any] = ["session_id":id,"result":structured]
        let encoded = try JSONSerialization.data(withJSONObject:outer)
        return ([["type":"text","text":String(decoding:encoded,as:UTF8.self)]] + safe,outer)
    }
    private func extensionCall(_ name: String,args:[String:Any],owner:String,requestAlive:()->Bool) throws -> ([[String:Any]],[String:Any]) {
        if name == "cua_extension_close" {
            guard let id = args["session_id"] as? String else { throw CUAFailure("invalid_extension_session") }
            gate.lock(); let owns = extensions[id]?.owner == owner; gate.unlock()
            guard owns else { throw CUAFailure("unknown_extension_session") }
            retireExtension(id)
            return try output(["closed":true])
        }
        let (policy,bytes) = try loadPolicy()
        guard guestMode || policy.requireVerifiedClient != true || owner != CUAPolicy.sharedOwner else {throw CUAFailure("cua_verified_client_required")}
        let profiles = (policy.profiles ?? []).filter { $0.clients.contains(owner) }
        if name == "cua_extension_list" {
            return try output(["profiles":profiles.map { ["id":$0.id,"kind":$0.kind,"origins":$0.origins ?? []] as [String:Any] }])
        }
        if name == "cua_extension_open" {
            guard let profileID = args["profile_id"] as? String, let p = profiles.first(where:{$0.id == profileID}),
                  ["browser","guest"].contains(p.kind), let runtime = policy.extensionRuntime else { throw CUAFailure("extension_profile_not_approved") }
            if p.kind == "guest" {
                var info = stat()
                guard let file = p.tokenFile, canonicalRegularFile(file), lstat(file,&info) == 0,
                      info.st_uid == getuid(), info.st_mode & 0o077 == 0, info.st_size == 65 else {
                    throw CUAFailure("guest_private_credential_required")
                }
            }
            try checkFile(runtime.node,hash:runtime.nodeSHA256,executable:true)
            try checkFile(runtime.worker,hash:runtime.workerSHA256,executable:false)
            let request=try Self.requestID(args["request_id"]), receipt=try ledger()
            sweep()
            gate.lock()
            guard !stopped, !revoked.contains(owner), sessions.count + extensions.count + launching.count < 8,
                  p.kind != "guest" || !extensions.values.contains(where:{$0.profile.kind == "guest" && $0.profile.port == p.port}) else {
                gate.unlock(); throw CUAFailure("extension_capacity_or_guest_busy")
            }
            let worker: CUAWorker
            do {
                try reserveReceipt(receipt,request:request,owner:owner)
                worker = try CUAWorker(driver:runtime.node,application:"",extensionRuntime:runtime,extensionProfile:p)
            } catch { gate.unlock(); throw error }
            let e=ExtensionSession(owner:owner,profile:p,policyBytes:bytes,worker:worker)
            extensions[e.id]=e; gate.unlock()
            do {
                let reply=try worker.call("extension_open",[:],allowed:{requestAlive() && self.extensionAllowed(e)})
                guard requestAlive(), extensionAllowed(e) else { throw CUAFailure("extension_open_interrupted") }
                let out=try extensionResult(reply,id:e.id)
                try finishReceipt(receipt,request:request,state:"exited")
                gate.lock(); e.busy=false; e.last=ProcessInfo.processInfo.systemUptime; gate.unlock()
                return out
            } catch { retireExtension(e.id); throw error }
        }
        guard name == "cua_extension_call",let id=args["session_id"] as? String,let op=args["operation"] as? String,
              let params=args["parameters"] as? [String:Any], try JSONSerialization.data(withJSONObject:params).count <= 32768 else { throw CUAFailure("invalid_extension_call") }
        gate.lock()
        guard let e=extensions[id],e.owner == owner,!e.busy,ProcessInfo.processInfo.systemUptime-e.last<=300 else {
            gate.unlock();throw CUAFailure("unknown_or_busy_extension_session")
        }
        e.busy=true;gate.unlock()
        defer {gate.lock();e.busy=false;e.last=ProcessInfo.processInfo.systemUptime;gate.unlock()}
        let browserOps=Set(["observe","navigate","click","fill","press","select","scroll","verify","wait_for","back","forward","hover","drag","tab_list","tab_open","tab_select","tab_close","dialog_prepare","upload","download"])
        let guestOps=Set(["cua_capabilities","cua_list_apps","cua_list_windows","cua_launch_app","cua_create_session","cua_observe","cua_act","cua_verify","cua_stop_session","cua_receipt_status"])
        guard (e.profile.kind == "browser" ? browserOps : guestOps).contains(op), extensionAllowed(e),requestAlive() else {throw CUAFailure("extension_operation_not_allowed")}
        let readOnly=Set(["observe","verify","wait_for","tab_list","cua_capabilities","cua_list_apps","cua_list_windows","cua_observe","cua_verify","cua_receipt_status"])
        let request=try Self.requestID(args["request_id"]),receipt=try ledger()
        if !readOnly.contains(op) {try reserveReceipt(receipt,request:request,owner:owner)}
        var recoverable = false
        do {
            let reply=try e.worker.call("extension_call",["operation":op,"parameters":params],allowed:{requestAlive() && self.extensionAllowed(e)})
            guard requestAlive(),extensionAllowed(e) else {throw CUAFailure("extension_action_outcome_unknown")}
            if reply["isError"] as? Bool == true, (reply["structuredContent"] as? [String:Any])?["recoverable"] as? Bool == true {
                recoverable = true
                if !readOnly.contains(op) {try finishReceipt(receipt,request:request,state:"launch_failed")}
            }
            let result=try extensionResult(reply,id:id)
            if !readOnly.contains(op) {try finishReceipt(receipt,request:request,state:"exited")}
            return result
        }catch{if !recoverable {retireExtension(id)};throw error}
    }
    func call(_ name: String, args: [String:Any], owner: String?, requestAlive: () -> Bool = { true }) throws -> ([[String:Any]],[String:Any]) {
        guard Self.listenerAllows(name,guest:guestMode,relay:relayOnly) else {throw CUAFailure("cua_listener_scope_denied")}
        let owner = owner ?? CUAPolicy.sharedOwner
        gate.lock(); let allowedOwner = !stopped && !revoked.contains(owner); gate.unlock()
        guard allowedOwner, requestAlive() else { throw CUAFailure("cua_service_stopped_or_revoked") }
        guard let def = Self.definitions().first(where: { $0["name"] as? String == name }),
              let schema = def["inputSchema"] as? [String:Any], let props = schema["properties"] as? [String:Any],
              let required = schema["required"] as? [String],
              Set(args.keys).isSubset(of:Set(props.keys)), required.allSatisfy({ args[$0] != nil }) else { throw CUAFailure("cua_invalid_arguments") }
        if name == "cua_capabilities" {
            let policy = try? loadPolicy().0
            let requiresVerified = !guestMode && policy?.requireVerifiedClient == true
            let enabled = (!requiresVerified || owner != CUAPolicy.sharedOwner) && (policy?.grants.contains(where: { $0.clients.contains(owner) }) == true || (!guestMode && policy?.profiles?.contains(where: { $0.clients.contains(owner) }) == true))
            return try output(["enabled":enabled,"background_only":true,"driver":"cua-driver","driver_version":"0.61.0","driver_mode":"bounded_direct","identity":relayOnly ? "dedicated_relay_credential" : guestMode ? "dedicated_guest_connection" : (owner == CUAPolicy.sharedOwner ? "shared_connection_not_chat_identity" : "verified_oauth_client"),"conversation_identity_verified":false,"relay_only":relayOnly,"host_tools_allowed":!guestMode && !relayOnly,"requires_verified_client":requiresVerified,
                "operations":Self.actions.keys.sorted(),"requires_local_grant":true,"accessibility":AXIsProcessTrusted(),"screen_recording":CGPreflightScreenCaptureAccess(),"snapshot_ttl_seconds":30,"guest_mode":guestMode,"extension_profiles_enabled":enabled && !guestMode && policy?.profiles?.contains(where: { $0.clients.contains(owner) }) == true])
        }
        if !guestMode, (try? loadPolicy().0.requireVerifiedClient)==true, owner==CUAPolicy.sharedOwner {
            throw CUAFailure("cua_verified_client_required")
        }
        if name.hasPrefix("cua_extension_") {
            guard !guestMode else { throw CUAFailure("nested_guest_extensions_disabled") }
            return try extensionCall(name,args:args,owner:owner,requestAlive:requestAlive)
        }
        if name == "cua_receipt_status" {
            let policy = try loadPolicy().0
            guard guestMode || policy.requireVerifiedClient != true || owner != CUAPolicy.sharedOwner else {throw CUAFailure("cua_verified_client_required")}
            return try output(try ledger().ownReceipt(scopeID:"cua",requestID:Self.requestID(args["request_id"]),ownerID:owner))
        }
        if name == "cua_stop_session" {
            guard let id = args["session_id"] as? String else { throw CUAFailure("cua_invalid_session") }
            gate.lock(); let owned = sessions[id]?.owner == owner; gate.unlock()
            guard owned else { throw CUAFailure("cua_unknown_or_unauthorized_session") }
            retire(id)
            return try output(["stopped":true])
        }
        let (policy,bytes) = try loadPolicy()
        guard guestMode || policy.requireVerifiedClient != true || owner != CUAPolicy.sharedOwner else {throw CUAFailure("cua_verified_client_required")}
        guard policy.grants.contains(where: { $0.clients.contains(owner) }) else { throw CUAFailure("cua_client_not_approved") }
        if name == "cua_launch_app" {
            guard let application = args["application"] as? String, Self.allowedApplication(application),
                  policy.permitsLaunch(application,owner:owner) else { throw CUAFailure("cua_launch_not_approved") }
            let request = try Self.requestID(args["request_id"])
            let receipt = try ledger()
            let driver = try checkedDriver(policy)
            gate.lock()
            guard !stopped, !revoked.contains(owner), !launching.contains(application), sessions.count + extensions.count + launching.count < 8,
                  !sessions.values.contains(where:{ $0.target.application == application }) else {
                gate.unlock(); throw CUAFailure("cua_target_busy_or_capacity")
            }
            launching.insert(application); gate.unlock()
            defer { gate.lock(); launching.remove(application); launchWorkers.removeValue(forKey:application); gate.unlock() }
            try reserveReceipt(receipt,request:request,owner:owner)
            let worker = try CUAWorker(driver:driver,application:application,launch:true)
            defer { worker.close() }
            gate.lock(); launchWorkers[application] = (owner,worker); gate.unlock()
            func allowed() -> Bool {
                gate.lock(); let live = !stopped && !revoked.contains(owner); gate.unlock()
                return live && requestAlive() && (try? loadPolicy().1) == bytes
            }
            let result = try worker.call("launch_app",["bundle_id":application],allowed:allowed)
            guard allowed(), result["isError"] as? Bool != true,
                  let details = result["structuredContent"] as? [String:Any],
                  let pid = details["pid"] as? Int32,
                  (try? approvedApp(pid,policy:policy,owner:owner).0) == application else {
                throw CUAFailure("cua_launch_unverified_or_refused: inspect actual state; do not replay")
            }
            try finishReceipt(receipt,request:request,state:"exited")
            return try output(["pid":pid,"application":application,"windows":windows(pid),"request_id":request,"background_only":true])
        }
        if name == "cua_list_apps" {
            let apps = NSWorkspace.shared.runningApplications.compactMap { app -> [String:Any]? in
                guard let (bundle,_) = try? approvedApp(app.processIdentifier,policy:policy,owner:owner) else { return nil }
                return ["pid":app.processIdentifier,"application":bundle,"name":app.localizedName ?? bundle]
            }
            return try output(["apps":apps,"approved_launch_applications":policy.grants.filter { ($0.launchClients ?? []).contains(owner) && Self.allowedApplication($0.application) }.map(\.application)])
        }
        if name == "cua_list_windows" {
            let pid = Int32(try int(args["pid"],max:Int64(Int32.max)))
            _ = try approvedApp(pid,policy:policy,owner:owner)
            return try output(["windows":windows(pid)])
        }
        if name == "cua_list_sessions" {
            sweep()
            gate.lock()
            let owned = sessions.values.filter { $0.owner == owner }
            let native = owned.map { ["session_id":$0.id,"pid":$0.target.pid,"window_id":$0.target.window,"kind":"native"] as [String:Any] }
            let extra = extensions.values.filter{$0.owner == owner}.map{["session_id":$0.id,"profile_id":$0.profile.id,"kind":$0.profile.kind] as [String:Any]}
            let list = owner == CUAPolicy.sharedOwner ? [] : native + extra
            gate.unlock()
            return try output(["sessions":list,"shared_session_ids_hidden":owner == CUAPolicy.sharedOwner])
        }
        if name == "cua_create_session" {
            try acquireRuntimeLock()
            let pid = Int32(try int(args["pid"],max:Int64(Int32.max)))
            let window = try int(args["window_id"],max:Int64(UInt32.max))
            let t = try target(pid,window,policy:policy,owner:owner)
            sweep()
            gate.lock(); defer { gate.unlock() }
            guard !stopped, !revoked.contains(owner), sessions.count + extensions.count + launching.count < 8, !launching.contains(t.application), !sessions.values.contains(where: { $0.target.pid == pid }) else { throw CUAFailure("cua_target_busy_or_capacity") }
            let worker = try CUAWorker(driver:checkedDriver(policy),application:t.application)
            let s = Session(owner:owner,target:t,policyBytes:bytes,worker:worker)
            sessions[s.id] = s
            return try output(["session_id":s.id,"pid":pid,"window_id":window,"background_only":true,"idle_timeout_seconds":300])
        }
        guard let id = args["session_id"] as? String else { throw CUAFailure("cua_invalid_session") }
        gate.lock()
        guard let s = sessions[id], s.owner == owner, !s.busy, !stopped, ProcessInfo.processInfo.systemUptime - s.last <= 300 else {
            gate.unlock(); throw CUAFailure("cua_unknown_unauthorized_or_busy_session")
        }
        s.busy = true; gate.unlock()
        defer { gate.lock(); s.busy = false; s.last = ProcessInfo.processInfo.systemUptime; gate.unlock() }
        let mutation = name == "cua_act"
        guard stillAllowed(s,mutation:mutation), requestAlive() else { s.snapshot = nil; throw CUAFailure("cua_target_unavailable_or_user_active") }
        var driverArgs: [String:Any] = ["pid":s.target.pid,"window_id":s.target.window]
        let driverTool: String
        var actionReceipt: (CommandReceiptLedger,String)?
        if mutation {
            guard let snapshot = s.snapshot, let operation = args["operation"] as? String,
                  let parameters = args["parameters"] as? [String:Any] else { throw CUAFailure("cua_observe_before_action") }
            try Self.validateAction(operation,parameters)
            if operation != "set_window_frame" { try snapshot.validate(parameters) } else { try snapshot.validate([:]) }
            let request = try Self.requestID(args["request_id"])
            let receipt = try ledger()
            try reserveReceipt(receipt,request:request,owner:owner)
            actionReceipt = (receipt,request)
            for (key,value) in parameters where key != "capture_id" || operation == "click" { driverArgs[key] = value }
            if operation != "invoke_menu" && operation != "set_window_frame", let revision = snapshot.revision { driverArgs["ui_tree_revision"] = revision }
            if operation != "set_value" && operation != "set_window_frame" { driverArgs["delivery_mode"] = "background" }
            if operation != "set_value" && operation != "invoke_menu" && operation != "set_window_frame" { driverArgs["scope"] = "window" }
            driverTool = operation
            s.snapshot = nil
        } else if name == "cua_verify" {
            let expect = try Self.verifyPredicates(args["expect"])
            let timeout: Int
            if let raw = args["timeout_ms"] {
                guard let n = raw as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(),
                      n.doubleValue == Double(n.intValue), (0...6000).contains(n.intValue) else { throw CUAFailure("cua_invalid_verify_timeout") }
                timeout = n.intValue
            } else { timeout = 3000 }
            driverTool = "verify_state"
            driverArgs["expect"] = expect
            driverArgs["timeout_ms"] = timeout
            driverArgs["include_screenshot"] = false
            s.snapshot = nil
        } else {
            guard name == "cua_observe", args["include_screenshot"] == nil || (args["include_screenshot"] as? NSNumber).map({ CFGetTypeID($0) == CFBooleanGetTypeID() }) == true else { throw CUAFailure("cua_invalid_observe") }
            driverTool = "get_window_state"
            driverArgs["include_screenshot"] = args["include_screenshot"] as? Bool ?? true
            driverArgs["include_accessibility_tree"] = true
            driverArgs["max_image_dimension"] = 1600
            driverArgs["tree_format"] = "both"
            driverArgs["max_elements"] = 250
            s.snapshot = nil
        }
        do {
            let reply = try s.worker.call(driverTool,driverArgs,allowed:{ requestAlive() && self.stillAllowed(s,mutation:mutation) })
            guard requestAlive(), self.stillAllowed(s,mutation:mutation) else { throw CUAFailure("cua_action_outcome_unknown") }
            guard reply["isError"] as? Bool != true else { throw CUAFailure("cua_driver_rejected: " + ((reply["content"] as? [[String:Any]])?.first?["text"] as? String ?? "background unavailable").prefix(2000)) }
            guard let blocks = reply["content"] as? [[String:Any]], !blocks.isEmpty else { throw CUAFailure("cua_driver_invalid_content") }
            var safe: [[String:Any]] = []
            for block in blocks {
                if block["type"] as? String == "text", let text = block["text"] as? String {
                    guard text.utf8.count <= 2_000_000 else { throw CUAFailure("cua_driver_text_too_large") }
                    safe.append(["type":"text","text":text])
                } else if !mutation, block["type"] as? String == "image", let mime = block["mimeType"] as? String,
                          ["image/png","image/jpeg"].contains(mime), let data = block["data"] as? String,
                          data.utf8.count <= 8_000_000, Data(base64Encoded:data) != nil {
                    safe.append(["type":"image","mimeType":mime,"data":data])
                }
            }
            guard !safe.isEmpty else { throw CUAFailure("cua_driver_invalid_content") }
            let structured = reply["structuredContent"] as? [String:Any] ?? [:]
            if structured["foreground_step"] != nil { throw CUAFailure("cua_driver_reported_foreground_step") }
            if name == "cua_observe" { s.snapshot = try CUASnapshot(structured:structured,blocks:safe) }
            // Images are only returned as MCP image blocks, not duplicated in JSON.
            let metadata = structured.filter { !["screenshot","screenshot_base64","image","image_data","screenshot_file_path"].contains($0.key) }
            guard try JSONSerialization.data(withJSONObject:metadata).count <= 2_000_000 else { throw CUAFailure("cua_driver_metadata_too_large") }
            if let (receipt,request) = actionReceipt { try finishReceipt(receipt,request:request,state:"exited") }
            return (safe,["session_id":id,"operation":driverTool,"background_only":true,"verification_required":mutation,"driver_result":metadata])
        } catch {
            // Do not replay actions after timeout, loss of transport or
            // uncertain native dispatch. New session and fresh observation.
            retire(id)
            throw error
        }
    }
}
