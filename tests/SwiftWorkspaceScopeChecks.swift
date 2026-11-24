import Foundation

extension LocalMCPServer {
    static func verifyOAuthRevocationCancelsOwnedJobs() throws {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("ctcmcp-revoke-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let issuer = "https://mcp.example.com"
        let pairing = String(repeating: "p", count: 64)
        let oauth = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing,
            policy: "test", permissionDescription: "Files and commands")
        let registerJSON = Data(
            #"{"redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"]}"#.utf8)
        let registration = oauth.handle(method: "POST", path: "/oauth/register",
            query: "", contentType: "application/json", body: registerJSON)!
        let registered = try JSONSerialization.jsonObject(
            with: registration.body) as! [String: Any]
        let clientID = registered["client_id"] as! String
        let redirect = "https://chatgpt.com/connector_platform_oauth_redirect"
        let fields = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "redirect_uri", value: redirect),
            URLQueryItem(name: "resource", value: issuer + "/mcp"),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "code_challenge", value: String(repeating: "a", count: 43))
        ]
        var query = URLComponents()
        query.queryItems = fields
        let page = oauth.handle(method: "GET", path: "/oauth/authorize",
            query: query.percentEncodedQuery!, contentType: nil, body: Data())!
        let html = String(decoding: page.body, as: UTF8.self)
        let pattern = try NSRegularExpression(
            pattern: "name=\\\"request_id\\\" value=\\\"([^\\\"]+)\\\"")
        let match = pattern.firstMatch(in: html,
            range: NSRange(html.startIndex..., in: html))!
        let requestID = String(html[Range(match.range(at: 1), in: html)!])
        var form = URLComponents()
        form.queryItems = [
            URLQueryItem(name: "request_id", value: requestID),
            URLQueryItem(name: "pairing_code", value: pairing)
        ]
        let approved = oauth.handle(method: "POST", path: "/oauth/authorize",
            query: "", contentType: "application/x-www-form-urlencoded",
            body: Data(form.percentEncodedQuery!.utf8))!
        precondition(approved.status == 302)
        let server = try LocalMCPServer(port: 18965, allowedDirectory: base.path,
            gitUserName: "Test", gitUserEmail: "test@example.invalid",
            enableCommands: true, localAuthToken: String(repeating: "a", count: 64),
            oauth: oauth, log: { _ in })
        defer { server.stop() }
        let client = VerifiedOAuthClient(id: clientID, approvedWorkspaceIDs: ["default"])
        func call(_ name: String, _ args: [String: Any]) throws -> [String: Any] {
            let reply = server.callTool(id: 1,
                params: ["name": name, "arguments": args], modern: false,
                verifiedClient: client)
            let boundary = reply.range(of: Data("\r\n\r\n".utf8))!
            let object = try JSONSerialization.jsonObject(
                with: reply.subdata(in: boundary.upperBound..<reply.count)) as! [String: Any]
            return object["result"] as! [String: Any]
        }
        let start = try call("start_command", [
            "request_id": "revoke-owned-job", "command": "sleep 20",
            "timeout_seconds": 30])
        precondition(start["isError"] as? Bool == false)
        let sessionID = (start["structuredContent"] as! [String: Any])["session_id"] as! String
        try server.revokeDesktopOAuthClient(clientID)
        precondition(oauth.desktopApprovedClients().isEmpty)
        let stop = try call("read_command_output", ["session_id": sessionID])
        let state = (stop["structuredContent"] as! [String: Any])["state"] as? String
        precondition(["cancelled", "stopping"].contains(state))
        print("PASS: locally revoked OAuth client has its outstanding job cancelled")
    }

    static func verifyAuthenticatedClientIsolation() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("ctcmcp-client-auth-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let defaultRoot = root.appendingPathComponent("default")
        let secondRoot = root.appendingPathComponent("second")
        try FileManager.default.createDirectory(at: defaultRoot, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: secondRoot, withIntermediateDirectories: true)
        try "local-only".write(to: defaultRoot.appendingPathComponent("default.txt"),
                               atomically: true, encoding: .utf8)
        try "other-data".write(to: secondRoot.appendingPathComponent("second.txt"),
                              atomically: true, encoding: .utf8)
        let second = WorkspaceDefinition(id: "second", name: "Second", path: secondRoot.path)
        let server = try LocalMCPServer(
            port: 18964, allowedDirectory: defaultRoot.path,
            gitUserName: "Test", gitUserEmail: "test@example.invalid",
            enableCommands: true, additionalWorkspaces: [second],
            localAuthToken: String(repeating: "a", count: 64), log: { _ in })
        defer { server.stop() }
        let alice = VerifiedOAuthClient(id: "oauth-client-alice",
            approvedWorkspaceIDs: ["default"])
        let bob = VerifiedOAuthClient(id: "oauth-client-bob",
            approvedWorkspaceIDs: ["default", "second"])

        func call(_ name: String, _ args: [String: Any] = [:],
                  _ client: VerifiedOAuthClient) throws -> [String: Any] {
            let response = server.callTool(id: 1,
                params: ["name": name, "arguments": args], modern: false,
                verifiedClient: client)
            let separator = Data("\r\n\r\n".utf8)
            guard let boundary = response.range(of: separator) else {
                fatalError("No HTTP response separator")
            }
            let body = response.subdata(in: boundary.upperBound..<response.count)
            let json = try JSONSerialization.jsonObject(with: body) as! [String: Any]
            return json["result"] as! [String: Any]
        }

        func checkCall(_ name: String, _ args: [String: Any],
                       _ client: VerifiedOAuthClient, expectedError: Bool,
                       _ explanation: String) throws {
            let response = try call(name, args, client)
            precondition(response["isError"] as? Bool == expectedError, explanation)
        }

        let aliceList = try call("list_workspaces", [:], alice)
        let aliceScopes = (aliceList["structuredContent"] as! [String: Any])["workspaces"] as! [[String: Any]]
        precondition(aliceScopes.count == 1 && aliceScopes[0]["id"] as? String == "default")
        let bobList = try call("list_workspaces", [:], bob)
        let bobScopes = (bobList["structuredContent"] as! [String: Any])["workspaces"] as! [[String: Any]]
        precondition(bobScopes.count == 2, "Owner-granted client can list its approved second root")
        let scopedArgs: [String: Any] = ["workspace_id": "second",
            "tool_name": "read_file", "arguments": ["relative_path": "second.txt"]]
        try checkCall("workspace_call", scopedArgs, alice, expectedError: true,
                      "Unapproved client cannot use another workspace")
        try checkCall("workspace_call", scopedArgs, bob, expectedError: false,
                      "Appropriate scoped grant can read its approved workspace")

        let directScope: [String: Any] = ["workspace_id": "second", "relative_path": "second.txt"]
        try checkCall("read_file", directScope, alice, expectedError: true,
                      "Direct scope parameter cannot bypass OAuth grant")
        try checkCall("read_file", directScope, bob, expectedError: false,
                      "Direct scope parameter respects OAuth grant")

        let started = try call("start_command", [
            "request_id": "owned-command",
            "command": "printf owner-ready; sleep 20",
            "timeout_seconds": 25
        ], alice)
        precondition(started["isError"] as? Bool == false)
        let sessionID = (started["structuredContent"] as! [String: Any])["session_id"] as! String
        try checkCall("read_command_output", ["session_id": sessionID],
                      bob, expectedError: true,
                      "One OAuth client cannot read another client's output")
        try checkCall("cancel_command", ["session_id": sessionID],
                      bob, expectedError: true,
                      "One OAuth client cannot cancel another client's process")
        try checkCall("start_command", [
            "request_id": "owned-command", "command": "printf owner-ready; sleep 20",
            "timeout_seconds": 25
        ], bob, expectedError: true,
            "A second OAuth client cannot reuse the first client's request ID")
        try checkCall("read_command_output", ["session_id": sessionID],
                      alice, expectedError: false, "Owner may read output")
        try checkCall("cancel_command", ["session_id": sessionID],
                      alice, expectedError: false, "Owner may cancel task")
        print("PASS: per-OAuth-client authorized scopes, session ownership, cancellation and request-ID collision isolation")
    }

    static func verifyDurableCommandReceipts() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("ctcmcp-server-receipts-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let workspace = root.appendingPathComponent("workspace")
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let path = root.appendingPathComponent("journal/receipts.json")
        let ledger = try CommandReceiptLedger(fileURL: path)
        let token = String(repeating: "a", count: 64)

        func server(_ store: CommandReceiptLedger) throws -> LocalMCPServer {
            try LocalMCPServer(port: 18963, allowedDirectory: workspace.path,
                gitUserName: "Test", gitUserEmail: "test@example.invalid",
                enableCommands: true, receiptLedger: store,
                localAuthToken: token, log: { _ in })
        }
        func call(_ instance: LocalMCPServer, name: String, args: [String: Any]) throws -> [String: Any] {
            let result = instance.callTool(id: 1,
                params: ["name": name, "arguments": args], modern: false)
            let split = Data("\r\n\r\n".utf8)
            guard let range = result.range(of: split) else {
                fatalError("Missing HTTP separator")
            }
            let body = result.subdata(in: range.upperBound..<result.count)
            let json = try JSONSerialization.jsonObject(with: body) as! [String: Any]
            return json["result"] as! [String: Any]
        }

        let first = try server(ledger)
        let arguments: [String: Any] = [
            "request_id": "idempotency-restart",
            "command": "printf executed > ran-once.txt",
            "timeout_seconds": 10
        ]
        let launched = try call(first, name: "start_command", args: arguments)
        precondition(launched["isError"] as? Bool == false)
        let started = launched["structuredContent"] as! [String: Any]
        let sessionID = started["session_id"] as! String
        let deadline = Date().addingTimeInterval(8)
        while Date() < deadline {
            let result = try call(first, name: "read_command_output",
                args: ["session_id": sessionID])
            let status = result["structuredContent"] as! [String: Any]
            if status["state"] as? String == "exited" { break }
            Thread.sleep(forTimeInterval: 0.04)
        }
        first.stop()

        let secondLedger = try CommandReceiptLedger(fileURL: path)
        let second = try server(secondLedger)
        defer { second.stop() }
        let duplicate = try call(second, name: "start_command", args: arguments)
        precondition(duplicate["isError"] as? Bool == true,
                     "Old request ID may never re-execute after server restart")
        let marker = workspace.appendingPathComponent("ran-once.txt")
        let markerValue = try String(contentsOf: marker, encoding: .utf8)
        precondition(markerValue == "executed")
        precondition(secondLedger.summary()["exited"] == 1 ||
                     secondLedger.summary()["interrupted"] == 1)
        print("PASS: server-scoped command receipt rejects replay after runtime restart")
    }

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
        let schema = server.publishedToolDefinitions.first { $0["name"] as? String == "read_file" }!
        let input = schema["inputSchema"] as! [String: Any]
        precondition((input["properties"] as! [String: Any])["workspace_id"] != nil)
        precondition(!(input["required"] as! [String]).contains("workspace_id"))
        let directScoped = try call("read_file", ["workspace_id": "second", "relative_path": "only-second.txt"])
        precondition(directScoped["isError"] as? Bool == false)
        precondition(String(describing: directScoped["structuredContent"]).contains("only-in-second"))
        for invalid: Any in ["unknown", "", 1, NSNull()] {
            let denied = try call("read_file", ["workspace_id": invalid, "relative_path": "only-default.txt"])
            precondition(denied["isError"] as? Bool == true)
        }
        let nestedScope = try call("workspace_call", ["workspace_id": "second", "tool_name": "read_file",
            "arguments": ["workspace_id": "default", "relative_path": "only-default.txt"]])
        precondition(nestedScope["isError"] as? Bool == true)
        let defaultWrapper = try call("workspace_call", ["workspace_id": "default", "tool_name": "read_file",
            "arguments": ["relative_path": "only-default.txt"]])
        precondition(defaultWrapper["isError"] as? Bool == false)
        let again = try call("read_file", ["relative_path": "only-default.txt"])
        precondition(again["isError"] as? Bool == false, "Direct routing must not change the global default")
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
                : try call("start_command", args.merging(["workspace_id": "second"]) { _, new in new })
            precondition(response["isError"] as? Bool == false)
            let payload = response["structuredContent"] as! [String: Any]
            let started = payload
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
                : try call("cancel_command", arguments.merging(["workspace_id": scope]) { _, new in new })
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
