using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static bool IsProcessRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task TestAgentToolsAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "agent-tools");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var safe = new LocalTools(root, "Test", "test@example.invalid", false);
        var full = new LocalTools(root, "Test", "test@example.invalid", true,
            enableGitPush: true, enableCodexHistory: true);
        async Task<JsonObject> Call(LocalTools tools, string name, JsonObject? args = null) => (await tools.CallAsync(name, args ?? new JsonObject())).StructuredContent;
        async Task<JsonObject> Poll(string id, int cursor = 0)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(12))
            {
                var value = await Call(full, "read_command_output", Obj(("session_id", id), ("cursor", cursor)));
                if (value["state"]!.GetValue<string>() is not ("queued" or "running" or "stopping")) return value;
                await Task.Delay(30);
            }
            throw new Exception("Command did not finish");
        }
        try
        {
            Assert(safe.HasTool("edit_file") && safe.HasTool("workspace_context") && !safe.HasTool("start_command"), "agent tool permissions");
            await Call(safe, "create_directory", Obj(("relative_path", "created/sub")));
            Assert(Directory.Exists(Path.Combine(root, "created", "sub")), "create_directory makes nested folders");
            File.WriteAllText(Path.Combine(root, "created", "source.txt"), "safe original");
            var fileInfo = await Call(safe, "file_info", Obj(("relative_path", "created/source.txt")));
            Assert(fileInfo["kind"]!.GetValue<string>() == "file" &&
                fileInfo["size_bytes"]!.GetValue<long>() == 13, "file_info reads bounded file metadata");
            await Call(safe, "copy_file", Obj(("source_path", "created/source.txt"),
                ("destination_path", "created/sub/copy.txt")));
            Assert(File.ReadAllText(Path.Combine(root, "created", "sub", "copy.txt")) == "safe original", "copy_file");
            await AssertThrowsAsync(() => Call(safe, "copy_file",
                Obj(("source_path", "created/source.txt"), ("destination_path", "created/sub/copy.txt"))),
                "exists", "copy_file refuses overwrite");
            await Call(safe, "move_file", Obj(("source_path", "created/sub/copy.txt"),
                ("destination_path", "created/renamed.txt")));
            Assert(File.Exists(Path.Combine(root, "created", "renamed.txt")) &&
                !File.Exists(Path.Combine(root, "created", "sub", "copy.txt")), "move_file");
            await AssertThrowsAsync(() => Call(safe, "move_file",
                Obj(("source_path", "created/renamed.txt"), ("destination_path", "created/source.txt"))),
                "exists", "move_file refuses overwrite");
            await AssertThrowsAsync(() => Call(safe, "create_directory", Obj(("relative_path", "../escape"))),
                "outside", "create_directory containment");
            await AssertThrowsAsync(() => Call(safe, "copy_file",
                Obj(("source_path", "../outside.txt"), ("destination_path", "created/escape.txt"))),
                "outside", "copy_file containment");
            var file = Path.Combine(root, "nested", "code.txt");
            File.WriteAllText(file, "héllo 👋\r\nkeep\r\n", new UTF8Encoding(false));
            var edit = Obj(("relative_path", "nested/code.txt"), ("old_text", "héllo 👋"), ("new_text", "xin chào"), ("dry_run", true));
            var preview = await Call(safe, "edit_file", edit);
            Assert(!preview["applied"]!.GetValue<bool>() && File.ReadAllText(file).StartsWith("héllo"), "edit preview does not write");
            edit["dry_run"] = false; edit["expected_sha256"] = preview["before_sha256"]!.GetValue<string>();
            await Call(safe, "edit_file", edit);
            Assert(File.ReadAllText(file) == "xin chào\r\nkeep\r\n", "edit preserves Unicode and CRLF");
            await AssertThrowsAsync(() => Call(safe, "edit_file", edit), "conflict", "stale edit refused");
            File.WriteAllText(Path.Combine(root, "ambiguous.txt"), "aaa");
            await AssertThrowsAsync(() => Call(safe, "edit_file", Obj(("relative_path", "ambiguous.txt"), ("old_text", "aa"), ("new_text", "x"))), "more than once", "overlapping edits refused");
            await AssertThrowsAsync(() => Call(safe, "edit_file", Obj(("relative_path", "../outside.txt"), ("old_text", "a"), ("new_text", "x"))), "outside", "edit path escape refused");
            await AssertThrowsAsync(() => Call(safe, "edit_file", Obj(("relative_path", "ambiguous.txt"), ("old_text", ""), ("new_text", "x"))), "empty", "empty match refused");
            File.WriteAllBytes(Path.Combine(root, "binary"), [0xff, 0xfe]);
            await AssertThrowsAsync(() => Call(safe, "edit_file", Obj(("relative_path", "binary"), ("old_text", "a"), ("new_text", "x"))), "UTF-8", "invalid UTF8 refused");
            File.WriteAllText(Path.Combine(root, "patch-a.txt"), "alpha\nbeta\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "patch-b.txt"), "one\n", new UTF8Encoding(false));
            var patchChanges = new JsonArray(
                Obj(("relative_path", "patch-a.txt"), ("old_text", "alpha"), ("new_text", "ALPHA")),
                Obj(("relative_path", "patch-a.txt"), ("old_text", "beta"), ("new_text", "BETA")),
                Obj(("relative_path", "patch-b.txt"), ("old_text", "one"), ("new_text", "ONE")));
            var patchPreview = await Call(safe, "apply_patch", Obj(("changes", patchChanges.DeepClone()), ("dry_run", true)));
            Assert(!patchPreview["applied"]!.GetValue<bool>() && patchPreview["file_count"]!.GetValue<int>() == 2 && patchPreview["change_count"]!.GetValue<int>() == 3, "patch preview summary");
            Assert(File.ReadAllText(Path.Combine(root, "patch-a.txt")) == "alpha\nbeta\n", "patch preview does not write");
            var guarded = patchChanges.DeepClone().AsArray();
            guarded[0]!.AsObject()["expected_sha256"] = patchPreview["files"]!.AsArray()[0]!["before_sha256"]!.GetValue<string>();
            await Call(safe, "apply_patch", Obj(("changes", guarded)));
            Assert(File.ReadAllText(Path.Combine(root, "patch-a.txt")) == "ALPHA\nBETA\n" && File.ReadAllText(Path.Combine(root, "patch-b.txt")) == "ONE\n", "multi-file patch applies in order");
            File.WriteAllText(Path.Combine(root, "patch-atomic-a.txt"), "keep-a");
            File.WriteAllText(Path.Combine(root, "patch-atomic-b.txt"), "keep-b");
            await AssertThrowsAsync(() => Call(safe, "apply_patch", Obj(("changes", new JsonArray(
                Obj(("relative_path", "patch-atomic-a.txt"), ("old_text", "keep-a"), ("new_text", "changed-a")),
                Obj(("relative_path", "patch-atomic-b.txt"), ("old_text", "missing"), ("new_text", "changed-b")))))), "not found", "patch validates before writes");
            Assert(File.ReadAllText(Path.Combine(root, "patch-atomic-a.txt")) == "keep-a", "failed patch leaves earlier files untouched");
            var oversizedPatch = new JsonArray();
            for (var index = 0; index < 7; index++)
            {
                var name = $"patch-large-{index}.txt";
                File.WriteAllText(Path.Combine(root, name), new string('x', 4_600_000) + "needle", new UTF8Encoding(false));
                oversizedPatch.Add(Obj(("relative_path", name), ("old_text", "needle"), ("new_text", "done")));
            }
            await AssertThrowsAsync(() => Call(safe, "apply_patch", Obj(("changes", oversizedPatch), ("dry_run", true))), "32 MB aggregate", "patch aggregate budget");
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "root guidance");
            File.WriteAllText(Path.Combine(root, "nested", "AGENTS.md"), "nested guidance");
            File.WriteAllText(Path.Combine(root, "nested", "package.json"), "{\"scripts\":{\"test\":\"echo test\"}}");
            var context = await Call(safe, "workspace_context", Obj(("path", "nested")));
            Assert(context["files"]!.AsArray().Select(value => value!["path"]!.GetValue<string>()).SequenceEqual(["AGENTS.md", "nested/AGENTS.md", "nested/package.json"]), "scoped instructions and manifests");
            File.WriteAllText(Path.Combine(root, "nested", "AGENTS.md"), new string('x', 20_000));
            context = await Call(safe, "workspace_context", Obj(("path", "nested")));
            Assert(context["files"]!.AsArray()[1]!["truncated"]!.GetValue<bool>(), "context is bounded");
            var args = Obj(("request_id", "stream"), ("command", "[Console]::Write('first'); Start-Sleep -Seconds 2; [Console]::Write('second'); [Console]::Error.Write('error'); exit 7"), ("timeout_seconds", 10));
            var started = await Call(full, "start_command", args);
            var id = started["session_id"]!.GetValue<string>();
            Assert((await Call(full, "start_command", args))["session_id"]!.GetValue<string>() == id, "idempotent retry");
            await AssertThrowsAsync(() => Call(full, "start_command", Obj(("request_id", "stream"), ("command", "exit 0"))), "different", "retry mismatch refused");
            await AssertThrowsAsync(() => Call(full, "edit_file", edit), "active", "mutations wait for job");
            var watch = Stopwatch.StartNew(); JsonObject partial;
            do
            {
                partial = await Call(full, "read_command_output", Obj(("session_id", id)));
                if (partial["output"]!.GetValue<string>().Contains("first")) break;
                await Task.Delay(30);
            } while (watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert(partial["state"]!.GetValue<string>() == "running" && partial["output"]!.GetValue<string>().Contains("first"), "output arrives before exit");
            var completed = await Poll(id, partial["next_cursor"]!.GetValue<int>());
            Assert(completed["exit_code"]!.GetValue<int>() == 7 && completed["output"]!.GetValue<string>().Contains("second") && !completed["output"]!.GetValue<string>().Contains("first"), "cursor and nonzero exit");
            await AssertThrowsAsync(() => Call(full, "read_command_output", Obj(("session_id", id), ("cursor", 999999))), "cursor", "future cursor refused");
            // One local runtime must admit the same bounded independent worktree
            // parallelism as macOS, not merely advertise the same MCP tool name.
            var runningSlots = new List<string>();
            for (var n = 0; n < 8; n++)
            {
                Directory.CreateDirectory(Path.Combine(root, "sessions", $"slot{n}"));
                if (n == 1)
                {
                    Directory.CreateDirectory(Path.Combine(root, "sessions", "slot0", "child"));
                    await AssertThrowsAsync(() => Call(full, "start_command",
                        Obj(("request_id", "overlap-reject"), ("command", "Start-Sleep -Seconds 30"),
                            ("cwd", "sessions/slot0/child"))), "occupies", "nested session overlaps existing task");
                }
                var slot = await Call(full, "start_command", Obj(
                    ("request_id", $"parallel-{n}"), ("command", "Start-Sleep -Seconds 30"),
                    ("cwd", $"sessions/slot{n}")));
                Assert(slot["state"]!.GetValue<string>() == "running", "independent command slot admitted");
                runningSlots.Add(slot["session_id"]!.GetValue<string>());
            }
            var utilization = await Call(full, "command_capacity_status");
            Assert(utilization["active_total"]!.GetValue<int>() == 8 &&
                utilization["available_slots"]!.GetValue<int>() == 0, "8 independent sessions share configured capacity");
            Directory.CreateDirectory(Path.Combine(root, "sessions", "slot8"));
            await AssertThrowsAsync(() => Call(full, "start_command", Obj(
                ("request_id", "parallel-over-cap"), ("command", "Start-Sleep -Seconds 30"),
                ("cwd", "sessions/slot8"))), "limit", "ninth session refused");
            await Call(full, "cancel_command", Obj(("session_id", runningSlots[0])));
            Assert((await Poll(runningSlots[0]))["state"]!.GetValue<string>() == "cancelled", "first parallel session cancelled independently");
            var replacement = await Call(full, "start_command", Obj(
                ("request_id", "parallel-replacement"), ("command", "Start-Sleep -Seconds 30"),
                ("cwd", "sessions/slot8")));
            Assert(replacement["state"]!.GetValue<string>() == "running", "released slot is reusable");
            for (var n = 1; n < runningSlots.Count; n++)
                await Call(full, "cancel_command", Obj(("session_id", runningSlots[n])));
            await Call(full, "cancel_command", Obj(("session_id", replacement["session_id"]!.GetValue<string>())));
            for (var n = 1; n < runningSlots.Count; n++)
                Assert((await Poll(runningSlots[n]))["state"]!.GetValue<string>() == "cancelled", "parallel cancellation is isolated");
            Assert((await Poll(replacement["session_id"]!.GetValue<string>()))["state"]!.GetValue<string>() == "cancelled", "replacement cancelled");
            utilization = await Call(full, "command_capacity_status");
            Assert(utilization["active_total"]!.GetValue<int>() == 0, "all independent sessions released");
            var shortTask = full.CallAsync("run_command", Obj(("command", "[Console]::Write('desktop-sync-live'); Start-Sleep -Seconds 20"),
                ("timeout_seconds", 25)), verifiedOwnerID: "desktop-sync-client");
            watch.Restart();
            DesktopCommandInfo? shortRow = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                var row = full.DesktopCommandSnapshots("default", "Default").FirstOrDefault(item => item.Active && item.SessionId.StartsWith("sync-"));
                if (row is not null && full.DesktopCommandOutput(row.SessionId)["output"]!.GetValue<string>().Contains("desktop-sync-live"))
                { shortRow = row; break; }
                await Task.Delay(50);
            }
            Assert(shortRow is not null, "short command exposes output before completion");
            var shortID = shortRow!.SessionId;
            await AssertThrowsAsync(() => Call(full, "read_command_output", Obj(("session_id", shortID))),
                "session", "remote long-command API cannot read local short task ID");
            full.CancelDesktopCommand(shortID);
            try { await shortTask; throw new Exception("Cancelled short command reported success"); }
            catch (OperationCanceledException) {}
            Assert(full.DesktopCommandOutput(shortID)["state"]!.GetValue<string>() == "cancelled", "short command targeted cancellation");
            var cancel = await Call(full, "start_command", Obj(("request_id", "cancel"), ("command", "$child = Start-Process ping.exe -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden; $child.Id | Set-Content child.pid; Start-Sleep -Seconds 30")));
            var cancelId = cancel["session_id"]!.GetValue<string>();
            watch.Restart();
            while (!File.Exists(Path.Combine(root, "child.pid")) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(30);
            var pid = int.Parse(File.ReadAllText(Path.Combine(root, "child.pid")).Trim());
            await Call(full, "cancel_command", Obj(("session_id", cancelId)));
            Assert((await Poll(cancelId))["state"]!.GetValue<string>() == "cancelled", "cancel reaches terminal state");
            Assert(!IsProcessRunning(pid), "cancel kills descendants");
            var timeout = await Call(full, "start_command", Obj(("request_id", "timeout"), ("command", "Start-Sleep -Seconds 30"), ("timeout_seconds", 1)));
            Assert((await Poll(timeout["session_id"]!.GetValue<string>()))["state"]!.GetValue<string>() == "timed_out", "timeout state");
            var flood = await Call(full, "start_command", Obj(("request_id", "flood"), ("command", "[Console]::Write(('x' * 1000000))")));
            var floodId = flood["session_id"]!.GetValue<string>(); var page = await Poll(floodId); var total = 0;
            Assert(page["truncated"]!.GetValue<bool>(), "bounded output marks truncation");
            while (true)
            {
                var size = Encoding.UTF8.GetByteCount(page["output"]!.GetValue<string>()); total += size;
                Assert(size <= 65_536, "output page bounded");
                if (!page["has_more"]!.GetValue<bool>()) break;
                page = await Call(full, "read_command_output", Obj(("session_id", floodId), ("cursor", page["next_cursor"]!.GetValue<int>())));
            }
            Assert(total is > 0 and <= 262_144, "retained output bounded");
            var desktop = full.DesktopCommandOutput(floodId);
            Assert(!desktop["has_more"]!.GetValue<bool>() &&
                Encoding.UTF8.GetByteCount(desktop["output"]!.GetValue<string>()) == total && total > 65_536,
                "desktop sees entire retained output, not first remote page");
            for (var n = 0; n < 8; n++)
            {
                var result = await Call(full, "start_command", Obj(("request_id", "retention-" + n), ("command", "exit 0")));
                await Poll(result["session_id"]!.GetValue<string>());
            }
            await AssertThrowsAsync(() => Call(full, "start_command", args), "expired", "evicted retry cannot rerun");
            var stop = await Call(full, "start_command", Obj(("request_id", "shutdown"), ("command", "$PID | Set-Content shutdown.pid; Start-Sleep -Seconds 30")));
            watch.Restart();
            while (!File.Exists(Path.Combine(root, "shutdown.pid")) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(30);
            var stopPid = int.Parse(File.ReadAllText(Path.Combine(root, "shutdown.pid")).Trim());
            full.StopCommandSessions();
            Assert(!IsProcessRunning(stopPid), "runtime shutdown kills process");
            await AssertThrowsAsync(() => Call(full, "start_command", Obj(("request_id", "late"), ("command", "exit 0"))), "stopped", "no jobs after shutdown");
        }
        finally { full.StopCommandSessions(); }
        var historyRoot = Path.Combine(testRoot, "private-history", "profile");
        var history = new CommandHistoryStore(historyRoot, true);
        var historyTools = new LocalTools(root, "Test", "test@example.invalid", true,
            workspaceScopeID: "history-test", workspaceName: "History test", commandHistory: history);
        try
        {
            await historyTools.CallAsync("run_command", Obj(("command", "Write-Output history-sync")),
                verifiedOwnerID: "oauth-history");
            await historyTools.CallAsync("start_command", Obj(("request_id", "history-async"),
                ("command", "Write-Output history-async; exit 7")), verifiedOwnerID: "oauth-history");
            var historyWatch = Stopwatch.StartNew();
            while (!history.Records().Any(r => r.RequestId == "history-async" && r.IsTerminal) &&
                historyWatch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(30);
            var persisted = CommandHistoryStore.Read(historyRoot);
            Assert(persisted.Count == 2, "both execution paths record history");
            Assert(persisted.Any(r => r.Kind == "sync" && r.ExitCode == 0 &&
                r.Output!.Contains("history-sync")), "sync history output");
            Assert(persisted.Any(r => r.RequestId == "history-async" && r.ExitCode == 7 &&
                r.Output!.Contains("history-async") && r.ClientId == "oauth-history" &&
                r.WorkspaceId == "history-test" && r.WorkspaceName == "History test"), "async history owner and output");
        }
        finally { historyTools.StopCommandSessions(); }
        var metadataRoot = Path.Combine(testRoot, "private-history", "metadata");
        var metadata = new CommandHistoryStore(metadataRoot, false);
        var metadataId = metadata.Begin("test", "Test", root, "sync", null, null, "SECRET_FIXTURE");
        metadata.Finish(metadataId, "exited", 0, "SECRET_FIXTURE");
        var interruptedId = metadata.Begin("test", "Test", root, "async", "interrupted", null, "SECRET_FIXTURE");
        var reopenedHistory = new CommandHistoryStore(metadataRoot, false);
        Assert(reopenedHistory.Records().All(r => r.Command is null && r.Output is null), "metadata-only default");
        Assert(reopenedHistory.Records().Any(r => r.Id == interruptedId && r.State == "interrupted"), "restart interruption");
        var boundedId = history.Begin("test", "Test", root, "sync", null, null, "bounded");
        history.Finish(boundedId, "exited", 0, string.Concat(Enumerable.Repeat("🙂", 20_000)));
        var bounded = history.Records().Single(r => r.Id == boundedId);
        Assert(bounded.Truncated && Encoding.UTF8.GetByteCount(bounded.Output!) <= CommandHistoryStore.ContentLimit,
            "bounded Unicode history");
        var isolation = new LocalTools(root, "", "", true, enableGitPush: true,
            enableCodexHistory: true, isolatedShellImage: "invalid-fixture-image");
        Assert(isolation.ShellExecution == "isolated_linux", "isolated mode reported");
        Assert(!isolation.HasTool("git_push") && !isolation.HasTool("save_conversation_to_codex"), "isolated mode blocks host privilege tools");
        await AssertThrowsAsync(() => isolation.CallAsync("run_command", Obj(("command", "Set-Content isolation-escape bad"))),
            "image", "invalid image does not fall back");
        await AssertThrowsAsync(() => isolation.CallAsync("start_command", Obj(("request_id", "isolated-failure"),
            ("command", "Set-Content isolation-escape bad"))), "image", "invalid long command does not fall back");
        Assert(!File.Exists(Path.Combine(root, "isolation-escape")), "no host command executed");
        if (Environment.GetEnvironmentVariable("MCP_TEST_ISOLATED_IMAGE") is string image)
        {
            var sandbox = new LocalTools(root, "", "", true, isolatedShellImage: image);
            var result = await sandbox.CallAsync("run_command", Obj(("command",
                "test ! -w /etc && printf isolated-ok && touch /workspace/isolation-write")));
            Assert(result.StructuredContent.ToJsonString().Contains("isolated-ok") &&
                File.Exists(Path.Combine(root, "isolation-write")), "actual isolated Linux execution");
        }
        Console.WriteLine("local command history: ok");
        Console.WriteLine("agent tools: ok");
    }
}
