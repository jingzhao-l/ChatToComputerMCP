using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task TestWindowsReceiptLedgerAsync(string root)
    {
        var folder = Path.Combine(root, "command-receipt-tests");
        var journal = Path.Combine(folder, "private", "receipts.json");
        var first = new CommandReceiptLedger(journal);
        Assert(first.Summary().Count == 0, "receipt journal initially empty");
        first.Reserve("default", "confidential-request-id");
        Assert(first.Summary()["starting"] == 1, "start receipt persisted before command");
        var payload = File.ReadAllText(journal);
        Assert(!payload.Contains("confidential-request-id", StringComparison.Ordinal) &&
               !payload.Contains("default", StringComparison.Ordinal),
            "journal contains neither plaintext request nor workspace ID");
        var account = WindowsIdentity.GetCurrent().User;
        var currentAccess = new DirectoryInfo(Path.GetDirectoryName(journal)!)
            .GetAccessControl();
        Assert(account is not null && currentAccess.AreAccessRulesProtected,
            "receipt folder has protected explicit ACL");
        if (account is null) throw new FileMcpException("Windows SID is missing");
        Assert(currentAccess.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => account.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl),
            "receipt directory grants owner full access");

        // A restarted app cannot know whether a task finished if its terminal
        // callback never persisted, so it never automatically replays it.
        var reopened = new CommandReceiptLedger(journal);
        Assert(reopened.Summary()["interrupted"] == 1,
            "incomplete receipts become interrupted after restart");
        try
        {
            reopened.Reserve("default", "confidential-request-id");
            throw new Exception("Stale command request accepted after restart");
        }
        catch (FileMcpException ex)
        {
            Assert(ex.Message.Contains("already recorded", StringComparison.Ordinal),
                "durable replay refused");
        }
        reopened.Reserve("other-scope", "confidential-request-id");
        reopened.Finish("other-scope", "confidential-request-id", "exited", 0);
        Assert(reopened.Summary()["exited"] == 1, "independent scopes store terminal receipts");
        reopened.Reserve("client-workspace", "secret-owned-id", "oauth-client-one");
        var own = reopened.OwnReceipt("client-workspace", "secret-owned-id", "oauth-client-one");
        Assert(own["state"]!.GetValue<string>() == "starting" &&
               own["reattachable"]!.GetValue<bool>() == false, "authorized owner reads own status");
        foreach (var denied in new[] { "oauth-client-two", "" })
        {
            await AssertThrowsAsync(() => Task.FromResult(reopened.OwnReceipt(
                "client-workspace", "secret-owned-id", denied)), "inaccessible", "foreign owner denied");
        }
        await AssertThrowsAsync(() => Task.FromResult(reopened.OwnReceipt(
            "default", "confidential-request-id", "oauth-client-one")), "inaccessible",
            "legacy unowned receipt is not claimable");
        var hashed = File.ReadAllText(journal);
        Assert(!hashed.Contains("oauth-client-one", StringComparison.Ordinal) &&
               !hashed.Contains("secret-owned-id", StringComparison.Ordinal),
            "only salted owner/request fingerprints saved");
        var again = new CommandReceiptLedger(journal);
        Assert(again.Summary()["interrupted"] == 2 && again.Summary()["exited"] == 1,
            "terminal receipts survive app restart");
        var durable = again.OwnReceipt("client-workspace", "secret-owned-id", "oauth-client-one");
        Assert(durable["state"]!.GetValue<string>() == "interrupted" &&
               durable["reattachable"]!.GetValue<bool>() == false,
            "client identity persists after restart, but task does not reattach");

        // Fail closed if the disk is corrupted. Never silently generate a new
        // salt/history that might allow replaying an old non-idempotent job.
        File.WriteAllText(journal, "{bad");
        try
        {
            _ = new CommandReceiptLedger(journal);
            throw new Exception("Corrupt command journal unexpectedly accepted");
        }
        catch (FileMcpException ex)
        {
            Assert(ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase),
                "corrupt journal fails closed");
        }

        // Verify the actual Windows MCP command mechanism honors the journal,
        // as opposed to testing only the persistence helper in isolation.
        var liveJournalPath = Path.Combine(folder, "runtime", "receipts.json");
        var ledger = new CommandReceiptLedger(liveJournalPath);
        var workspace = Path.Combine(folder, "workspace");
        Directory.CreateDirectory(workspace);
        var tools = new LocalTools(workspace, "Test", "test@example.invalid",
            true, receiptLedger: ledger);
        var args = new JsonObject {
            ["request_id"] = "actual-shell-request",
            ["command"] = "Set-Content -LiteralPath 'effect.txt' -Value 'done' -NoNewline",
            ["cwd"] = "",
            ["timeout_seconds"] = 15
        };
        var response = await tools.CallAsync("start_command", (JsonObject)args.DeepClone());
        var sessionID = response.StructuredContent["session_id"]!.GetValue<string>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await tools.CallAsync("read_command_output",
                new JsonObject { ["session_id"] = sessionID });
            if (snapshot.StructuredContent["state"]?.GetValue<string>() == "exited") break;
            await Task.Delay(50);
        }
        var effect = Path.Combine(workspace, "effect.txt");
        Assert(File.Exists(effect) && File.ReadAllText(effect) == "done",
            "MCP command actually wrote test effect");
        tools.StopCommandSessions();
        var afterRestart = new CommandReceiptLedger(liveJournalPath);
        var freshTools = new LocalTools(workspace, "Test", "test@example.invalid",
            true, receiptLedger: afterRestart);
        try
        {
            _ = await freshTools.CallAsync("start_command", (JsonObject)args.DeepClone());
            throw new Exception("MCP command replay not blocked after restart");
        }
        catch (FileMcpException ex)
        {
            Assert(ex.Message.Contains("already recorded", StringComparison.Ordinal),
                "real MCP start_command replay blocked after restart");
        }
        var durableByTool = await freshTools.CallAsync("command_receipt_status",
            new JsonObject { ["request_id"] = "actual-shell-request" });
        Assert(durableByTool.StructuredContent["state"]!.GetValue<string>() == "exited" &&
               durableByTool.StructuredContent["reattachable"]!.GetValue<bool>() == false,
            "actual MCP request durable receipt can be checked after restart");
        await AssertThrowsAsync(() => freshTools.CallAsync("command_receipt_status",
            new JsonObject { ["request_id"] = "actual-shell-request" },
            verifiedOwnerID: "another-authenticated-client"),
            "inaccessible", "foreign client denied durable MCP receipt");
        freshTools.StopCommandSessions();
        Console.WriteLine("windows-command-receipts: ok");
    }
}
