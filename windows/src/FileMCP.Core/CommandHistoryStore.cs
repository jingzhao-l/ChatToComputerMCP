using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileMCP.Core;

/// <summary>Private local history; never returned by MCP. Replay receipts are separate.</summary>
public sealed record CommandHistoryRecord(
    Guid Id, string WorkspaceId, string WorkspaceName, string Cwd, string Kind,
    string? RequestId, string? ClientId, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    string State, int? ExitCode, string? Command, string? Output, bool Truncated)
{
    public bool IsTerminal => State != "running";
}

public sealed class CommandHistoryStore
{
    public const int MaxCompleted = 200;
    public const int RetentionDays = 30;
    public const int ContentLimit = 65_536;
    private static readonly HashSet<string> States =
        ["running", "exited", "cancelled", "timed_out", "launch_failed", "interrupted"];
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Dictionary<Guid, CommandHistoryRecord> _records;
    public bool CaptureContent { get; }

    public static string DirectoryFor(string profile)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile))).ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "command-history-v1", hash);
    }

    public CommandHistoryStore(string directory, bool captureContent)
    {
        _directory = Path.GetFullPath(directory);
        CaptureContent = captureContent;
        CommandReceiptLedger.EnsurePrivateFolder(Path.GetDirectoryName(_directory)!);
        CommandReceiptLedger.EnsurePrivateFolder(_directory);
        _records = Read(_directory).ToDictionary(item => item.Id);
        // The owning runtime holds its exclusive profile lock before opening.
        foreach (var record in _records.Values.Where(record => !record.IsTerminal).ToArray())
        {
            var interrupted = record with { State = "interrupted", UpdatedAt = DateTimeOffset.UtcNow };
            Persist(interrupted);
            _records[interrupted.Id] = interrupted;
        }
        Prune();
    }

    public Guid Begin(string workspaceId, string workspaceName, string cwd, string kind,
        string? requestId, string? clientId, string command, Guid? id = null)
    {
        lock (_gate)
        {
            var key = id ?? Guid.NewGuid();
            if (_records.ContainsKey(key)) throw new FileMcpException("Duplicate local history ID");
            var now = DateTimeOffset.UtcNow;
            var content = CaptureContent ? Bounded(command) : ((string Text, bool Truncated)?)null;
            var record = new CommandHistoryRecord(key, workspaceId, workspaceName, cwd, kind,
                requestId, clientId == "__local_admin__" ? null : clientId, now, now, "running",
                null, content?.Text, null, content?.Truncated ?? false);
            Persist(record);
            _records.Add(key, record);
            return key;
        }
    }

    public void Finish(Guid id, string state, int? exitCode, string output = "", bool truncated = false)
    {
        lock (_gate)
        {
            if (!States.Contains(state) || state == "running" ||
                !_records.TryGetValue(id, out var record) || record.IsTerminal)
                throw new FileMcpException("Invalid or already terminal local history entry");
            var content = CaptureContent ? Bounded(output) : ((string Text, bool Truncated)?)null;
            var updated = record with {
                State = state, ExitCode = exitCode, UpdatedAt = DateTimeOffset.UtcNow,
                Output = content?.Text, Truncated = record.Truncated || (CaptureContent && (truncated || content!.Value.Truncated))
            };
            Persist(updated);
            _records[id] = updated;
            Prune();
        }
    }

    private static (string Text, bool Truncated) Bounded(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= ContentLimit) return (text, false);
        var buffer = new byte[ContentLimit];
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), buffer.AsSpan(), true,
            out _, out var used, out _);
        return (Encoding.UTF8.GetString(buffer, 0, used), true);
    }

    public IReadOnlyList<CommandHistoryRecord> Records()
    {
        lock (_gate) return _records.Values.OrderByDescending(item => item.StartedAt).ToArray();
    }

    public static IReadOnlyList<CommandHistoryRecord> Read(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        CommandReceiptLedger.RefuseLinks(Path.GetDirectoryName(directory)!);
        CommandReceiptLedger.RefuseLinks(directory);
        var records = new List<CommandHistoryRecord>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id)) continue;
            CommandReceiptLedger.RefuseLinks(file);
            try
            {
                if (new FileInfo(file).Length > 1_000_000)
                    throw new FileMcpException("History record exceeds size limit");
                var record = JsonSerializer.Deserialize<CommandHistoryRecord>(File.ReadAllText(file))
                    ?? throw new FileMcpException("Invalid history record");
                if (record.Id != id || !States.Contains(record.State) ||
                    Encoding.UTF8.GetByteCount(record.Command ?? "") > ContentLimit ||
                    Encoding.UTF8.GetByteCount(record.Output ?? "") > ContentLimit)
                    throw new FileMcpException("Invalid history record");
                records.Add(record);
            }
            catch (FileNotFoundException) { /* Concurrent retention prune. */ }
        }
        return records.OrderByDescending(item => item.StartedAt).ToArray();
    }

    private string RecordPath(Guid id) => Path.Combine(_directory, id.ToString("D") + ".json");

    private void Persist(CommandHistoryRecord record)
    {
        var path = RecordPath(record.Id);
        CommandReceiptLedger.RefuseLinks(path);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(record));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var finished = _records.Values.Where(item => item.IsTerminal)
            .OrderByDescending(item => item.UpdatedAt).ToArray();
        foreach (var (record, index) in finished.Select((item, index) => (item, index)))
        {
            if (index < MaxCompleted && record.UpdatedAt >= cutoff) continue;
            var path = RecordPath(record.Id);
            CommandReceiptLedger.RefuseLinks(path);
            File.Delete(path);
            _records.Remove(record.Id);
        }
    }
}
