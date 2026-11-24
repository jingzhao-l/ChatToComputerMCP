using System.Diagnostics;
using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private const string SupervisedFixtureCommand = "[Console]::Write('started'); [Console]::Out.Flush(); while (-not (Test-Path -LiteralPath release)) { Start-Sleep -Milliseconds 100 }; [Console]::Write('finished')";
    private static void SupervisorFixture(string root)
    {
        var workspace = Path.Combine(root, "workspace");
        var ledger = new CommandReceiptLedger(Path.Combine(root, "receipts", "ledger.json"));
        var history = new CommandHistoryStore(Path.Combine(root, "history", "profile"), false);
        var supervisor = new CommandSupervisor(Path.Combine(root, "workers", "profile"));
        var tools = new LocalTools(workspace, "", "", true, receiptLedger: ledger, commandHistory: history, commandSupervisor: supervisor);
        var result = tools.CallAsync("start_command", Obj(("request_id", "crash-recovery"),
            ("command", SupervisedFixtureCommand), ("timeout_seconds", 60))).GetAwaiter().GetResult().StructuredContent;
        var ready=Path.Combine(root,"session-id");
        // Publish readiness only after the writer has closed its handle.
        var temporary=ready+".tmp";
        File.WriteAllText(temporary,result["session_id"]!.GetValue<string>());
        File.Move(temporary,ready);
        Thread.Sleep(Timeout.Infinite);
    }

    private static async Task TestSupervisedRecoveryAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "supervisor");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var executable = Environment.ProcessPath ?? throw new Exception("Missing test executable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--supervisor-fixture"); start.ArgumentList.Add(root);
        using var fixture = Process.Start(start) ?? throw new Exception("Could not start test fixture");
        var supervisor = new CommandSupervisor(Path.Combine(root, "workers", "profile"));
        LocalMcpServer? server = null;
        try
        {
            var idPath = Path.Combine(root, "session-id");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(idPath) && !fixture.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert(File.Exists(idPath), "Supervisor launched by independent fixture");
            var id = File.ReadAllText(idPath);
            var readyJob=supervisor.Jobs().Single(job=>job.Record.SessionId==id);
            deadline=DateTime.UtcNow.AddSeconds(15);
            SupervisedCommandStatus before;
            do {
                before=readyJob.ReadStatus();
                if(before.Terminal||string.Concat(before.Chunks.Select(c=>c.Text))=="started")break;
                await Task.Delay(50);
            }while(DateTime.UtcNow<deadline);
            Assert(!before.Terminal&&string.Concat(before.Chunks.Select(c=>c.Text))=="started",
                "Worker reached the running barrier before gateway kill: "+System.Text.Json.JsonSerializer.Serialize(before));
            fixture.Kill(entireProcessTree: false); await fixture.WaitForExitAsync();
            var jobs = supervisor.Jobs();
            Assert(jobs.Count == 1 && jobs[0].Record.SessionId == id && jobs[0].WorkerAlive(), "Original worker survives gateway kill");
            var ledger = new CommandReceiptLedger(Path.Combine(root, "receipts", "ledger.json"));
            var history = new CommandHistoryStore(Path.Combine(root, "history", "profile"), false);
            server = new LocalMcpServer(18969, workspace, "", "", true, new string('a', 64), _ => {},
                receiptLedger: ledger, commandHistory: history, commandSupervisor: supervisor);
            // The server has already restored the job into its own dispatcher.
            // Test the same underlying task through desktop APIs and the persisted journal.
            var recovered = server.DesktopCommandSnapshots();
            Assert(recovered.Any(row => row.SessionId == id && row.Active), "Original session recovered by the new gateway");
            File.WriteAllText(Path.Combine(workspace, "release"), "");
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && server.DesktopCommandSnapshots().Any(row => row.SessionId == id && row.Active)) await Task.Delay(50);
            var output = await server.DesktopCommandOutputAsync("default", id);
            Assert(output["state"]!.GetValue<string>() == "exited" && output["output"]!.GetValue<string>() == "startedfinished", "Recovered worker retains output and exits: "+output.ToJsonString()+"; durable="+System.Text.Json.JsonSerializer.Serialize(readyJob.ReadStatus()));
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && ledger.OwnReceipt("default", "crash-recovery", "__local_admin__")["state"]!.GetValue<string>() != "exited") await Task.Delay(50);
            Assert(ledger.OwnReceipt("default", "crash-recovery", "__local_admin__")["state"]!.GetValue<string>() == "exited", "Terminal receipt reconciled");
            Console.WriteLine("PASS: Windows gateway death, independent worker survival, recovered output and durable receipt");
        }
        finally
        {
            if (!fixture.HasExited) { fixture.Kill(entireProcessTree: false); await fixture.WaitForExitAsync(); }
            server?.Stop();
            foreach (var job in supervisor.Jobs()) if (!job.ReadStatus().Terminal) job.RequestCancel();
        }
    }
}
