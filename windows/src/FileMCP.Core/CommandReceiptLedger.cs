using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FileMCP.Core;

/// <summary>
/// Fail-closed on-disk request-ID receipts. Reboot and application crashes
/// never cause the old PowerShell command to be retried automatically.
/// No command text, output, workspace path or credentials are saved.
/// This is NOT a reattachable subprocess supervisor.
/// </summary>
public sealed class CommandReceiptLedger
{
    private sealed class Receipt
    {
        public required string Identity { get; set; }
        public required string State { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int? ExitCode { get; set; }
    }

    private sealed class Store
    {
        public int Schema { get; set; } = 1;
        public required string Salt { get; set; }
        public List<Receipt> Entries { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Serializer = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string _path;
    private Store _store;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatToComputerMCP", "receipts", "command-receipts-v1.json");

    internal CommandReceiptLedger(string? filePath = null)
    {
        _path = Path.GetFullPath(filePath ?? DefaultPath);
        var folder = Path.GetDirectoryName(_path)!;
        EnsurePrivateFolder(folder);
        RefuseLinks(_path);
        if (File.Exists(_path))
        {
            try
            {
                var metadata = new FileInfo(_path);
                if (metadata.Length > 3_000_000) throw new FileMcpException("Receipt store is too large");
                _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path), Serializer)
                    ?? throw new FileMcpException("Receipt store was empty");
                if (_store.Schema != 1 || _store.Salt.Length != 64 ||
                    !_store.Salt.All(Uri.IsHexDigit) ||
                    _store.Entries.Count > 8192 ||
                    _store.Entries.Any(entry =>
                        entry.Identity.Length != 64 || !entry.Identity.All(Uri.IsHexDigit) ||
                        entry.State is not ("starting" or "interrupted" or "exited" or
                            "cancelled" or "timed_out" or "launch_failed")) ||
                    _store.Entries.Select(entry => entry.Identity)
                        .Distinct(StringComparer.Ordinal).Count() != _store.Entries.Count)
                    throw new FileMcpException("Receipt store validation failed");
            }
            catch (Exception ex)
            {
                throw new FileMcpException("Command receipt store is invalid; refusing to replay prior commands: " +
                    ex.GetType().Name);
            }
        }
        else
        {
            _store = new Store { Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
            Persist();
        }

        // An earlier runtime cannot prove that a command completed. The
        // explicit interrupted state requires a separate, intentional new ID.
        var changed = false;
        foreach (var receipt in _store.Entries.Where(entry => entry.State == "starting"))
        {
            receipt.State = "interrupted";
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (changed) Persist();
    }

    internal IReadOnlyDictionary<string, int> Summary()
    {
        lock (_gate)
        {
            return _store.Entries.GroupBy(entry => entry.State)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        }
    }

    internal void Reserve(string scopeID, string requestID)
    {
        lock (_gate)
        {
            var identity = Identity(scopeID, requestID);
            if (_store.Entries.Any(entry => entry.Identity == identity))
                throw new FileMcpException("Request ID already recorded by another runtime; inspect actual effects before choosing a new ID");
            if (_store.Entries.Count >= 8192)
                throw new FileMcpException("Command receipt ledger capacity reached; archive it before accepting commands");
            var now = DateTimeOffset.UtcNow;
            var receipt = new Receipt
            {
                Identity = identity, State = "starting", StartedAt = now, UpdatedAt = now
            };
            _store.Entries.Add(receipt);
            try { Persist(); } catch { _store.Entries.Remove(receipt); throw; }
        }
    }

    internal void Finish(string scopeID, string requestID, string state, int? code)
    {
        if (state is not ("exited" or "cancelled" or "timed_out" or "launch_failed"))
            throw new FileMcpException("Invalid terminal receipt state");
        lock (_gate)
        {
            var identity = Identity(scopeID, requestID);
            var receipt = _store.Entries.FirstOrDefault(entry => entry.Identity == identity)
                ?? throw new FileMcpException("Unknown command receipt");
            var oldState = receipt.State;
            var oldCode = receipt.ExitCode;
            var oldDate = receipt.UpdatedAt;
            receipt.State = state;
            receipt.ExitCode = code;
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            try { Persist(); }
            catch
            {
                receipt.State = oldState;
                receipt.ExitCode = oldCode;
                receipt.UpdatedAt = oldDate;
                throw;
            }
        }
    }

    private string Identity(string scope, string request)
    {
        var payload = _store.Salt + ":" + Encoding.UTF8.GetByteCount(scope) + ":" + scope +
                      ":" + Encoding.UTF8.GetByteCount(request) + ":" + request;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void RefuseLinks(string file)
    {
        // File.Exists returns false for dangling junction/symlink targets,
        // so query attributes rather than dereferencing.
        try
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new FileMcpException("Command receipt path is a reparse point");
        }
        catch (FileNotFoundException) { }
    }

    private static void EnsurePrivateFolder(string folder)
    {
        // Reject ancestor junctions/reparse points as well as the final
        // receipt directory, never redirect a persistent store unexpectedly.
        for (var ancestor = folder; !string.IsNullOrEmpty(ancestor);
             ancestor = Path.GetDirectoryName(ancestor))
        {
            if (Directory.Exists(ancestor) &&
                (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new FileMcpException("Command receipt parent directory is a reparse point");
            if (Path.GetPathRoot(ancestor)?.Equals(ancestor, StringComparison.OrdinalIgnoreCase) == true)
                break;
        }
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return;
        }
        Directory.CreateDirectory(folder);
        var identity = WindowsIdentity.GetCurrent().User ??
            throw new FileMcpException("Windows user identity unavailable");
        var rules = new DirectorySecurity();
        rules.SetAccessRuleProtection(true, false);
        rules.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(rules);
    }

    private void Persist()
    {
        RefuseLinks(_path);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_store, Serializer));
            File.Move(temp, _path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
