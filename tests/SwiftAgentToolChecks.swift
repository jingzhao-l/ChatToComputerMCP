// Compiled in the server file so these tests exercise the real private tool dispatcher.
func runAgentToolChecks(rootPath: String) throws {
    func check(_ condition: Bool, line: Int = #line) { precondition(condition, "Failed at fixture line \(line)") }
    let root = URL(fileURLWithPath: rootPath)
    let resolver = try SafePathResolver(rootPath: rootPath)
    let safe = LocalTools(resolver: resolver, gitUserName: "Test", gitUserEmail: "test@example.invalid", enableCommands: false, enableGitPush: true, enableCodexHistory: true)
    let full = LocalTools(resolver: resolver, gitUserName: "Test", gitUserEmail: "test@example.invalid", enableCommands: true, enableGitPush: true, enableCodexHistory: true)
    defer { full.stopCommandSessions() }
    func call(_ tools: LocalTools, _ name: String, _ args: [String: Any] = [:]) throws -> [String: Any] {
        let result = try tools.call(name: name, arguments: args).structuredContent
        precondition(JSONSerialization.isValidJSONObject(result))
        return result
    }
    func rejects(_ label: String, _ operation: () throws -> Void) {
        do { try operation(); fatalError("Expected rejection: \(label)") } catch { print("PASS reject: \(label)") }
    }
    func write(_ path: String, _ text: String) throws {
        let file = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: file)
    }
    func poll(_ id: String, cursor: Int = 0, deadline: TimeInterval = 8) throws -> [String: Any] {
        let end = Date().addingTimeInterval(deadline)
        while Date() < end {
            let result = try call(full, "read_command_output", ["session_id": id, "cursor": cursor])
            if !["queued", "running", "stopping"].contains(result["state"] as! String) { return result }
            Thread.sleep(forTimeInterval: 0.03)
        }
        fatalError("Session did not finish")
    }
    let restricted = LocalTools(resolver: resolver, gitUserName: "Test", gitUserEmail: "test@example.invalid", enableCommands: false)
    check(!restricted.hasTool(named: "git_push") && !restricted.hasTool(named: "save_conversation_to_codex"))
    rejects("disabled Git push") { _ = try call(restricted, "git_push") }
    rejects("disabled Codex history") { _ = try call(restricted, "save_conversation_to_codex", ["title": "disabled", "messages": [["role": "user", "content": "test"]]]) }
    check(safe.toolDefinitions.count == 26 && full.toolDefinitions.count == 32)
    for name in ["file_info", "create_directory", "copy_file", "move_file"] {
        check(safe.hasTool(named: name))
    }
    check(!safe.hasTool(named: "command_capacity_status") && full.hasTool(named: "command_capacity_status"))
    check(safe.hasTool(named: "apply_patch") && !safe.hasTool(named: "start_command") && !safe.hasTool(named: "read_command_output"))
    rejects("disabled commands") { _ = try call(safe, "start_command", ["request_id": "disabled", "command": "true"]) }
    _ = try call(safe, "create_directory", ["relative_path": "new-folder/sub"])
    check(FileManager.default.fileExists(
        atPath: root.appendingPathComponent("new-folder/sub").path))
    let newFolderInfo = try call(safe, "file_info", ["relative_path": "new-folder/sub"])
    check(newFolderInfo["kind"] as? String == "directory")
    try write("new-folder/source.txt", "safe original")
    let sourceInfo = try call(safe, "file_info", ["relative_path": "new-folder/source.txt"])
    check(sourceInfo["kind"] as? String == "file" && sourceInfo["size_bytes"] as? Int == 13)
    _ = try call(safe, "copy_file", [
        "source_path": "new-folder/source.txt", "destination_path": "new-folder/sub/copy.txt"])
    check(try String(contentsOf: root.appendingPathComponent("new-folder/sub/copy.txt"),
                     encoding: .utf8) == "safe original")
    rejects("copy refuses overwrite") {
        _ = try call(safe, "copy_file", [
            "source_path": "new-folder/source.txt", "destination_path": "new-folder/sub/copy.txt"])
    }
    _ = try call(safe, "move_file", [
        "source_path": "new-folder/sub/copy.txt", "destination_path": "new-folder/moved.txt"])
    check(FileManager.default.fileExists(atPath: root.appendingPathComponent("new-folder/moved.txt").path))
    check(!FileManager.default.fileExists(atPath: root.appendingPathComponent("new-folder/sub/copy.txt").path))
    rejects("move refuses overwrite") {
        _ = try call(safe, "move_file", [
            "source_path": "new-folder/moved.txt", "destination_path": "new-folder/source.txt"])
    }
    rejects("file tools refuse traversal") {
        _ = try call(safe, "create_directory", ["relative_path": "../forbidden"])
    }
    rejects("file copy refuses source outside workspace") {
        _ = try call(safe, "copy_file", [
            "source_path": "../outside.txt", "destination_path": "new-folder/escape.txt"])
    }
    print("PASS: file info, new directories, copy/move, no overwrite, workspace containment")

    try write("nested/code.txt", "héllo 👋\r\nkeep\r\n")
    let edit: [String: Any] = ["relative_path": "nested/code.txt", "old_text": "héllo 👋", "new_text": "xin chào", "dry_run": true]
    let preview = try call(safe, "edit_file", edit)
    check(preview["applied"] as? Bool == false)
    check(try String(contentsOf: root.appendingPathComponent("nested/code.txt"), encoding: .utf8) == "héllo 👋\r\nkeep\r\n")
    var apply = edit; apply["dry_run"] = false; apply["expected_sha256"] = preview["before_sha256"]
    _ = try call(safe, "edit_file", apply)
    check(try String(contentsOf: root.appendingPathComponent("nested/code.txt"), encoding: .utf8) == "xin chào\r\nkeep\r\n")
    rejects("stale hash") { _ = try call(safe, "edit_file", apply) }
    rejects("missing text") { _ = try call(safe, "edit_file", ["relative_path": "nested/code.txt", "old_text": "missing", "new_text": "bad"]) }
    try write("ambiguous.txt", "aaa")
    rejects("overlapping matches") { _ = try call(safe, "edit_file", ["relative_path": "ambiguous.txt", "old_text": "aa", "new_text": "x"]) }
    rejects("empty old text") { _ = try call(safe, "edit_file", ["relative_path": "ambiguous.txt", "old_text": "", "new_text": "x"]) }
    rejects("escape path") { _ = try call(safe, "edit_file", ["relative_path": "../outside.txt", "old_text": "a", "new_text": "x"]) }
    try Data([0xff, 0xfe]).write(to: root.appendingPathComponent("binary"))
    rejects("invalid UTF8") { _ = try call(safe, "edit_file", ["relative_path": "binary", "old_text": "a", "new_text": "x"]) }
    try write("patch-a.txt", "alpha\nbeta\n")
    try write("patch-b.txt", "one\n")
    let patchChanges: [[String: Any]] = [
        ["relative_path": "patch-a.txt", "old_text": "alpha", "new_text": "ALPHA"],
        ["relative_path": "patch-a.txt", "old_text": "beta", "new_text": "BETA"],
        ["relative_path": "patch-b.txt", "old_text": "one", "new_text": "ONE"],
    ]
    let patchPreview = try call(safe, "apply_patch", ["changes": patchChanges, "dry_run": true])
    check(patchPreview["applied"] as? Bool == false && patchPreview["file_count"] as? Int == 2 && patchPreview["change_count"] as? Int == 3)
    check(try String(contentsOf: root.appendingPathComponent("patch-a.txt"), encoding: .utf8) == "alpha\nbeta\n")
    var guardedChanges = patchChanges
    let patchFiles = patchPreview["files"] as! [[String: Any]]
    guardedChanges[0]["expected_sha256"] = patchFiles[0]["before_sha256"]
    _ = try call(safe, "apply_patch", ["changes": guardedChanges])
    check(try String(contentsOf: root.appendingPathComponent("patch-a.txt"), encoding: .utf8) == "ALPHA\nBETA\n")
    check(try String(contentsOf: root.appendingPathComponent("patch-b.txt"), encoding: .utf8) == "ONE\n")
    try write("patch-atomic-a.txt", "keep-a")
    try write("patch-atomic-b.txt", "keep-b")
    rejects("patch validates before writes") {
        _ = try call(safe, "apply_patch", ["changes": [
            ["relative_path": "patch-atomic-a.txt", "old_text": "keep-a", "new_text": "changed-a"],
            ["relative_path": "patch-atomic-b.txt", "old_text": "missing", "new_text": "changed-b"],
        ]])
    }
    check(try String(contentsOf: root.appendingPathComponent("patch-atomic-a.txt"), encoding: .utf8) == "keep-a")
    var oversizedPatch: [[String: Any]] = []
    for index in 0..<7 {
        let name = "patch-large-\(index).txt"
        try write(name, String(repeating: "x", count: 4_600_000) + "needle")
        oversizedPatch.append(["relative_path": name, "old_text": "needle", "new_text": "done"])
    }
    rejects("patch aggregate budget") { _ = try call(safe, "apply_patch", ["changes": oversizedPatch, "dry_run": true]) }
    try write("AGENTS.md", "root guidance")
    try write("nested/AGENTS.md", "nested guidance")
    try write("nested/package.json", "{\"scripts\":{\"test\":\"echo test\"}}")
    let context = try call(safe, "workspace_context", ["path": "nested"])
    let files = context["files"] as! [[String: Any]]
    check(files.map { $0["path"] as! String } == ["AGENTS.md", "nested/AGENTS.md", "nested/package.json"])
    check(context["cwd"] as? String == "nested")
    try write("nested/AGENTS.md", String(repeating: "x", count: 20_000))
    let bounded = try call(safe, "workspace_context", ["path": "nested"])
    check((bounded["files"] as! [[String: Any]])[1]["truncated"] as? Bool == true)
    let outside = root.deletingLastPathComponent().appendingPathComponent("outside.txt")
    try Data("PRIVATE OUTSIDE".utf8).write(to: outside)
    try FileManager.default.createSymbolicLink(atPath: root.appendingPathComponent("outside-link").path, withDestinationPath: outside.path)
    rejects("symlink edit escape") { _ = try call(safe, "edit_file", ["relative_path": "outside-link", "old_text": "PRIVATE", "new_text": "LEAK"]) }
    try FileManager.default.removeItem(at: root.appendingPathComponent("nested/AGENTS.md"))
    try FileManager.default.createSymbolicLink(atPath: root.appendingPathComponent("nested/AGENTS.md").path, withDestinationPath: outside.path)
    let escaped = try call(safe, "workspace_context", ["path": "nested"])
    check(!String(describing: escaped).contains("PRIVATE OUTSIDE"))
    print("PASS: edit preview/apply/Unicode/CRLF and scoped bounded context")

    let args: [String: Any] = ["request_id": "stream", "command": "printf first; sleep 1; printf second; printf error >&2; exit 7", "timeout_seconds": 10]
    let started = try call(full, "start_command", args)
    let id = started["session_id"] as! String
    check(try call(full, "start_command", args)["session_id"] as? String == id)
    rejects("retry key mismatch") { _ = try call(full, "start_command", ["request_id": "stream", "command": "true"]) }
    rejects("mutation while command active") { _ = try call(full, "edit_file", edit) }
    rejects("second session while active") { _ = try call(full, "start_command", ["request_id": "second", "command": "true"]) }
    Thread.sleep(forTimeInterval: 0.25)
    let partial = try call(full, "read_command_output", ["session_id": id])
    check(partial["state"] as? String == "running" && (partial["output"] as! String).contains("first"))
    let cursor = partial["next_cursor"] as! Int
    let completed = try poll(id, cursor: cursor)
    check(completed["exit_code"] as? Int32 == 7)
    check((completed["output"] as! String).contains("second") && !(completed["output"] as! String).contains("first"))
    check(try call(full, "read_command_output", ["session_id": id, "cursor": completed["next_cursor"]!])["output"] as? String == "")
    rejects("future cursor") { _ = try call(full, "read_command_output", ["session_id": id, "cursor": 99999]) }
    // Exercise the actual configured limit (default 8; 4...16 supported).
    let maxParallel = min(16, max(4, Int(ProcessInfo.processInfo.environment["FILEMCP_MAX_CONCURRENT_COMMANDS"] ?? "") ?? 8))
    let initialCapacity = try call(full, "command_capacity_status")
    check(initialCapacity["max_concurrent_commands"] as? Int == maxParallel)
    check(initialCapacity["active_total"] as? Int == 0)
    check(initialCapacity["available_slots"] as? Int == maxParallel)
    check(initialCapacity["accepting_new_commands"] as? Bool == true)
    let parallelNames = (0..<maxParallel).map { String(format: "worktree-%02d", $0) }
    for name in parallelNames + ["worktree-overcap", "worktree-free"] {
        try write("\(name)/sample.txt", "original")
    }
    try write("\(parallelNames[0])/nested/sample.txt", "nested")
    var parallel: [String: String] = [:]
    for name in parallelNames {
        let result = try call(full, "start_command", [
            "request_id": "parallel-\(name)", "command": "printf started; sleep 12; printf finished",
            "cwd": name, "timeout_seconds": 25,
        ])
        parallel[name] = result["session_id"] as? String
    }
    check(parallel.count == maxParallel)
    let capacityAtLimit = try call(full, "command_capacity_status")
    check(capacityAtLimit["active_long_running"] as? Int == maxParallel)
    check(capacityAtLimit["active_synchronous"] as? Int == 0)
    check(capacityAtLimit["available_slots"] as? Int == 0)
    rejects("session beyond configured concurrency cap") {
        _ = try call(full, "start_command", ["request_id": "parallel-overcap", "command": "true", "cwd": "worktree-overcap"])
    }
    rejects("nested session conflicts with parent scope") {
        _ = try call(full, "start_command", ["request_id": "parallel-nested", "command": "true", "cwd": parallelNames[0] + "/nested"])
    }
    rejects("root session overlaps all worktrees") {
        _ = try call(full, "start_command", ["request_id": "parallel-root", "command": "true"])
    }
    rejects("same-worktree file mutation blocked") {
        _ = try call(full, "edit_file", ["relative_path": parallelNames[0] + "/sample.txt", "old_text": "original", "new_text": "unsafe"])
    }
    rejects("nested file mutation blocked") {
        _ = try call(full, "edit_file", ["relative_path": parallelNames[0] + "/nested/sample.txt", "old_text": "nested", "new_text": "unsafe"])
    }
    rejects("mixed-scope patch rejected before mutation") {
        _ = try call(full, "apply_patch", ["changes": [
            ["relative_path": "worktree-free/sample.txt", "old_text": "original", "new_text": "bad"],
            ["relative_path": parallelNames[1] + "/sample.txt", "old_text": "original", "new_text": "bad"],
        ]])
    }
    check(try String(contentsOf: root.appendingPathComponent("worktree-free/sample.txt"), encoding: .utf8) == "original")
    _ = try call(full, "edit_file", ["relative_path": "worktree-free/sample.txt", "old_text": "original", "new_text": "safe"])
    check(try String(contentsOf: root.appendingPathComponent("worktree-free/sample.txt"), encoding: .utf8) == "safe")
    rejects("synchronous command shares configured total task budget") {
        _ = try full.call(name: "run_command", arguments: ["command": "printf should-not-run", "cwd": "worktree-free"])
    }
    _ = try call(full, "cancel_command", ["session_id": parallel[parallelNames[1]]!])
    check(try poll(parallel[parallelNames[1]]!)["state"] as? String == "cancelled")
    check(try call(full, "read_command_output", ["session_id": parallel[parallelNames[0]]!])["state"] as? String == "running")
    for name in parallelNames where name != parallelNames[1] {
        let result = try poll(parallel[name]!, deadline: 20)
        check(result["state"] as? String == "exited" && result["exit_code"] as? Int32 == 0)
        check((result["output"] as? String ?? "").contains("finished"))
    }
    let sync = try full.call(name: "run_command", arguments: ["command": "printf sync-ok", "cwd": "worktree-free"])
    check(String(describing: sync.structuredContent).contains("sync-ok"))
    print("PASS: \(maxParallel) disjoint sessions, scoped mutations, independent cancellation and bounded admission")

    // Independent synchronous commands share the same bounded concurrency
    // budget as long sessions, but never hold the global mutation slot.
    for name in ["sync-a", "sync-b", "sync-free"] {
        try write("\(name)/sample.txt", "before")
    }
    let synchronousGroup = DispatchGroup()
    for name in ["sync-a", "sync-b"] {
        synchronousGroup.enter()
        DispatchQueue.global(qos: .utility).async {
            defer { synchronousGroup.leave() }
            do {
                _ = try full.call(name: "run_command", arguments: [
                    "cwd": name,
                    "command": "touch started; while [ ! -f release ]; do sleep 0.05; done; printf sync-ok",
                    "timeout_seconds": 12,
                ])
                try Data("ok".utf8).write(to: root.appendingPathComponent(name).appendingPathComponent("completed"))
            } catch {
                try? Data(String(describing: error).utf8).write(
                    to: root.appendingPathComponent(name).appendingPathComponent("failed"))
            }
        }
    }
    let syncAdmissionDeadline = Date().addingTimeInterval(6)
    while Date() < syncAdmissionDeadline && !["sync-a", "sync-b"].allSatisfy({
        FileManager.default.fileExists(atPath: root.appendingPathComponent($0 + "/started").path)
    }) { Thread.sleep(forTimeInterval: 0.02) }
    check(["sync-a", "sync-b"].allSatisfy({
        FileManager.default.fileExists(atPath: root.appendingPathComponent($0 + "/started").path)
    }))
    let synchronousCapacity = try call(full, "command_capacity_status")
    check(synchronousCapacity["active_synchronous"] as? Int == 2)
    check(synchronousCapacity["active_total"] as? Int == 2)
    rejects("synchronous command blocks same-directory edits") {
        _ = try call(full, "edit_file", ["relative_path": "sync-a/sample.txt", "old_text": "before", "new_text": "unsafe"])
    }
    rejects("synchronous command blocks overlapping long task") {
        _ = try call(full, "start_command", ["request_id": "sync-overlap", "command": "true", "cwd": "sync-a"])
    }
    _ = try call(full, "edit_file", ["relative_path": "sync-free/sample.txt", "old_text": "before", "new_text": "safe"])
    for name in ["sync-a", "sync-b"] { try write(name + "/release", "1") }
    check(synchronousGroup.wait(timeout: .now() + 8) == .success)
    check(["sync-a", "sync-b"].allSatisfy({
        FileManager.default.fileExists(atPath: root.appendingPathComponent($0 + "/completed").path)
    }))
    print("PASS: simultaneous synchronous commands with scoped collision protection")
    let syncDone = DispatchSemaphore(value: 0)
    DispatchQueue.global().async {
        defer { syncDone.signal() }
        do {
            _ = try full.call(name: "run_command", arguments: ["cwd": "sync-a",
                "command": "printf desktop-sync-live; sleep 20", "timeout_seconds": 25],
                verifiedOwnerID: "desktop-sync-client")
            preconditionFailure("Cancelled short command reported success")
        } catch {}
    }
    let syncDeadline = Date().addingTimeInterval(6)
    var syncTask: DesktopCommandSnapshot?
    while Date() < syncDeadline {
        if let task = full.desktopCommandSnapshots(workspaceID: "default", name: "Default")
            .first(where: { $0.active && $0.sessionID.hasPrefix("sync-") }),
           let output = try? full.desktopTaskOutput(task.sessionID),
           (output["output"] as? String ?? "").contains("desktop-sync-live") {
            syncTask = task; break
        }
        Thread.sleep(forTimeInterval: 0.03)
    }
    check(syncTask != nil)
    let selectedSyncID = syncTask!.sessionID
    rejects("remote long-command API cannot read local short-task ID") {
        _ = try call(full, "read_command_output", ["session_id": selectedSyncID])
    }
    try full.cancelDesktopTask(selectedSyncID)
    check(syncDone.wait(timeout: .now() + 6) == .success)
    check(try full.desktopTaskOutput(selectedSyncID)["state"] as? String == "cancelled")
    print("PASS: real synchronous command live output, local-only task ID and targeted cancellation")


    let cancel = try call(full, "start_command", ["request_id": "cancel", "command": "sleep 30 & echo $! > child.pid; wait"])
    let cancelID = cancel["session_id"] as! String
    Thread.sleep(forTimeInterval: 0.2)
    _ = try call(full, "cancel_command", ["session_id": cancelID])
    check(try poll(cancelID)["state"] as? String == "cancelled")
    let pid = Int32(try String(contentsOf: root.appendingPathComponent("child.pid"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines))!
    check(kill(pid, 0) == -1 && errno == ESRCH)
    let timeout = try call(full, "start_command", ["request_id": "timeout", "command": "sleep 30", "timeout_seconds": 1])
    check(try poll(timeout["session_id"] as! String)["state"] as? String == "timed_out")
    let flood = try call(full, "start_command", ["request_id": "flood", "command": "head -c 1000000 /dev/zero | tr '\\0' x"])
    let floodID = flood["session_id"] as! String
    var page = try poll(floodID)
    check(page["truncated"] as? Bool == true)
    var total = 0
    repeat {
        total += (page["output"] as! String).utf8.count
        check((page["output"] as! String).utf8.count <= 65_536)
        if page["has_more"] as? Bool == false { break }
        page = try call(full, "read_command_output", ["session_id": floodID, "cursor": page["next_cursor"]!])
    } while true
    check(total <= 262_144 && total > 0)
    let desktop = try full.findSession(floodID).snapshot(cursor: 0, maxBytes: 262_144)
    check(desktop["has_more"] as? Bool == false)
    check((desktop["output"] as! String).utf8.count == total && total > 65_536)
    // Fill retention, then ensure retrying an evicted key cannot execute it again.
    for n in 0..<8 {
        let result = try call(full, "start_command", ["request_id": "retention-\(n)", "command": "true"])
        _ = try poll(result["session_id"] as! String)
    }
    rejects("evicted retry key") { _ = try call(full, "start_command", args) }
    let shutdown = try call(full, "start_command", ["request_id": "shutdown", "command": "sleep 30 & echo $! > shutdown.pid; wait"])
    Thread.sleep(forTimeInterval: 0.2)
    full.stopCommandSessions()
    _ = try poll(shutdown["session_id"] as! String)
    let stoppedPID = Int32(try String(contentsOf: root.appendingPathComponent("shutdown.pid"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines))!
    check(kill(stoppedPID, 0) == -1 && errno == ESRCH)
    rejects("start after shutdown") { _ = try call(full, "start_command", ["request_id": "late", "command": "true"]) }
    let synchronous = LocalTools(resolver: resolver, gitUserName: "Test", gitUserEmail: "test@example.invalid", enableCommands: true)
    let synchronousDone = DispatchSemaphore(value: 0)
    DispatchQueue.global().async {
        defer { synchronousDone.signal() }
        do {
            _ = try synchronous.call(name: "run_command", arguments: ["command": "sleep 30 & echo $! > sync-shutdown.pid; wait", "timeout_seconds": 60])
            preconditionFailure("stopped synchronous command must not report success")
        } catch { }
    }
    let synchronousPID = root.appendingPathComponent("sync-shutdown.pid")
    let synchronousDeadline = Date().addingTimeInterval(3)
    while !FileManager.default.fileExists(atPath: synchronousPID.path) && Date() < synchronousDeadline { Thread.sleep(forTimeInterval: 0.02) }
    check(FileManager.default.fileExists(atPath: synchronousPID.path))
    synchronous.stopCommandSessions()
    check(synchronousDone.wait(timeout: .now() + 3) == .success)
    let childPID = Int32(try String(contentsOf: synchronousPID, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines))!
    check(kill(childPID, 0) == -1 && errno == ESRCH)
    // Exercise the actual tool dispatcher, not only the receipt storage helper.
    let receiptLocation = root.appendingPathComponent("owner-receipts/receipts.json")
    let receiptLedger = try CommandReceiptLedger(fileURL: receiptLocation)
    let receiptTools = LocalTools(resolver: resolver, gitUserName: "Test",
                                  gitUserEmail: "test@example.invalid", enableCommands: true,
                                  receiptLedger: receiptLedger)
    defer { receiptTools.stopCommandSessions() }
    let owned = try receiptTools.call(name: "start_command", arguments: [
        "request_id": "owned-via-tool", "command": "true"], verifiedOwnerID: "oauth-client-a")
        .structuredContent
    let receiptSessionID = owned["session_id"] as! String
    let ownedDeadline = Date().addingTimeInterval(8)
    while Date() < ownedDeadline {
        let status = try receiptTools.call(name: "read_command_output",
            arguments: ["session_id": receiptSessionID]).structuredContent
        if status["state"] as? String == "exited" { break }
        Thread.sleep(forTimeInterval: 0.03)
    }
    receiptTools.stopCommandSessions()
    // Session exit becomes visible just before the asynchronous receipt
    // callback durably commits. Verify the on-disk terminal state first.
    let persistedDeadline = Date().addingTimeInterval(4)
    while receiptLedger.summary()["exited"] != 1 && Date() < persistedDeadline {
        Thread.sleep(forTimeInterval: 0.02)
    }
    check(receiptLedger.summary()["exited"] == 1)
    let restartedLedger = try CommandReceiptLedger(fileURL: receiptLocation)
    let restartedTools = LocalTools(resolver: resolver, gitUserName: "Test",
                                    gitUserEmail: "test@example.invalid", enableCommands: true,
                                    receiptLedger: restartedLedger)
    defer { restartedTools.stopCommandSessions() }
    let ownStatus = try restartedTools.call(name: "command_receipt_status",
        arguments: ["request_id": "owned-via-tool"], verifiedOwnerID: "oauth-client-a")
        .structuredContent
    check(ownStatus["state"] as? String == "exited")
    check(ownStatus["reattachable"] as? Bool == false)
    rejects("other OAuth client cannot read durable receipts") {
        _ = try restartedTools.call(name: "command_receipt_status",
            arguments: ["request_id": "owned-via-tool"], verifiedOwnerID: "oauth-client-b")
    }
    rejects("local tunnel admin cannot impersonate a verified OAuth client") {
        _ = try restartedTools.call(name: "command_receipt_status",
            arguments: ["request_id": "owned-via-tool"])
    }
    print("PASS: client-owned durable receipt status after app restart and cross-owner denial")
    let historyRoot = root.appendingPathComponent("private-history/profile")
    let history = try CommandHistoryStore(directory: historyRoot, captureContent: true)
    let historyTools = LocalTools(resolver: resolver, gitUserName: "Test",
        gitUserEmail: "test@example.invalid", enableCommands: true, workspaceScopeID: "history-test",
        workspaceName: "History test", commandHistory: history)
    defer { historyTools.stopCommandSessions() }
    _ = try historyTools.call(name: "run_command", arguments: ["command": "printf history-sync"],
        verifiedOwnerID: "oauth-history")
    _ = try historyTools.call(name: "start_command", arguments: ["request_id": "history-async",
        "command": "printf history-async; exit 7"], verifiedOwnerID: "oauth-history")
    let historyDeadline = Date().addingTimeInterval(5)
    while !history.records().contains(where: { $0.requestID == "history-async" && $0.isTerminal }) &&
        Date() < historyDeadline { Thread.sleep(forTimeInterval: 0.03) }
    historyTools.stopCommandSessions()
    let persistedHistory = try CommandHistoryStore.read(directory: historyRoot)
    check(persistedHistory.count == 2)
    check(persistedHistory.contains { $0.kind == "sync" && $0.exitCode == 0 &&
        ($0.output ?? "").contains("history-sync") })
    check(persistedHistory.contains { $0.requestID == "history-async" && $0.exitCode == 7 &&
        ($0.output ?? "").contains("history-async") && $0.clientID == "oauth-history" &&
        $0.workspaceID == "history-test" && $0.workspaceName == "History test" })
    let metadataRoot = root.appendingPathComponent("private-history/metadata")
    let metadata = try CommandHistoryStore(directory: metadataRoot, captureContent: false)
    let metadataID = try metadata.begin(workspaceID: "test", workspaceName: "Test", cwd: rootPath,
        kind: "sync", requestID: nil, clientID: nil, command: "SECRET_FIXTURE")
    try metadata.finish(id: metadataID, state: "exited", exitCode: 0, output: "SECRET_FIXTURE")
    let interruptedID = try metadata.begin(workspaceID: "test", workspaceName: "Test", cwd: rootPath,
        kind: "async", requestID: "interrupted", clientID: nil, command: "SECRET_FIXTURE")
    let reopenedHistory = try CommandHistoryStore(directory: metadataRoot, captureContent: false)
    check(reopenedHistory.records().allSatisfy { $0.command == nil && $0.output == nil })
    check(reopenedHistory.records().contains { $0.id == interruptedID && $0.state == "interrupted" })
    let boundedID = try history.begin(workspaceID: "test", workspaceName: "Test", cwd: rootPath,
        kind: "sync", requestID: nil, clientID: nil, command: "bounded")
    try history.finish(id: boundedID, state: "exited", exitCode: 0,
        output: String(repeating: "🙂", count: 20_000))
    let boundedRecord = history.records().first { $0.id == boundedID }!
    check(boundedRecord.truncated && boundedRecord.output!.utf8.count <= CommandHistoryStore.contentLimit)
    print("PASS: real sync/async history, owner/workspace metadata, opt-in content, restart interruption and Unicode bounds")
    print("PASS: synchronous command cancellation and descendant cleanup on disconnect")
    print("PASS: command streaming/cursors/exit status/retries/bounds/cancel/timeout/descendant cleanup/shutdown")
}
