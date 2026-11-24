using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task TestWorkspaceScopeAsync(string baseRoot)
    {
        var root = Path.Combine(baseRoot, "scoped-workspaces");
        var first = Path.Combine(root, "default");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "in-default.txt"), "default-only");
        File.WriteAllText(Path.Combine(second, "in-second.txt"), "second-only");
        var secondDefinition = new ApprovedWorkspace("second", "Second", second);
        var port = FreePort();
        var token = new string('a', 64);
        var auditRoot = Path.Combine(root, "audit", "profile");
        var audit = new ActivityAuditStore(auditRoot);

        await using var server = new LocalMcpServer((ushort)port, first, "Test",
            "test@example.invalid", true, token, _ => { },
            [secondDefinition], maxConcurrentCommands: 4, activityAudit: audit);
        await server.StartAsync();
        async Task<JsonObject> Rpc(string name, JsonObject? args = null)
        {
            var body = new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 123, ["method"] = "tools/call",
                ["params"] = new JsonObject {
                    ["name"] = name,
                    ["arguments"] = args ?? new JsonObject()
                }
            };
            var response = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), body.ToJsonString());
            return JsonNode.Parse(HttpBody(response))!["result"]!.AsObject();
        }
        var restrictedTools = new LocalTools(second, "Test", "test@example.invalid", true,
            enableGitPush: true, enableCodexHistory: true, allowWrite: false);
        Assert(restrictedTools.HasTool("read_file") && restrictedTools.HasTool("workspace_context"), "read-only tools available");
        foreach (var tool in new[] { "write_file", "delete_file", "edit_file", "git_init", "git_push", "run_command", "start_command", "save_conversation_to_codex" })
            Assert(!restrictedTools.HasTool(tool), "restricted scope does not advertise " + tool);
        await AssertThrowsAsync(() => restrictedTools.CallAsync("write_file", Obj(("relative_path", "forbidden.txt"), ("content", "no"))),
            "tool", "read-only workspace rejects mutation");
        var scopedPort = FreePort();
        await using var restrictedServer = new LocalMcpServer((ushort)scopedPort, first, "Test",
            "test@example.invalid", true, token, _ => { }, [secondDefinition],
            defaultWorkspacePermissions: WorkspacePermissions.ReadOnly);
        await restrictedServer.StartAsync();
        async Task<JsonObject> RestrictedRpc(string name, JsonObject args)
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 123, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
            var response = await SendHttpAsync(scopedPort, "POST", "/mcp", AuthHeaders(token), body.ToJsonString());
            return JsonNode.Parse(HttpBody(response))!["result"]!.AsObject();
        }
        Assert((await RestrictedRpc("write_file", Obj(("relative_path", "forbidden.txt"), ("content", "no"))))["isError"]!.GetValue<bool>(),
            "default read-only workspace rejects mutation");
        Assert(!(await RestrictedRpc("write_file", Obj(("workspace_id", "second"), ("relative_path", "allowed.txt"), ("content", "ok"))))["isError"]!.GetValue<bool>(),
            "writable secondary scope remains discoverable");
        var listed = await Rpc("list_workspaces");
        Assert(listed["isError"]!.GetValue<bool>() == false, "list workspaces success");
        var workspaces = listed["structuredContent"]!["workspaces"]!.AsArray();
        Assert(workspaces.Count == 2 && workspaces[1]!["id"]!.GetValue<string>() == "second",
            "workspace list is user approved");
        var direct = await Rpc("read_file", Obj(("relative_path", "in-default.txt")));
        Assert(direct["isError"]!.GetValue<bool>() == false, "legacy default workspace supported");
        var directScope = await Rpc("read_file", Obj(("workspace_id", "second"), ("relative_path", "in-second.txt")));
        Assert(!directScope["isError"]!.GetValue<bool>() && directScope["structuredContent"]!.ToJsonString().Contains("second-only"),
            "direct workspace parameter selects second scope");
        _ = await Rpc("read_file", Obj(("workspace_id", "second"), ("relative_path", "SECRET_FIXTURE")));
        var audited = ActivityAuditStore.Read(auditRoot);
        Assert(audited.Any(item => item.Tool == "read_file" && item.Workspace == "second" && item.Result == "ok"), "successful scoped audit");
        Assert(audited.Any(item => item.Tool == "read_file" && item.Workspace == "second" && item.Result == "error"), "failed scoped audit");
        var auditText = File.ReadAllText(Path.Combine(auditRoot, "current.jsonl"));
        Assert(!auditText.Contains("SECRET_FIXTURE") && !auditText.Contains("second-only"), "audit omits arguments and result text");
        foreach (var invalid in new JsonNode?[] { JsonValue.Create("unknown"), JsonValue.Create(""), JsonValue.Create(1), null })
        {
            var args = Obj(("relative_path", "in-default.txt"));
            args["workspace_id"] = invalid;
            Assert((await Rpc("read_file", args))["isError"]!.GetValue<bool>(), "invalid direct scope rejected");
        }
        var nestedScope = await Rpc("workspace_call", Obj(("workspace_id", "second"), ("tool_name", "read_file"),
            ("arguments", Obj(("workspace_id", "default"), ("relative_path", "in-default.txt")))));
        Assert(nestedScope["isError"]!.GetValue<bool>(), "nested scope cannot override wrapper");
        var defaultWrapper = await Rpc("workspace_call", Obj(("workspace_id", "default"), ("tool_name", "read_file"),
            ("arguments", Obj(("relative_path", "in-default.txt")))));
        Assert(!defaultWrapper["isError"]!.GetValue<bool>(), "wrapper supports explicit default");
        Assert(!(await Rpc("read_file", Obj(("relative_path", "in-default.txt"))))["isError"]!.GetValue<bool>(),
            "direct scope routing does not change global default");
        var scoped = await Rpc("workspace_call", Obj(("workspace_id", "second"),
            ("tool_name", "read_file"),
            ("arguments", Obj(("relative_path", "in-second.txt")))));
        Assert(scoped["isError"]!.GetValue<bool>() == false, "read in approved workspace");
        Assert(scoped["structuredContent"]!["result"]!.ToJsonString().Contains("second-only"),
            "scoped content matches approved second root");
        var denied = await Rpc("workspace_call", Obj(("workspace_id", "second"),
            ("tool_name", "read_file"),
            ("arguments", Obj(("relative_path", "../default/in-default.txt")))));
        Assert(denied["isError"]!.GetValue<bool>(), "traversal outside scope rejected");
        var unknown = await Rpc("workspace_call", Obj(("workspace_id", "unknown"),
            ("tool_name", "read_file"),
            ("arguments", Obj(("relative_path", "in-default.txt")))));
        Assert(unknown["isError"]!.GetValue<bool>(), "unknown workspace rejected");
        var nested = Path.Combine(first, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            await using var unsafeServer = new LocalMcpServer((ushort)FreePort(), first, "",
                "", false, token, _ => { },
                [new ApprovedWorkspace("nested", "Nested", nested)]);
            throw new Exception("Overlapping workspace unexpectedly accepted");
        }
        catch (FileMcpException ex)
        {
            Assert(ex.Message.Contains("overlap"), "nested root rejected");
        }

        // Four long-running jobs across two scopes must share one global limit.
        // Target distinct child cwd so the cooperative collision guard allows them.
        var sessions = new List<(string Scope, string Id)>();
        for (var n = 0; n < 4; n++)
        {
            var isDefault = n % 2 == 0;
            var scopeRoot = isDefault ? first : second;
            var path = "job-" + n;
            Directory.CreateDirectory(Path.Combine(scopeRoot, path));
            var args = Obj(("request_id", "workspace-job-" + n),
                ("command", "Start-Sleep -Seconds 6"), ("cwd", path), ("timeout_seconds", 15));
            if (!isDefault) args["workspace_id"] = "second";
            var response = await Rpc("start_command", args);
            Assert(!response["isError"]!.GetValue<bool>(), "parallel job admitted");
            var content = response["structuredContent"]!;

            sessions.Add((isDefault ? "default" : "second",
                content["session_id"]!.GetValue<string>()));
        }
        var cap = await Rpc("command_capacity_status");
        Assert(cap["structuredContent"]!["active_total"]!.GetValue<int>() == 4,
            "global capacity counts both workspace roots");
        var scopedCap = await Rpc("command_capacity_status", Obj(("workspace_id", "second")));
        Assert(scopedCap["structuredContent"]!["active_total"]!.GetValue<int>() == 4 &&
            scopedCap["structuredContent"]!["available_slots"]!.GetValue<int>() == 0,
            "scoped capacity reports same global occupancy");
        Directory.CreateDirectory(Path.Combine(second, "overflow"));
        var over = await Rpc("workspace_call", Obj(("workspace_id", "second"),
            ("tool_name", "start_command"),
            ("arguments", Obj(("request_id", "overflow"), ("command", "exit 0"),
                ("cwd", "overflow")))));
        Assert(over["isError"]!.GetValue<bool>(), "cross-workspace capacity exceeded");
        foreach (var (scopeID, id) in sessions)
        {
            var response = scopeID == "default"
                ? await Rpc("cancel_command", Obj(("session_id", id)))
                : await Rpc("cancel_command", Obj(("workspace_id", scopeID), ("session_id", id)));
            Assert(!response["isError"]!.GetValue<bool>(), "targeted workspace task cancellation");
        }
        Console.WriteLine("windows-workspace-scope-and-global-concurrency: ok");
    }
}
