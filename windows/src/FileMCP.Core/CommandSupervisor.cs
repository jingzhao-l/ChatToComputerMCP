using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileMCP.Core;

internal sealed record SupervisedCommandRecord(string SessionId, string RequestId, string ScopeId,
    string OwnerId, string Root, string Cwd, string Shell, string? Image, int Timeout, bool QueueIfBusy,
    string CommandDigest, Guid HistoryId, DateTimeOffset CreatedAt)
{
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record SupervisedOutputChunk(int Cursor, string Text);
internal sealed record SupervisedCommandStatus(string State, int? ExitCode,
    SupervisedOutputChunk[] Chunks, int LastCursor, DateTimeOffset UpdatedAt)
{
    internal bool Terminal => State is not ("starting" or "running" or "stopping");
    internal static SupervisedCommandStatus Initial() => new("starting", null, [], 0, DateTimeOffset.UtcNow);
}

internal sealed class SupervisedCommandJob(string directory, SupervisedCommandRecord record)
{
    internal string DirectoryPath => directory;
    internal SupervisedCommandRecord Record => record;
    private string StatusPath => Path.Combine(directory, "status.json");
    private string LockPath => Path.Combine(directory, "worker.lock");
    private string CancelPath => Path.Combine(directory, "cancel");

    internal static void Write<T>(string path, T value)
    {
        CommandReceiptLedger.RefuseLinks(path);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static T Read<T>(string path, long max)
    {
        CommandReceiptLedger.RefuseLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > max) throw new FileMcpException("Worker record exceeds size limit");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new FileMcpException("Worker record is invalid");
    }

    internal void Publish(SupervisedCommandStatus status) => Write(StatusPath, status);
    internal bool WorkerAlive()
    {
        CommandReceiptLedger.RefuseLinks(LockPath);
        try
        {
            using var probe = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return true; }
    }

    internal SupervisedCommandStatus ReadStatus()
    {
        var status = Read<SupervisedCommandStatus>(StatusPath, 1_600_000);
        if (status.State is not ("starting" or "running" or "stopping" or "exited" or "cancelled" or "timed_out" or "launch_failed" or "interrupted") ||
            status.LastCursor < 0 || status.Chunks.Length > 256 ||
            status.Chunks.Sum(chunk => Encoding.UTF8.GetByteCount(chunk.Text)) > 262_144 ||
            status.Chunks.Any(chunk => chunk.Cursor <= 0 || chunk.Cursor > status.LastCursor) ||
            status.Chunks.Zip(status.Chunks.Skip(1)).Any(pair => pair.First.Cursor + 1 != pair.Second.Cursor) ||
            (status.Chunks.LastOrDefault()?.Cursor ?? 0) != status.LastCursor)
            throw new FileMcpException("Invalid worker status");
        if (!status.Terminal && DateTimeOffset.UtcNow - status.UpdatedAt > TimeSpan.FromSeconds(8) && !WorkerAlive())
            return status with { State = "interrupted", ExitCode = null };
        return status;
    }

    internal void RequestCancel() => Write(CancelPath, true);
    internal void Launch(string command, string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
        start.ArgumentList.Add("--command-worker"); start.ArgumentList.Add(directory);
        start.StandardInputEncoding = new UTF8Encoding(false);
        using var process = Process.Start(start) ?? throw new FileMcpException("Worker could not start");
        try
        {
            process.StandardInput.Write(command); process.StandardInput.Close();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var status = ReadStatus();
                if (status.State != "starting") return;
                if (process.HasExited) break;
                Thread.Sleep(25);
            }
            throw new FileMcpException("Worker did not acknowledge launch");
        }
        catch { try { RequestCancel(); } catch { } throw; }
    }

    internal static void RunWorker(string directory)
    {
        CommandReceiptLedger.RefuseLinks(Path.GetDirectoryName(directory)!);
        CommandReceiptLedger.EnsurePrivateFolder(directory);
        var record = Read<SupervisedCommandRecord>(Path.Combine(directory, "record.json"), 65_536);
        if (Path.GetFileName(directory) != record.SessionId || !Guid.TryParse(record.SessionId, out _) || record.Timeout is < 1 or > 3600)
            throw new FileMcpException("Invalid worker record");
        var job = new SupervisedCommandJob(directory, record);
        CommandReceiptLedger.RefuseLinks(job.LockPath);
        using var exclusive = new FileStream(job.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Read raw state while holding the exclusive worker lock.
        if (Read<SupervisedCommandStatus>(job.StatusPath, 1_600_000).State != "starting")
            throw new FileMcpException("Worker cannot run a prior job twice");
        Console.InputEncoding = new UTF8Encoding(false);
        var command = Console.In.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(command) > 8_000_000 || SupervisedCommandRecord.Digest(command) != record.CommandDigest)
        {
            job.Publish(SupervisedCommandStatus.Initial() with { State = "launch_failed" }); return;
        }
        var gate = new object();
        var state = "starting";
        string? reason = null;
        int? code = null;
        var chunks = new Queue<SupervisedOutputChunk>();
        var sequence = 0;
        var bytes = 0;
        void Append(string text)
        {
            lock (gate)
            {
                chunks.Enqueue(new(++sequence, text)); bytes += Encoding.UTF8.GetByteCount(text);
                while (bytes > 262_144 || chunks.Count > 256) bytes -= Encoding.UTF8.GetByteCount(chunks.Dequeue().Text);
            }
        }
        void Finish(int exit) { lock (gate) { code = exit; state = reason ?? "exited"; } }
        SupervisedCommandStatus Snapshot() { lock (gate) return new(state, code, chunks.ToArray(), sequence, DateTimeOffset.UtcNow); }
        if (File.Exists(job.CancelPath)) { job.Publish(Snapshot() with { State = "cancelled" }); return; }
        ShellExecutionPlan? plan = null;
        try
        {
            var resolver = new SafePathResolver(record.Root);
            if (!string.Equals(resolver.Root, record.Root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(resolver.Resolve(Path.GetRelativePath(record.Root, record.Cwd)), record.Cwd, StringComparison.OrdinalIgnoreCase))
                throw new FileMcpException("Worker directory authorization changed");
            plan = ShellExecutionPlan.Build(record.Root, record.Cwd, command, record.Shell, record.Timeout, record.Image);
            using var child = ProcessRunner.StartManaged(plan.Executable, plan.Arguments, plan.Cwd, plan.Environment, Append, Finish);
            lock (gate) if (state == "starting") state = "running";
            var deadline = DateTime.UtcNow.AddSeconds(record.Timeout);
            SupervisedCommandStatus? previous = null;
            while (true)
            {
                var status = Snapshot();
                if (status.Terminal)
                {
                    child.StopSynchronously(); plan.Cleanup?.Invoke(); plan = null;
                    job.Publish(status); break;
                }
                if (previous is null || previous.State != status.State || previous.LastCursor != status.LastCursor)
                { job.Publish(status); previous = status; }
                if (reason is null && (File.Exists(job.CancelPath) || DateTime.UtcNow >= deadline))
                {
                    lock (gate)
                    {
                        if (state == "running") { reason = File.Exists(job.CancelPath) ? "cancelled" : "timed_out"; state = "stopping"; }
                    }
                    if (reason is not null) child.Stop();
                }
                Thread.Sleep(80);
            }
        }
        catch { job.Publish(Snapshot() with { State = "launch_failed", ExitCode = null }); throw; }
        finally { plan?.Cleanup?.Invoke(); }
    }
}

public sealed class CommandSupervisor
{
    private readonly string _directory;
    private readonly string _executable;
    internal bool AllowsNew { get; }
    public CommandSupervisor(string directory, bool allowsNew = true, string? executable = null)
    {
        _directory = Path.GetFullPath(directory);
        _executable = executable ?? Environment.ProcessPath ?? throw new FileMcpException("Worker executable unavailable");
        AllowsNew = allowsNew;
        CommandReceiptLedger.EnsurePrivateFolder(Path.GetDirectoryName(_directory)!);
        CommandReceiptLedger.EnsurePrivateFolder(_directory);
    }
    internal static string DirectoryFor(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatToComputerMCP", "supervised-jobs-v1", SupervisedCommandRecord.Digest(profile));
    public static void RunWorker(string directory) => SupervisedCommandJob.RunWorker(directory);
    internal SupervisedCommandJob Create(SupervisedCommandRecord record, string command)
    {
        if (!AllowsNew || !Guid.TryParse(record.SessionId, out _)) throw new FileMcpException("Invalid supervised job");
        var location = Path.Combine(_directory, record.SessionId);
        if (Directory.Exists(location)) throw new FileMcpException("Duplicate supervised job");
        CommandReceiptLedger.EnsurePrivateFolder(location);
        var job = new SupervisedCommandJob(location, record);
        SupervisedCommandJob.Write(Path.Combine(location, "record.json"), record);
        job.Publish(SupervisedCommandStatus.Initial());
        job.Launch(command, _executable);
        return job;
    }
    internal IReadOnlyList<SupervisedCommandJob> Jobs()
    {
        var jobs = new List<SupervisedCommandJob>();
        foreach (var location in Directory.EnumerateDirectories(_directory))
        {
            if (!Guid.TryParse(Path.GetFileName(location), out _)) continue;
            CommandReceiptLedger.RefuseLinks(location);
            var record = SupervisedCommandJob.Read<SupervisedCommandRecord>(Path.Combine(location, "record.json"), 65_536);
            if (record.SessionId != Path.GetFileName(location)) throw new FileMcpException("Invalid worker directory");
            jobs.Add(new(location, record));
        }
        var old = jobs.Where(job => job.ReadStatus().Terminal).OrderByDescending(job => job.Record.CreatedAt).Skip(200).ToArray();
        foreach (var job in old.Where(job => !job.WorkerAlive())) { Directory.Delete(job.DirectoryPath, true); jobs.Remove(job); }
        return jobs.OrderBy(job => job.Record.CreatedAt).ToArray();
    }
}
