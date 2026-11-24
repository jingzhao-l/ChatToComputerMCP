using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileMCP.Core;

public sealed record ActivityAuditEvent(Guid Id, DateTimeOffset Time, string Tool, string? Workspace,
    string? Client, string Result, int DurationMs, int ResponseBytes);

/// <summary>Local audit metadata only, no arguments, results, or credentials.</summary>
public sealed class ActivityAuditStore
{
    public const int SegmentLimit = 1_000_000;
    private readonly object _gate = new();
    private readonly string _directory;

    public static string DirectoryFor(string profile)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile))).ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "activity-audit-v1", hash);
    }

    public ActivityAuditStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        CommandReceiptLedger.EnsurePrivateFolder(Path.GetDirectoryName(_directory)!);
        CommandReceiptLedger.EnsurePrivateFolder(_directory);
        CommandReceiptLedger.RefuseLinks(Path.Combine(_directory, "current.jsonl"));
        CommandReceiptLedger.RefuseLinks(Path.Combine(_directory, "previous.jsonl"));
    }

    public void Record(string tool, string? workspace, string? client, string result, int durationMs, int responseBytes)
    {
        var item = new ActivityAuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, tool, workspace,
            client, result, Math.Max(0, durationMs), Math.Max(0, responseBytes));
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item) + "\n");
        if (data.Length > 2048) throw new FileMcpException("Audit record exceeds size limit");
        lock (_gate)
        {
            var active = Path.Combine(_directory, "current.jsonl");
            var previous = Path.Combine(_directory, "previous.jsonl");
            CommandReceiptLedger.RefuseLinks(active);
            CommandReceiptLedger.RefuseLinks(previous);
            if (File.Exists(active) && new FileInfo(active).Length > SegmentLimit - data.Length)
                File.Move(active, previous, true);
            using var stream = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            stream.Write(data); stream.Flush(true);
        }
    }

    public static IReadOnlyList<ActivityAuditEvent> Read(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        CommandReceiptLedger.RefuseLinks(Path.GetDirectoryName(directory)!);
        CommandReceiptLedger.RefuseLinks(directory);
        var records = new List<ActivityAuditEvent>();
        foreach (var name in new[] { "previous.jsonl", "current.jsonl" })
        {
            var path = Path.Combine(directory, name);
            CommandReceiptLedger.RefuseLinks(path);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > SegmentLimit) throw new FileMcpException("Audit segment exceeds size limit");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is string line)
                {
                    try { if (JsonSerializer.Deserialize<ActivityAuditEvent>(line) is { } item) records.Add(item); }
                    catch (JsonException) { /* Incomplete final record after interruption. */ }
                }
            }
            catch (FileNotFoundException) { /* Concurrent rotation or empty store. */ }
        }
        return records.DistinctBy(item => item.Id).OrderByDescending(item => item.Time).ToArray();
    }
}
