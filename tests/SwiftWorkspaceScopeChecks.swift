import Foundation

extension LocalMCPServer {
    static func verifyApprovedWorkspaces() throws {
        let temp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("chat-to-computer-scope-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: temp) }
        let defaultRoot = temp.appendingPathComponent("default")
        let secondRoot = temp.appendingPathComponent("second")
        try FileManager.default.createDirectory(at: defaultRoot, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: secondRoot, withIntermediateDirectories: true)
        try Data("only-in-default".utf8).write(to: defaultRoot.appendingPathComponent("only-default.txt"))
        try Data("only-in-second".utf8).write(to: secondRoot.appendingPathComponent("only-second.txt"))
        let token = String(repeating: "a", count: 64)
        let scoped = WorkspaceDefinition(id: "second", name: "Second", path: secondRoot.path)

        func setup(_ workspaces: [WorkspaceDefinition]) throws -> LocalMCPServer {
            try LocalMCPServer(port: 18962, allowedDirectory: defaultRoot.path,
                gitUserName: "Test", gitUserEmail: "test@example.invalid",
                enableCommands: true, additionalWorkspaces: workspaces,
                localAuthToken: token, log: { _ in })
        }
        let server = try setup([scoped])
        defer { server.stop() }

        func call(_ name: String, _ arguments: [String: Any] = [:]) throws -> [String: Any] {
            let response = server.callTool(id: 1, params: ["name": name, "arguments": arguments], modern: false)
            let headerSeparator = Data("\r\n\r\n".utf8)
            guard let range = response.range(of: headerSeparator) else {
                fatalError("Missing HTTP header separator")
            }
            let json = try JSONSerialization.jsonObject(with: response.subdata(in: range.upperBound..<response.count)) as! [String: Any]
            return json["result"] as! [String: Any]
        }
        let listed = try call("list_workspaces")
        let workspaces = (listed["structuredContent"] as! [String: Any])["workspaces"] as! [[String: Any]]
        precondition(workspaces.count == 2)
        precondition(workspaces.map { $0["id"] as! String } == ["default", "second"])
        precondition(server.publishedToolDefinitions.contains { $0["name"] as? String == "workspace_call" })

        let direct = try call("read_file", ["relative_path": "only-default.txt"])
        precondition(direct["isError"] as? Bool == false)
        let secondary = try call("workspace_call", ["workspace_id": "second",
            "tool_name": "read_file", "arguments": ["relative_path": "only-second.txt"]])
        precondition(secondary["isError"] as? Bool == false)
        let secondaryResult = (secondary["structuredContent"] as! [String: Any])["result"] as! [String: Any]
        precondition(String(describing: secondaryResult).contains("only-in-second"))
        let unauthorized = try call("workspace_call", ["workspace_id": "unapproved",
            "tool_name": "read_file", "arguments": ["relative_path": "only-default.txt"]])
        precondition(unauthorized["isError"] as? Bool == true)
        let traversal = try call("workspace_call", ["workspace_id": "second",
            "tool_name": "read_file", "arguments": ["relative_path": "../default/only-default.txt"]])
        precondition(traversal["isError"] as? Bool == true)
        let recursive = try call("workspace_call", ["workspace_id": "second",
            "tool_name": "workspace_call", "arguments": [:]])
        precondition(recursive["isError"] as? Bool == true)
        print("PASS: explicit workspace routing, default compatibility, cross-root containment, unknown IDs")

        // Two different roots share one bounded process-wide command admission.
        let limit = min(16, max(4, Int(ProcessInfo.processInfo.environment["FILEMCP_MAX_CONCURRENT_COMMANDS"] ?? "") ?? 8))
        let all = try call("workspace_call", ["workspace_id": "second",
            "tool_name": "command_capacity_status", "arguments": [:]])
        let capacity = (all["structuredContent"] as! [String: Any])["result"] as! [String: Any]
        precondition(capacity["max_concurrent_commands"] as? Int == limit)
        print("PASS: workspace command tools retain configured capacity")

        // All approved roots must share the same overall command cap, even
        // though requests are routed to independently scoped tool dispatchers.
        var ids: [(String, String)] = []
        for index in 0..<limit {
            let root = index % 2 == 0 ? defaultRoot : secondRoot
            let dir = root.appendingPathComponent("slot-\(index)")
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let args: [String: Any] = [
                "request_id": "budget-\(index)", "cwd": "slot-\(index)",
                "command": "sleep 4", "timeout_seconds": 10]
            let response = index % 2 == 0
                ? try call("start_command", args)
                : try call("workspace_call", ["workspace_id": "second",
                    "tool_name": "start_command", "arguments": args])
            precondition(response["isError"] as? Bool == false)
            let payload = response["structuredContent"] as! [String: Any]
            let started = (index % 2 == 0 ? payload :
                payload["result"] as! [String: Any])
            ids.append((index % 2 == 0 ? "default" : "second",
                        started["session_id"] as! String))
        }
        try FileManager.default.createDirectory(
            at: secondRoot.appendingPathComponent("overlimit"), withIntermediateDirectories: true)
        let over = try call("workspace_call", ["workspace_id": "second",
            "tool_name": "start_command", "arguments": [
                "request_id": "overbudget", "command": "true", "cwd": "overlimit"]])
        precondition(over["isError"] as? Bool == true)
        for (scope, id) in ids {
            let arguments: [String: Any] = ["session_id": id]
            let result = scope == "default"
                ? try call("cancel_command", arguments)
                : try call("workspace_call", ["workspace_id": scope,
                    "tool_name": "cancel_command", "arguments": arguments])
            precondition(result["isError"] as? Bool == false)
        }
        print("PASS: shared 4–16 budget across independently scoped workspaces")

        // An explicitly approved root must never overlap the default even
        // through symlinks. A malformed root does not get created implicitly.
        let nested = defaultRoot.appendingPathComponent("nested")
        try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true)
        do {
            _ = try setup([WorkspaceDefinition(id: "nested", name: "Nested", path: nested.path)])
            fatalError("Unexpected acceptance of nested workspace")
        } catch { print("PASS: nested workspace rejected") }
        let alias = temp.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: defaultRoot)
        do {
            _ = try setup([WorkspaceDefinition(id: "alias", name: "Alias", path: alias.path)])
            fatalError("Unexpected acceptance of symlink-alias workspace")
        } catch { print("PASS: symlink alias workspace rejected") }
    }
}
