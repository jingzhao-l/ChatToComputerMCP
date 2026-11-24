import Foundation
import AppKit
import CoreGraphics
import CoreFoundation
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
    }
    let enabled: Bool
    let driver: String
    let driverSHA256: String
    let grants: [Grant]
    static let sharedOwner = "shared-connection"
    static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ChatToComputerMCP/cua-policy.json")
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

    init(driver: String, application: String) throws {
        // A private, direct worker per lease. No connection to a preexisting
        // unrestricted daemon, no independently exposed network listener.
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ctcmcp-cua-" + UUID().uuidString)
        try FileManager.default.createDirectory(at:directory, withIntermediateDirectories:false, attributes:[.posixPermissions:0o700])
        manifestURL = directory.appendingPathComponent("capability.json")
        let manifest: [String:Any] = ["version":3,"expires_after":"2h","idle_timeout":"10m",
            "allow":["tools":["check_permissions","get_window_state","click","type_text","press_key","scroll","drag","set_value","invoke_menu","verify_state"]],
            "resources":["apps":[["bundle_id":application,"launch":false,"windows":"all"]],"desktop":["display":false]]]
        do {
            let data = try JSONSerialization.data(withJSONObject:manifest)
            guard FileManager.default.createFile(atPath:manifestURL.path,contents:data,attributes:[.posixPermissions:0o600]) else {
                throw CUAFailure("cua_manifest_unavailable")
            }
            process.executableURL = URL(fileURLWithPath: driver)
            process.arguments = ["mcp","--direct"]
            process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
            let envKeys = Set(["HOME","USER","LOGNAME","PATH","TMPDIR","LANG","LC_ALL","LC_CTYPE","TZ"])
            var env = ProcessInfo.processInfo.environment.filter { envKeys.contains($0.key) }
            env["CUA_DRIVER_PERMISSION_MODE"] = "bounded"
            env["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"] = manifestURL.path
            env["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] = "1"
            env["DO_NOT_TRACK"] = "1"
            env["CUA_DRIVER_RS_TELEMETRY_ENABLED"] = "false"
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
        if process.isRunning { Darwin.kill(process.processIdentifier, SIGKILL) }
        try? FileManager.default.removeItem(at:manifestURL.deletingLastPathComponent())
    }
    deinit { close() }

    private func check(_ deadline: Date, _ allowed: () -> Bool) throws {
        life.lock(); let stopped = closed; life.unlock()
        guard !stopped, allowed() else { throw CUAFailure("cua_session_revoked") }
        guard Date() < deadline else { throw CUAFailure("cua_driver_timeout: action outcome may be unknown; observe again in a new session") }
    }

    private func send(_ message: [String: Any], deadline: Date, allowed: () -> Bool) throws {
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

    private func receive(id: Int, deadline: Date, allowed: () -> Bool) throws -> [String: Any] {
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

    private func rpc(_ method: String, _ args: [String: Any], deadline: Date, allowed: () -> Bool) throws -> [String: Any] {
        nextID += 1
        try send(["jsonrpc":"2.0", "id":nextID, "method":method, "params":args], deadline:deadline, allowed:allowed)
        return try receive(id:nextID, deadline:deadline, allowed:allowed)
    }

    func call(_ tool: String, _ args: [String: Any], allowed: () -> Bool) throws -> [String: Any] {
        let deadline = Date().addingTimeInterval(10)
        if !initialized {
            let initResult = try rpc("initialize", ["protocolVersion":"2025-06-18", "capabilities":[:],
                "clientInfo":["name":"ChatToComputerMCP", "version":"1"]], deadline:deadline, allowed:allowed)
            guard initResult["protocolVersion"] as? String == "2025-06-18",
                  let info = initResult["serverInfo"] as? [String:Any], info["name"] as? String == "cua-driver",
                  info["version"] as? String == "0.61.0" else { throw CUAFailure("cua_driver_protocol_or_version_mismatch") }
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
        var last = Date()
        var busy = false
        var snapshot: CUASnapshot?
        init(owner: String, target: CUATarget, policyBytes: Data, worker: CUAWorker) {
            self.owner = owner; self.target = target; self.policyBytes = policyBytes; self.worker = worker
        }
    }
    private let gate = NSLock()
    private var sessions: [String: Session] = [:]
    private var revoked = Set<String>()
    private var stopped = false
    private let policyURL: URL
    private var timer: DispatchSourceTimer?
    private static let actions: [String: Set<String>] = [
        "click":["element_token","x","y","capture_id","button","count","action"],
        "type_text":["element_token","text","x","y","capture_id"],
        "press_key":["key","modifiers","element_token"],
        "scroll":["element_token","direction","amount","x","y","capture_id"],
        "drag":["from_x","from_y","to_x","to_y","duration_ms","capture_id"],
        "set_value":["element_token","value"],
        "invoke_menu":["path"]
    ]
    init(policyURL: URL = CUAPolicy.defaultURL) {
        self.policyURL = policyURL
        let timer = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos:.utility))
        timer.schedule(deadline:.now() + 1, repeating:1)
        timer.setEventHandler { [weak self] in self?.sweep() }
        timer.resume()
        self.timer = timer
    }
    deinit { timer?.cancel(); stop() }

    static func definitions() -> [[String: Any]] {
        let string: [String:Any] = ["type":"string"]
        let integer: [String:Any] = ["type":"integer","minimum":1]
        let boolean: [String:Any] = ["type":"boolean"]
        let items: [(String,String,[String:Any],[String],Bool)] = [
            ("cua_capabilities","Report local opt-in and background-only CUA capabilities.",[:],[],true),
            ("cua_list_apps","List running applications explicitly granted to this connection.",[:],[],true),
            ("cua_list_windows","List windows of an approved running application.",["pid":integer],["pid"],true),
            ("cua_create_session","Acquire an exclusive background window session. Keep its bearer ID private.",["pid":integer,"window_id":integer],["pid","window_id"],false),
            ("cua_list_sessions","List own session status. Shared connections cannot enumerate session IDs.",[:],[],true),
            ("cua_observe","Observe the exact leased window, including its accessibility tree and image.",["session_id":string,"include_screenshot":boolean],["session_id"],false),
            ("cua_act","Perform one background-only action on the leased window. Observe again to verify. No foreground fallback.",
                ["session_id":string,"operation":["type":"string","enum":actions.keys.sorted()],"parameters":["type":"object","additionalProperties":true]],["session_id","operation","parameters"],false),
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
        guard policy.enabled, policy.driver.hasPrefix("/"), policy.grants.count <= 64,
              policy.driverSHA256.range(of:"^[0-9a-fA-F]{64}$",options:.regularExpression) != nil else {
            throw CUAFailure("cua_disabled: invalid or disabled local policy")
        }
        return (policy,data)
    }
    private func approvedApp(_ pid: Int32, policy: CUAPolicy, owner: String) throws -> (String,Date) {
        guard let app = NSRunningApplication(processIdentifier:pid), !app.isTerminated,
              let bundle = app.bundleIdentifier, let launched = app.launchDate,
              policy.permits(bundle,owner:owner), pid != getpid(),
              !bundle.lowercased().contains("chat-to-computer-mcp"),
              !["com.trycua.driver","com.apple.systempreferences","com.apple.securityagent","com.apple.loginwindow","com.apple.systemuiserver"].contains(bundle.lowercased()) else { throw CUAFailure("cua_application_not_approved") }
        return (bundle,launched)
    }
    private func windows(_ pid: Int32) -> [[String:Any]] {
        let rows = CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String:Any]] ?? []
        return rows.compactMap { row in
            guard (row[kCGWindowOwnerPID as String] as? Int32) == pid,
                  (row[kCGWindowLayer as String] as? Int) == 0,
                  let id = row[kCGWindowNumber as String] as? Int64, id > 0 else { return nil }
            return ["window_id":id,"title":row[kCGWindowName as String] as? String ?? ""]
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
        if mutation && NSWorkspace.shared.frontmostApplication?.processIdentifier == s.target.pid { return false }
        return true
    }
    private func sweep() {
        gate.lock(); let all = Array(sessions.values); gate.unlock()
        for s in all {
            gate.lock(); let expired = !s.busy && Date().timeIntervalSince(s.last) > 300; gate.unlock()
            if expired || !stillAllowed(s,mutation:false) { retire(s.id) }
        }
    }
    private func retire(_ id: String) {
        gate.lock(); let s = sessions.removeValue(forKey:id); gate.unlock()
        s?.worker.close()
    }
    func revoke(owner: String) {
        gate.lock(); revoked.insert(owner); let ids = sessions.values.filter { $0.owner == owner }.map(\.id); gate.unlock()
        for id in ids { retire(id) }
    }
    func stop() {
        gate.lock(); stopped = true; let all = Array(sessions.values); sessions.removeAll(); gate.unlock()
        for s in all { s.worker.close() }
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
    func call(_ name: String, args: [String:Any], owner: String?) throws -> ([[String:Any]],[String:Any]) {
        let owner = owner ?? CUAPolicy.sharedOwner
        gate.lock(); let allowedOwner = !stopped && !revoked.contains(owner); gate.unlock()
        guard allowedOwner else { throw CUAFailure("cua_service_stopped_or_revoked") }
        guard let def = Self.definitions().first(where: { $0["name"] as? String == name }),
              let schema = def["inputSchema"] as? [String:Any], let props = schema["properties"] as? [String:Any],
              let required = schema["required"] as? [String],
              Set(args.keys).isSubset(of:Set(props.keys)), required.allSatisfy({ args[$0] != nil }) else { throw CUAFailure("cua_invalid_arguments") }
        if name == "cua_capabilities" {
            let policy = try? loadPolicy().0
            let enabled = policy?.grants.contains(where: { $0.clients.contains(owner) }) == true
            return try output(["enabled":enabled,"background_only":true,"driver":"cua-driver","driver_version":"0.61.0","driver_mode":"bounded_direct","identity":owner == CUAPolicy.sharedOwner ? "shared_connection_not_chat_identity" : "verified_oauth_client",
                "operations":Self.actions.keys.sorted(),"requires_local_grant":true])
        }
        if name == "cua_stop_session" {
            guard let id = args["session_id"] as? String else { throw CUAFailure("cua_invalid_session") }
            gate.lock(); let owned = sessions[id]?.owner == owner; gate.unlock()
            guard owned else { throw CUAFailure("cua_unknown_or_unauthorized_session") }
            retire(id)
            return try output(["stopped":true])
        }
        let (policy,bytes) = try loadPolicy()
        guard policy.grants.contains(where: { $0.clients.contains(owner) }) else { throw CUAFailure("cua_client_not_approved") }
        if name == "cua_list_apps" {
            let apps = NSWorkspace.shared.runningApplications.compactMap { app -> [String:Any]? in
                guard let (bundle,_) = try? approvedApp(app.processIdentifier,policy:policy,owner:owner) else { return nil }
                return ["pid":app.processIdentifier,"application":bundle,"name":app.localizedName ?? bundle]
            }
            return try output(["apps":apps])
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
            let list = owner == CUAPolicy.sharedOwner ? [] : owned.map { ["session_id":$0.id,"pid":$0.target.pid,"window_id":$0.target.window] as [String:Any] }
            gate.unlock()
            return try output(["sessions":list,"shared_session_ids_hidden":owner == CUAPolicy.sharedOwner])
        }
        if name == "cua_create_session" {
            let pid = Int32(try int(args["pid"],max:Int64(Int32.max)))
            let window = try int(args["window_id"],max:Int64(UInt32.max))
            let t = try target(pid,window,policy:policy,owner:owner)
            sweep()
            gate.lock(); defer { gate.unlock() }
            guard !stopped, !revoked.contains(owner), sessions.count < 8, !sessions.values.contains(where: { $0.target.pid == pid }) else { throw CUAFailure("cua_target_busy_or_capacity") }
            let driverURL = URL(fileURLWithPath:policy.driver).resolvingSymlinksInPath()
            guard driverURL.path == policy.driver, FileManager.default.isExecutableFile(atPath:policy.driver),
                  let fileSize = try? driverURL.resourceValues(forKeys:[.fileSizeKey]).fileSize,
                  fileSize > 0, fileSize <= 200_000_000 else { throw CUAFailure("cua_driver_unavailable") }
            let hash = SHA256.hash(data:try Data(contentsOf:driverURL,options:.mappedIfSafe)).map { String(format:"%02x",$0) }.joined()
            guard hash == policy.driverSHA256.lowercased() else { throw CUAFailure("cua_driver_integrity_mismatch") }
            let worker = try CUAWorker(driver:policy.driver,application:t.application)
            let s = Session(owner:owner,target:t,policyBytes:bytes,worker:worker)
            sessions[s.id] = s
            return try output(["session_id":s.id,"pid":pid,"window_id":window,"background_only":true,"idle_timeout_seconds":300])
        }
        guard let id = args["session_id"] as? String else { throw CUAFailure("cua_invalid_session") }
        gate.lock()
        guard let s = sessions[id], s.owner == owner, !s.busy, !stopped, Date().timeIntervalSince(s.last) <= 300 else {
            gate.unlock(); throw CUAFailure("cua_unknown_unauthorized_or_busy_session")
        }
        s.busy = true; gate.unlock()
        defer { gate.lock(); s.busy = false; s.last = Date(); gate.unlock() }
        let mutation = name == "cua_act"
        guard stillAllowed(s,mutation:mutation) else { throw CUAFailure("cua_target_unavailable_or_user_active") }
        var driverArgs: [String:Any] = ["pid":s.target.pid,"window_id":s.target.window]
        let driverTool: String
        if mutation {
            guard let snapshot = s.snapshot, let operation = args["operation"] as? String,
                  let parameters = args["parameters"] as? [String:Any] else { throw CUAFailure("cua_observe_before_action") }
            try Self.validateAction(operation,parameters)
            try snapshot.validate(parameters)
            for (key,value) in parameters where key != "capture_id" || operation == "click" { driverArgs[key] = value }
            if operation != "invoke_menu", let revision = snapshot.revision { driverArgs["ui_tree_revision"] = revision }
            if operation != "set_value" { driverArgs["delivery_mode"] = "background" }
            if operation != "set_value" && operation != "invoke_menu" { driverArgs["scope"] = "window" }
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
            let reply = try s.worker.call(driverTool,driverArgs,allowed:{ self.stillAllowed(s,mutation:mutation) })
            guard self.stillAllowed(s,mutation:mutation) else { throw CUAFailure("cua_action_outcome_unknown") }
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
            return (safe,["session_id":id,"operation":driverTool,"background_only":true,"verification_required":mutation,"driver_result":metadata])
        } catch {
            // Do not replay actions after timeout, loss of transport or
            // uncertain native dispatch. New session and fresh observation.
            retire(id)
            throw error
        }
    }
}
