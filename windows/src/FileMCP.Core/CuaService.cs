using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileMCP.Core;

/// Independent local opt-in, never inferred from saved guest profiles.
internal static class CuaGuestApproval
{
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatToComputerMCP", "cua-guest-approval");
    internal static string? Epoch(string? path = null)
    {
        try
        {
            var file = new FileInfo(path ?? DefaultPath);
            if (!file.Exists || file.Length != 65 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User;
            var acl = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (owner is null || !owner.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) return null;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !owner.Equals(rule.IdentityReference) &&
                    !system.Equals(rule.IdentityReference) && !admins.Equals(rule.IdentityReference)) return null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[65]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 || bytes[64] != 10 ||
                bytes.Take(64).Any(b => !(b >= 48 && b <= 57 || b >= 97 && b <= 102))) return null;
            return Encoding.ASCII.GetString(bytes,0,64);
        }
        catch { return null; }
    }
}

/// Host-level capability. A workspace grant is never a desktop grant.
/// No remote tool can create or widen the local policy.
internal sealed class CuaService : IDisposable
{
    internal const string SharedOwner = "shared-connection";
    private sealed record Grant(string application, string[] clients, string[]? launchClients = null);
    internal sealed record ExtensionRuntime(string node,string nodeSHA256,string worker,string workerSHA256);
    internal sealed record ExtensionProfile(string id,string kind,string[] clients,string[]? origins=null,int? port=null,string? tokenFile=null,bool? allowFiles=null);
    private sealed record Policy(bool enabled, string driver, string driverSHA256, Grant[] grants, ExtensionRuntime? extensionRuntime=null, ExtensionProfile[]? profiles=null,bool? requireVerifiedClient=null)
    {
        public bool PermitsLaunch(string app, string owner) => grants.Any(g => string.Equals(g.application,app,StringComparison.OrdinalIgnoreCase) && g.clients.Contains(owner,StringComparer.Ordinal) && (g.launchClients ?? []).Contains(owner,StringComparer.Ordinal));
        public bool Permits(string app, string owner) =>
            grants.Any(g => string.Equals(g.application, app, StringComparison.OrdinalIgnoreCase) &&
                            g.clients.Contains(owner, StringComparer.Ordinal));
    }
    private sealed record Target(int Pid, long Window, string Application, long Launched);
    private sealed class Session(string owner, Target target, byte[] policyBytes, Worker worker)
    {
        internal readonly string Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        internal readonly string Owner = owner;
        internal readonly Target Target = target;
        internal readonly byte[] PolicyBytes = policyBytes;
        internal readonly Worker Worker = worker;
        internal long Last = Environment.TickCount64;
        internal bool Busy;
        internal Snapshot? Snapshot;
    }
    internal sealed class Snapshot
    {
        internal readonly string Id;
        internal readonly long? Revision;
        internal readonly string? CaptureId;
        internal readonly uint Width, Height;
        internal readonly long ObservedAt = Environment.TickCount64;
        internal readonly HashSet<string> Tokens = new(StringComparer.Ordinal);
        internal Snapshot(JsonObject structured, JsonArray blocks)
        {
            Id = structured["snapshot_id"]?.GetValue<string>() ?? "";
            if (!Regex.IsMatch(Id, "^s[0-9a-f]{8}$")) throw Fail("cua_missing_snapshot_id");
            if (structured["ui_tree_revision"] is JsonValue revision && revision.TryGetValue<long>(out var r)) Revision = r;
            if (structured["elements"] is JsonArray elements)
                foreach (var element in elements)
                    if (element is JsonObject obj && obj["element_token"] is JsonValue value &&
                        value.TryGetValue<string>(out var token) && token.StartsWith(Id+":",StringComparison.Ordinal) &&
                        Regex.IsMatch(token,"^s[0-9a-f]{8}:[0-9]+$")) Tokens.Add(token);
            var image = blocks.OfType<JsonObject>().FirstOrDefault(b =>
                b["type"]?.GetValue<string>() == "image" && b["mimeType"]?.GetValue<string>() == "image/png");
            if (image?["data"] is JsonValue d && d.TryGetValue<string>(out var data) &&
                structured["capture_id"] is JsonValue capture && capture.TryGetValue<string>(out var captureId) && !string.IsNullOrEmpty(captureId))
            {
                var bytes = Convert.FromBase64String(data);
                if (bytes.Length >= 24 && bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
                {
                    Width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4));
                    Height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
                    CaptureId = captureId;
                }
            }
        }
        internal void Validate(JsonObject args)
        {
            if (Environment.TickCount64 - ObservedAt > 30000) throw Fail("cua_snapshot_expired");
            if (args["element_token"] is JsonValue t && !Tokens.Contains(t.GetValue<string>()))
                throw Fail("cua_stale_or_foreign_element_token");
            if (args.ContainsKey("capture_id") && args["capture_id"]?.GetValue<string>() != CaptureId)
                throw Fail("cua_stale_capture");
            foreach (var (xk,yk) in new[] { ("x","y"),("from_x","from_y"),("to_x","to_y") })
            {
                if (!args.ContainsKey(xk) && !args.ContainsKey(yk)) continue;
                if (CaptureId is null || args["capture_id"]?.GetValue<string>() != CaptureId || Width == 0 || Height == 0 ||
                    args[xk] is not JsonValue x || !x.TryGetValue<double>(out var px) ||
                    args[yk] is not JsonValue y || !y.TryGetValue<double>(out var py) || px < 0 || px >= Width || py < 0 || py >= Height)
                    throw Fail("cua_stale_capture_or_point_outside_window");
            }
        }
    }
    private sealed class ExtensionSession(string owner, ExtensionProfile profile, byte[] policy, Worker worker, string? guestEpoch)
    {
        internal readonly string Id=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        internal readonly string Owner=owner;
        internal readonly ExtensionProfile Profile=profile;
        internal readonly byte[] PolicyBytes=policy;
        internal readonly Worker Worker=worker;
        internal long Last=Environment.TickCount64;
        internal bool Busy=true;
        internal readonly string? GuestEpoch=guestEpoch;
    }
    private readonly Dictionary<string,ExtensionSession> _extensions=new(StringComparer.Ordinal);
    private readonly bool _guestMode;
    private readonly bool _relayOnly;
    private readonly string _guestApprovalPath;
    private readonly string? _startupGuestEpoch;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly string _policyPath;
    private readonly Timer _timer;
    private bool _stopped;
    private FileStream? _runtimeLock;
    private readonly HashSet<string> _launching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,(string Owner,Worker Worker)> _launchWorkers = new(StringComparer.OrdinalIgnoreCase);
    private CommandReceiptLedger? _receiptLedger;
    private static readonly Dictionary<string, string[]> Actions = new(StringComparer.Ordinal)
    {
        ["click"] = ["element_token","x","y","capture_id","button","count","action"],
        ["type_text"] = ["element_token","text","x","y","capture_id"],
        ["press_key"] = ["key","modifiers","element_token"],
        ["scroll"] = ["element_token","direction","amount","x","y","capture_id"],
        ["drag"] = ["from_x","from_y","to_x","to_y","duration_ms","capture_id"],
        ["set_value"] = ["element_token","value"],
        ["invoke_menu"] = ["path"],
        ["set_window_frame"] = ["x","y","width","height"]
    };

    internal CuaService(string? policyPath = null,bool guestMode=false,bool relayOnly=false,string? guestApprovalPath=null)
    {
        _guestMode=guestMode;_relayOnly=relayOnly;
        _guestApprovalPath=guestApprovalPath??CuaGuestApproval.DefaultPath;
        _startupGuestEpoch=(guestMode||relayOnly)?CuaGuestApproval.Epoch(_guestApprovalPath):null;
        _policyPath = policyPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "cua-policy.json");
        _timer = new Timer(_ => { try { Sweep(); } catch { /* fail closed at next admission */ } }, null, 1000, 1000);
    }

    internal static bool ListenerAllows(string name,bool guest,bool relay)
    {
        if(guest)return name.StartsWith("cua_",StringComparison.Ordinal)&&!name.StartsWith("cua_extension_",StringComparison.Ordinal);
        if(relay)return name is "cua_capabilities" or "cua_extension_list" or "cua_extension_open" or "cua_extension_call" or "cua_extension_close" or "cua_receipt_status";
        return true;
    }
    internal static JsonArray Definitions()
    {
        static JsonObject String() => new() { ["type"] = "string" };
        static JsonObject Integer() => new() { ["type"] = "integer", ["minimum"] = 1 };
        static JsonObject Bool() => new() { ["type"] = "boolean" };
        static JsonObject Def(string name, string description, JsonObject properties, string[] required, bool read) => new()
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["additionalProperties"] = false },
            ["outputSchema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = read, ["destructiveHint"] = !read, ["openWorldHint"] = true }
        };
        return new JsonArray(
            Def("cua_extension_list","List locally approved isolated browser profiles and, only after manual local enablement, Guest profiles. Prefer native background control for desktop tasks.",new(),[],true),
            Def("cua_extension_open","Open an isolated browser for web work or an explicitly requested, locally enabled Guest lease. Never use Guest as automatic fallback for native background control.",new(){["profile_id"]=String(),["request_id"]=String()},["profile_id","request_id"],false),
            Def("cua_extension_call","Operate the leased browser or guest using its typed operations. No arbitrary code.",new(){["session_id"]=String(),["operation"]=String(),["parameters"]=new JsonObject{["type"]="object",["additionalProperties"]=true},["request_id"]=String()},["session_id","operation","parameters","request_id"],false),
            Def("cua_extension_close","Release the isolated browser or guest lease.",new(){["session_id"]=String()},["session_id"],false),
            Def("cua_capabilities", "Report local opt-in and background-only CUA capabilities. Prefer native background window control. Guest is optional and disabled unless locally enabled.", new(), [], true),
            Def("cua_launch_app","Launch an application with a separate locally granted launch permission; no arbitrary arguments.",
                new() { ["application"]=String(), ["request_id"]=String() }, ["application","request_id"],false),
            Def("cua_receipt_status","Read an action dispatch receipt using its unique request ID. Dispatch is not verification.",
                new() { ["request_id"]=String() }, ["request_id"],true),
            Def("cua_list_apps", "List running applications explicitly granted to this connection.", new(), [], true),
            Def("cua_list_windows", "List windows of an approved running application.", new() { ["pid"] = Integer() }, ["pid"], true),
            Def("cua_create_session", "Acquire an exclusive background window session. Keep its bearer ID private.",
                new() { ["pid"] = Integer(), ["window_id"] = Integer() }, ["pid","window_id"], false),
            Def("cua_list_sessions", "List own session status. Shared connections cannot enumerate session IDs.", new(), [], true),
            Def("cua_observe", "Observe the exact leased window, including its accessibility tree and image.",
                new() { ["session_id"] = String(), ["include_screenshot"] = Bool() }, ["session_id"], false),
            Def("cua_act", "Perform one background-only action on the leased window. Observe again to verify. No foreground fallback.",
                new() { ["session_id"] = String(), ["request_id"] = String(), ["operation"] = new JsonObject { ["type"] = "string",
                    ["enum"] = new JsonArray(Actions.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) },
                    ["parameters"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true } },
                ["session_id","request_id","operation","parameters"], false),
            Def("cua_verify", "Verify bounded element predicates in the leased window; unknown is not success.",
                new() { ["session_id"] = String(), ["expect"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["items"] = new JsonObject { ["type"] = "object" } },
                    ["timeout_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 6000 } }, ["session_id","expect"], true),
            Def("cua_stop_session", "Revoke a leased session; interrupted action outcomes may be unknown.",
                new() { ["session_id"] = String() }, ["session_id"], false)
        );
    }
    private static FileMcpException Fail(string message) => new(message);
    private string? GuestEpoch()
    {
        var epoch=CuaGuestApproval.Epoch(_guestApprovalPath);
        return (_guestMode||_relayOnly)&&epoch!=_startupGuestEpoch?null:epoch;
    }
    private (Policy, byte[]) LoadPolicy()
    {
        if((_guestMode||_relayOnly)&&GuestEpoch() is null)throw Fail("cua_guest_disabled_by_local_user");

        using var process = Process.GetCurrentProcess();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || process.SessionId == 0) throw Fail("cua_requires_interactive_windows_session");
        var file = new FileInfo(_policyPath);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length is <= 0 or > 65536)
            throw Fail("cua_disabled: local private policy required");
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw Fail("cua_disabled: current user unavailable");
        var acl = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!current.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw Fail("cua_disabled: wrong policy owner");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !current.Equals(rule.IdentityReference) &&
                !system.Equals(rule.IdentityReference) && !admins.Equals(rule.IdentityReference))
                throw Fail("cua_disabled: policy ACL is not private");
        var bytes = File.ReadAllBytes(_policyPath);
        var policy = JsonSerializer.Deserialize<Policy>(bytes);
        if (policy is null || !policy.enabled || policy.driver is null || !Path.IsPathFullyQualified(policy.driver) ||
            policy.grants is null || policy.grants.Length > 64 || (policy.profiles?.Length ?? 0)>32 || (policy.profiles??[]).Select(p=>p.id).Distinct(StringComparer.Ordinal).Count()!=(policy.profiles?.Length??0) || policy.grants.Any(g => g is null || g.application is null || g.clients is null) ||
            policy.driverSHA256 is null || policy.driverSHA256.Length != 64 || !policy.driverSHA256.All(Uri.IsHexDigit))
            throw Fail("cua_disabled: invalid or disabled local policy");
        if(_relayOnly&&(policy.grants.Length!=0||policy.requireVerifiedClient==true||policy.profiles is not {Length:1}||
            policy.profiles[0].kind!="guest"||!policy.profiles[0].clients.SequenceEqual(new[]{SharedOwner})))
            throw Fail("cua_relay_requires_one_dedicated_guest_no_host_grants");
        return (policy, bytes);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    private static bool AllowedApplication(string executable) =>
        !new[] {"cua-driver","ChatToComputerMCP","FileMCP","consent","LogonUI","CredentialUIBroker","SystemSettings","winlogon","lsass","LockApp","CredentialUIBroker","Taskmgr"}
            .Contains(Path.GetFileNameWithoutExtension(executable),StringComparer.OrdinalIgnoreCase);
    private static string CheckedDriver(Policy policy)
    {
        var file = new FileInfo(policy.driver);
        if (!file.Exists || file.Length is <= 0 or > 200_000_000 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 || !string.Equals(file.Extension,".exe",StringComparison.OrdinalIgnoreCase))
            throw Fail("cua_driver_unavailable");
        using var stream=file.OpenRead();
        var hash=Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash,policy.driverSHA256,StringComparison.OrdinalIgnoreCase)) throw Fail("cua_driver_integrity_mismatch");
        return file.FullName;
    }
    private void AcquireRuntimeLock()
    {
        lock(_gate)
        {
            if(_stopped)throw Fail("cua_service_stopped");
            if(_runtimeLock is not null)return;
            var path=Path.Combine(Path.GetDirectoryName(_policyPath)!,"cua-runtime.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                    throw Fail("cua_runtime_lock_unsafe");
                _runtimeLock=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            }
            catch(IOException){throw Fail("cua_runtime_already_owned_or_unsafe");}
        }
    }
    private void ReserveReceipt(CommandReceiptLedger receipt,string request,string owner)
    {
        lock(_gate){if(_stopped||_revoked.Contains(owner)||_runtimeLock is null)throw Fail("cua_service_stopped_or_revoked");receipt.Reserve("cua",request,owner);}
    }
    private void FinishReceipt(CommandReceiptLedger receipt,string request,string state)
    {
        lock(_gate){if(_stopped||_runtimeLock is null)throw Fail("cua_action_outcome_unknown");receipt.Finish("cua",request,state,null);}
    }
    private CommandReceiptLedger Ledger()
    {
        lock (_gate) { AcquireRuntimeLock(); return _receiptLedger ??= new CommandReceiptLedger(Path.Combine(Path.GetDirectoryName(_policyPath)!,"cua-receipts-v1.json")); }
    }
    internal static string RequestId(JsonNode? raw)
    {
        if (raw is not JsonValue v || !v.TryGetValue<string>(out var id) || !Regex.IsMatch(id,"^[A-Za-z0-9_-]{32,128}$"))
            throw Fail("cua_request_id_required: use a unique unpredictable 32-128 character ID");
        return id;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { internal int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out WindowRect rect);
    private static (string, long) ApprovedApp(int pid, Policy policy, string owner)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var executable = process.MainModule?.FileName;
            if (process.HasExited || executable is null || pid == Environment.ProcessId ||
                !policy.Permits(executable, owner) ||
                !AllowedApplication(executable))
                throw Fail("cua_application_not_approved");
            return (executable, process.StartTime.ToUniversalTime().Ticks);
        }
        catch { throw Fail("cua_application_not_approved"); }
    }
    private static JsonArray Windows(int pid)
    {
        var list = new JsonArray();
        EnumWindows((hwnd, param) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out uint p);
            if (p != (uint)pid || list.Count >= 256) return true;
            var text = new StringBuilder(1024);
            _ = GetWindowText(hwnd, text, text.Capacity);
            var bounds=new JsonObject();
            if(GetWindowRect(hwnd,out var rect)) bounds=new JsonObject { ["X"]=rect.Left,["Y"]=rect.Top,["Width"]=rect.Right-rect.Left,["Height"]=rect.Bottom-rect.Top };
            list.Add(new JsonObject { ["window_id"] = hwnd.ToInt64(), ["title"] = text.ToString(),["bounds"]=bounds });
            return true;
        }, IntPtr.Zero);
        return list;
    }
    private static Target GetTarget(int pid, long window, Policy policy, string owner)
    {
        var (app, start) = ApprovedApp(pid, policy, owner);
        var hwnd = new IntPtr(window);
        if (!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd, out uint p) == 0 || p != (uint)pid ||
            !Windows(pid).Any(w => w?["window_id"]?.GetValue<long>() == window))
            throw Fail("cua_window_not_owned_or_unavailable");
        return new Target(pid, window, app, start);
    }
    private bool StillAllowed(Session s, bool mutation)
    {
        lock (_gate)
            if (_stopped || !_sessions.TryGetValue(s.Id, out var live) || !ReferenceEquals(live, s) || _revoked.Contains(s.Owner)) return false;
        try
        {
            var (policy, bytes) = LoadPolicy();
            if (!bytes.AsSpan().SequenceEqual(s.PolicyBytes) || GetTarget(s.Target.Pid, s.Target.Window, policy, s.Owner) != s.Target)
                return false;
            if (mutation && !_guestMode)
            {
                _ = GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == 0 || pid == (uint)s.Target.Pid) return false;
            }
            return true;
        }
        catch { return false; }
    }
    private void Sweep()
    {
        Session[] all;ExtensionSession[] extended;
        lock (_gate) {all = _sessions.Values.ToArray();extended=_extensions.Values.ToArray();}
        foreach(var e in extended)
        {
            bool expired;lock(_gate)expired=!e.Busy && Environment.TickCount64-e.Last>300000;
            if(expired||!ExtensionAllowed(e))RetireExtension(e.Id);
        }
        foreach (var s in all)
        {
            bool expired;
            lock (_gate) expired = !s.Busy && Environment.TickCount64 - s.Last > 300000;
            if (expired || !StillAllowed(s, false)) Retire(s.Id);
        }
    }
    private void Retire(string id)
    {
        lock (_gate) { if (_sessions.TryGetValue(id,out var s)) s.Worker.Dispose(); _sessions.Remove(id); }
    }
    internal void Revoke(string owner)
    {
        string[] ids,ext; Worker[] workers;
        lock (_gate) { _revoked.Add(owner); ids = _sessions.Values.Where(s => s.Owner == owner).Select(s => s.Id).ToArray(); ext=_extensions.Values.Where(e=>e.Owner==owner).Select(e=>e.Id).ToArray(); workers=_launchWorkers.Values.Where(w=>w.Owner==owner).Select(w=>w.Worker).ToArray(); }
        foreach(var worker in workers) worker.Dispose();
        foreach(var id in ext)RetireExtension(id);
        foreach (var id in ids) Retire(id);
    }
    internal void Stop()
    {
        Session[] all; Worker[] workers;ExtensionSession[] ext;
        lock (_gate) { if(_stopped)return;_stopped = true; all = _sessions.Values.ToArray(); workers=_launchWorkers.Values.Select(w=>w.Worker).ToArray(); ext=_extensions.Values.ToArray();_extensions.Clear();_sessions.Clear(); }
        foreach(var worker in workers) worker.Dispose();
        foreach(var e in ext)e.Worker.Dispose();
        _timer.Dispose();
        foreach (var s in all) s.Worker.Dispose();
        lock(_gate){_runtimeLock?.Dispose();_runtimeLock=null;}
    }
    public void Dispose() { Stop(); }

    private static ToolCallOutput Output(JsonObject value) =>
        new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString() }), value);
    private static long Integer(JsonNode? value, long max)
    {
        if (value is not JsonValue v || !v.TryGetValue<long>(out var n) || n < 1 || n > max)
            throw Fail("cua_invalid_integer");
        return n;
    }
    internal static void ValidateAction(string name, JsonObject args)
    {
        if (!Actions.TryGetValue(name, out var allowed) || args.Any(kv => !allowed.Contains(kv.Key, StringComparer.Ordinal)) ||
            Encoding.UTF8.GetByteCount(args.ToJsonString()) > 32768)
            throw Fail("cua_invalid_action_arguments");
        if (name == "set_window_frame")
        {
            if(args.Count!=4 || !new[] {"x","y","width","height"}.All(args.ContainsKey)) throw Fail("cua_invalid_window_frame");
            foreach(var (key,value) in args)
                if(value is not JsonValue v || !v.TryGetValue<double>(out var n) || !double.IsFinite(n) || n >32768 ||
                    n < (key is "width" or "height" ? 1 : -32768)) throw Fail("cua_invalid_window_frame");
            return;
        }
        foreach (var (key,value) in args)
        {
            if (key is "x" or "y" or "from_x" or "from_y" or "to_x" or "to_y" or "count" or "amount" or "duration_ms")
            {
                if (value is not JsonValue number || !number.TryGetValue<double>(out var n) || !double.IsFinite(n) || n < 0 || n > 32768)
                    throw Fail("cua_invalid_coordinate_or_amount");
            }
            else if (key is "modifiers" or "path")
            {
                if (value is not JsonArray a || a.Count > 16 ||
                    a.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out var str) || string.IsNullOrEmpty(str) || Encoding.UTF8.GetByteCount(str) > 256))
                    throw Fail("cua_invalid_array");
            }
            else if (value is not JsonValue text || !text.TryGetValue<string>(out var s) ||
                Encoding.UTF8.GetByteCount(s) > (key is "text" or "value" ? 16000 : 1024))
                throw Fail("cua_invalid_string");
        }
        foreach (var (key,min,max) in new[] { ("count",1,3),("amount",1,50),("duration_ms",0,3000) })
            if (args[key] is JsonValue n && (!n.TryGetValue<int>(out var integer) || integer < min || integer > max))
                throw Fail("cua_invalid_integer_action_parameter");
        if (args["button"] is JsonValue button && !new[] {"left","right"}.Contains(button.GetValue<string>(),StringComparer.Ordinal))
            throw Fail("cua_invalid_button");
        if (args["action"] is JsonValue action && !new[] {"expand"}.Contains(action.GetValue<string>(),StringComparer.Ordinal))
            throw Fail("cua_invalid_element_action");
        bool token = args.ContainsKey("element_token"), xy = args.ContainsKey("x") && args.ContainsKey("y");
        if (token && (args.ContainsKey("x") || args.ContainsKey("y") || args.ContainsKey("capture_id") || args.ContainsKey("count")) ||
            args.ContainsKey("x") != args.ContainsKey("y")) throw Fail("cua_ambiguous_action_target");
        bool valid = name switch
        {
            "click" => token != xy && (token ? !args.ContainsKey("x") && !args.ContainsKey("y") : args.ContainsKey("capture_id")),
            "type_text" => token != xy && args.ContainsKey("text"),
            "set_value" => token && args.ContainsKey("value"),
            "press_key" => args.ContainsKey("key"),
            "scroll" => args["direction"] is JsonValue dir && new[] {"up","down","left","right"}.Contains(dir.GetValue<string>(),StringComparer.Ordinal) && token != xy,
            "drag" => new[] {"from_x","from_y","to_x","to_y","capture_id"}.All(args.ContainsKey),
            "invoke_menu" => args["path"] is JsonArray { Count: > 0 },
            _ => false
        };
        if (!valid) throw Fail("cua_action_requires_exact_snapshot_target_and_parameters");
    }

    internal static JsonArray VerifyPredicates(JsonNode? raw)
    {
        if (raw is not JsonArray predicates || predicates.Count is < 1 or > 8)
            throw Fail("cua_invalid_verify_predicates");
        foreach (var node in predicates)
        {
            if (node is not JsonObject predicate || predicate.Count != 1 || predicate["element"] is not JsonObject element ||
                element.Any(kv => !new[] {"selector","exists","enabled","selected","value_equals"}.Contains(kv.Key,StringComparer.Ordinal)) ||
                element["selector"] is not JsonObject selector || selector.Count == 0 || element.Count < 2 ||
                selector.Any(kv => !new[] {"role","label_contains"}.Contains(kv.Key,StringComparer.Ordinal) ||
                    kv.Value is not JsonValue value || !value.TryGetValue<string>(out var str) || string.IsNullOrEmpty(str) || Encoding.UTF8.GetByteCount(str) > 256))
                throw Fail("cua_invalid_verify_predicate");
            foreach (var key in new[] {"exists","enabled","selected"})
                if (element.ContainsKey(key) && (element[key] is not JsonValue value || !value.TryGetValue<bool>(out var b) || key == "exists" && !b))
                    throw Fail("cua_invalid_verify_boolean");
            if (element.ContainsKey("value_equals") && (element["value_equals"] is not JsonValue v ||
                !v.TryGetValue<string>(out var text) || Encoding.UTF8.GetByteCount(text) > 16000))
                throw Fail("cua_invalid_verify_value");
        }
        return (JsonArray)predicates.DeepClone();
    }

    private bool ExtensionAllowed(ExtensionSession e)
    {
        lock(_gate)if(_stopped||_revoked.Contains(e.Owner)||!_extensions.TryGetValue(e.Id,out var live)||!ReferenceEquals(live,e))return false;
        try{return (e.Profile.kind!="guest"||(e.GuestEpoch is not null&&GuestEpoch()==e.GuestEpoch))&&LoadPolicy().Item2.AsSpan().SequenceEqual(e.PolicyBytes);}catch{return false;}
    }
    private void RetireExtension(string id)
    {
        ExtensionSession? e;lock(_gate)_extensions.TryGetValue(id,out e);
        e?.Worker.Dispose();lock(_gate)_extensions.Remove(id);
    }
    private static void CheckFile(string path,string hash)
    {
        var file=new FileInfo(path);
        if(!Path.IsPathFullyQualified(path)||!file.Exists||(file.Attributes&FileAttributes.ReparsePoint)!=0||file.Length is <=0 or >200_000_000)
            throw Fail("cua_extension_integrity_mismatch");
        using var stream=file.OpenRead();
        if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash,StringComparison.OrdinalIgnoreCase))throw Fail("cua_extension_integrity_mismatch");
    }
    private static ToolCallOutput ExtensionResult(JsonObject reply,string id)
    {
        if(reply["isError"]?.GetValue<bool>()==true)
        {
            var text=(reply["content"] as JsonArray)?.FirstOrDefault()?["text"]?.GetValue<string>()??"unknown";
            throw Fail("cua_extension_refused: "+text[..Math.Min(text.Length,1000)]);
        }
        if(reply["content"] is not JsonArray content||reply["structuredContent"] is not JsonObject structured||
            Encoding.UTF8.GetByteCount(structured.ToJsonString())>2_000_000)throw Fail("invalid_extension_response");
        var safe=new JsonArray();
        foreach(var item in content.OfType<JsonObject>())
        {
            if(item["type"]?.GetValue<string>()=="text"&&item["text"] is JsonValue t&&t.TryGetValue<string>(out var text)&&Encoding.UTF8.GetByteCount(text)<=2_000_000)
                safe.Add(new JsonObject{["type"]="text",["text"]=text});
            else if(item["type"]?.GetValue<string>()=="image"&&item["mimeType"]?.GetValue<string>() is "image/png" or "image/jpeg" &&
                item["data"] is JsonValue d&&d.TryGetValue<string>(out var data)&&data.Length<=8_000_000)
            {
                _=Convert.FromBase64String(data);safe.Add(new JsonObject{["type"]="image",["mimeType"]=item["mimeType"]!.DeepClone(),["data"]=data});
            }
        }
        if(safe.Count==0)throw Fail("invalid_extension_content");
        var outer=new JsonObject{["session_id"]=id,["result"]=structured.DeepClone()};
        var blocks=new JsonArray(new JsonObject{["type"]="text",["text"]=outer.ToJsonString()});
        foreach(var image in safe.OfType<JsonObject>())blocks.Add(image.DeepClone());
        return new ToolCallOutput(blocks,outer);
    }
    private async Task<ToolCallOutput> ExtensionCallAsync(string name,JsonObject args,string owner,CancellationToken cancellation)
    {
        if(name=="cua_extension_close")
        {
            var id=args["session_id"]?.GetValue<string>()??"";
            lock(_gate)if(!_extensions.TryGetValue(id,out var e)||e.Owner!=owner)throw Fail("unknown_extension_session");
            RetireExtension(id);return Output(new JsonObject{["closed"]=true});
        }
        var (policy,bytes)=LoadPolicy();
        if(!_guestMode&&policy.requireVerifiedClient==true&&owner==SharedOwner)throw Fail("cua_verified_client_required");
        var profiles=(policy.profiles??[]).Where(p=>p.clients.Contains(owner,StringComparer.Ordinal)&&(p.kind!="guest"||GuestEpoch() is not null)).ToArray();
        if(name=="cua_extension_list")
            return Output(new JsonObject{["profiles"]=new JsonArray(profiles.Select(p=>(JsonNode?)new JsonObject{["id"]=p.id,["kind"]=p.kind,
                ["origins"]=new JsonArray((p.origins??[]).Select(o=>(JsonNode?)JsonValue.Create(o)).ToArray())}).ToArray())});
        if(name=="cua_extension_open")
        {
            var profile=profiles.FirstOrDefault(p=>p.id==args["profile_id"]?.GetValue<string>());
            var runtime=policy.extensionRuntime;
            if(profile is null||profile.kind is not ("browser" or "guest")||runtime is null)throw Fail("extension_profile_not_approved");
            var approval=profile.kind=="guest"?GuestEpoch():null;
            if(profile.kind=="guest")
            {
                if(approval is null)throw Fail("cua_guest_disabled_by_local_user");
                var file=new FileInfo(profile.tokenFile??throw Fail("guest_private_credential_required"));
                if(!file.Exists||file.Length!=65||(file.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw Fail("guest_private_credential_required");
                using var identity=WindowsIdentity.GetCurrent();
                var current=identity.User??throw Fail("guest_private_credential_required");
                var acl=file.GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access);
                if(!current.Equals(acl.GetOwner(typeof(SecurityIdentifier))))throw Fail("guest_private_credential_required");
                var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
                var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
                foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                    if(rule.AccessControlType==AccessControlType.Allow&&!current.Equals(rule.IdentityReference)&&
                       !system.Equals(rule.IdentityReference)&&!admins.Equals(rule.IdentityReference))
                        throw Fail("guest_private_credential_required");
            }
            CheckFile(runtime.node,runtime.nodeSHA256);CheckFile(runtime.worker,runtime.workerSHA256);
            var request=RequestId(args["request_id"]);var receipt=Ledger();Sweep();
            ExtensionSession created;
            lock(_gate)
            {
                if(_stopped||_revoked.Contains(owner)||_sessions.Count+_extensions.Count+_launching.Count>=8||
                    profile.kind=="guest"&&_extensions.Values.Any(e=>e.Profile.kind=="guest"&&e.Profile.port==profile.port))
                    throw Fail("extension_capacity_or_guest_busy");
                if(profile.kind=="guest"&&(approval is null||GuestEpoch()!=approval))throw Fail("cua_guest_disabled_by_local_user");
                ReserveReceipt(receipt,request,owner);
                var worker=new Worker(runtime.node,"",false,runtime,profile);
                created=new ExtensionSession(owner,profile,bytes,worker,approval);
                _extensions.Add(created.Id,created);
            }
            try
            {
                var reply=await created.Worker.CallAsync("extension_open",new(),()=>ExtensionAllowed(created),cancellation).ConfigureAwait(false);
                if(cancellation.IsCancellationRequested||!ExtensionAllowed(created))throw Fail("extension_open_interrupted");
                var result=ExtensionResult(reply,created.Id);FinishReceipt(receipt,request,"exited");
                lock(_gate){created.Busy=false;created.Last=Environment.TickCount64;}
                return result;
            }
            catch{RetireExtension(created.Id);throw;}
        }
        if(name!="cua_extension_call"||args["operation"] is not JsonValue operation||!operation.TryGetValue<string>(out var op)||
            args["parameters"] is not JsonObject parameters||Encoding.UTF8.GetByteCount(parameters.ToJsonString())>32768)throw Fail("invalid_extension_call");
        var sessionId=args["session_id"]?.GetValue<string>()??"";
        ExtensionSession session;
        lock(_gate)
        {
            if(!_extensions.TryGetValue(sessionId,out var e)||e.Owner!=owner||e.Busy||Environment.TickCount64-e.Last>300000)
                throw Fail("unknown_or_busy_extension_session");
            session=e;session.Busy=true;
        }
        try
        {
            var browserOps=new[]{"observe","navigate","click","fill","press","select","scroll","verify","wait_for","back","forward","hover","drag","tab_list","tab_open","tab_select","tab_close","dialog_prepare","upload","download"};
            var guestOps=new[]{"cua_capabilities","cua_list_apps","cua_list_windows","cua_launch_app","cua_create_session","cua_observe","cua_act","cua_verify","cua_stop_session","cua_receipt_status"};
            if(!(session.Profile.kind=="browser"?browserOps:guestOps).Contains(op,StringComparer.Ordinal)||!ExtensionAllowed(session)||cancellation.IsCancellationRequested)
                throw Fail("extension_operation_not_allowed");
            var readOnly=new[]{"observe","verify","wait_for","tab_list","cua_capabilities","cua_list_apps","cua_list_windows","cua_observe","cua_verify","cua_receipt_status"}.Contains(op,StringComparer.Ordinal);
            var request=RequestId(args["request_id"]);var receipt=Ledger();
            if(!readOnly)ReserveReceipt(receipt,request,owner);
            bool recoverable=false;
            try
            {
                var reply=await session.Worker.CallAsync("extension_call",new JsonObject{["operation"]=op,["parameters"]=parameters.DeepClone()},()=>ExtensionAllowed(session),cancellation).ConfigureAwait(false);
                if(cancellation.IsCancellationRequested||!ExtensionAllowed(session))throw Fail("extension_action_outcome_unknown");
                if(reply["isError"]?.GetValue<bool>()==true&&reply["structuredContent"]?["recoverable"]?.GetValue<bool>()==true){
                    recoverable=true;if(!readOnly)FinishReceipt(receipt,request,"launch_failed");
                }
                var result=ExtensionResult(reply,sessionId);
                if(!readOnly)FinishReceipt(receipt,request,"exited");
                return result;
            }
            catch{if(!recoverable)RetireExtension(sessionId);throw;}
        }
        finally{lock(_gate){session.Busy=false;session.Last=Environment.TickCount64;}}
    }

    internal async Task<ToolCallOutput> CallAsync(string name, JsonObject args, string? verifiedOwner, CancellationToken cancellation)
    {
        if(!ListenerAllows(name,_guestMode,_relayOnly))throw Fail("cua_listener_scope_denied");
        var owner = verifiedOwner ?? SharedOwner;
        var def = Definitions().FirstOrDefault(x => x?["name"]?.GetValue<string>() == name);
        if (def?["inputSchema"] is not JsonObject schema || schema["properties"] is not JsonObject props ||
            schema["required"] is not JsonArray required || args.Any(kv => !props.ContainsKey(kv.Key)) ||
            required.Any(x => !args.ContainsKey(x!.GetValue<string>())))
            throw Fail("cua_invalid_arguments");
        lock (_gate) if (_stopped || _revoked.Contains(owner) || cancellation.IsCancellationRequested) throw Fail("cua_service_stopped_or_revoked");
        if (name == "cua_capabilities")
        {
            bool enabled = false;
            bool extEnabled=false,requiresVerified=false;var guestEnabled=GuestEpoch() is not null;
            try { var activePolicy=LoadPolicy().Item1;requiresVerified=!_guestMode&&activePolicy.requireVerifiedClient==true;extEnabled=(!requiresVerified||owner!=SharedOwner)&&!_guestMode&&(activePolicy.profiles??[]).Any(p=>p.clients.Contains(owner,StringComparer.Ordinal)&&(p.kind!="guest"||guestEnabled));
                enabled=(!requiresVerified||owner!=SharedOwner)&&(extEnabled||activePolicy.grants.Any(g=>g.clients.Contains(owner,StringComparer.Ordinal))); } catch { }
            return Output(new JsonObject { ["enabled"] = enabled, ["background_only"] = true, ["driver"] = "cua-driver", ["driver_version"] = "0.61.0", ["driver_mode"] = "bounded_direct",
                ["identity"] = _relayOnly?"dedicated_relay_credential":_guestMode?"dedicated_guest_connection":owner == SharedOwner ? "shared_connection_not_chat_identity" : "verified_oauth_client", ["conversation_identity_verified"]=false,["relay_only"]=_relayOnly,["host_tools_allowed"]=!_guestMode&&!_relayOnly,["requires_verified_client"]=requiresVerified,
                ["operations"] = new JsonArray(Actions.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["requires_local_grant"] = true, ["interactive_session"] = OperatingSystem.IsWindows() && Environment.UserInteractive, ["snapshot_ttl_seconds"]=30,["guest_mode"]=_guestMode,["guest_enabled"]=guestEnabled,["preferred_execution"]=(_guestMode||_relayOnly)?"explicit_guest":"native_background",["extension_profiles_enabled"]=extEnabled });
        }
        if((_guestMode||_relayOnly)&&GuestEpoch() is null)throw Fail("cua_guest_disabled_by_local_user");
        if(!_guestMode&&owner==SharedOwner)
        {
            bool strict=false;try{strict=LoadPolicy().Item1.requireVerifiedClient==true;}catch{}
            if(strict)throw Fail("cua_verified_client_required");
        }
        if(name.StartsWith("cua_extension_",StringComparison.Ordinal)){if(_guestMode)throw Fail("nested_guest_extensions_disabled");return await ExtensionCallAsync(name,args,owner,cancellation).ConfigureAwait(false);}
        if (name == "cua_receipt_status") { var receiptPolicy=LoadPolicy().Item1;if(!_guestMode&&receiptPolicy.requireVerifiedClient==true&&owner==SharedOwner)throw Fail("cua_verified_client_required");return Output(Ledger().OwnReceipt("cua",RequestId(args["request_id"]),owner)); }
        if (name == "cua_stop_session")
        {
            var id = args["session_id"]?.GetValue<string>() ?? throw Fail("cua_invalid_session");
            lock (_gate)
                if (!_sessions.TryGetValue(id, out var stoppedSession) || stoppedSession.Owner != owner) throw Fail("cua_unknown_or_unauthorized_session");
            Retire(id);
            return Output(new JsonObject { ["stopped"] = true });
        }
        var (policy,bytes) = LoadPolicy();
        if(!_guestMode&&policy.requireVerifiedClient==true&&owner==SharedOwner)throw Fail("cua_verified_client_required");
        if (!policy.grants.Any(g => g.clients.Contains(owner, StringComparer.Ordinal))) throw Fail("cua_client_not_approved");
        if(name=="cua_launch_app")
        {
            var application=args["application"]?.GetValue<string>() ?? "";
            if(!AllowedApplication(application) || !policy.PermitsLaunch(application,owner)) throw Fail("cua_launch_not_approved");
            var request=RequestId(args["request_id"]); var receipt=Ledger(); var driver=CheckedDriver(policy);
            lock(_gate)
            {
                if(_stopped || _revoked.Contains(owner) || _launching.Contains(application) || _sessions.Count+_extensions.Count+_launching.Count>=8 ||
                    _sessions.Values.Any(s=>s.Target.Application.Equals(application,StringComparison.OrdinalIgnoreCase)))
                    throw Fail("cua_target_busy_or_capacity");
                _launching.Add(application);
            }
            try
            {
                ReserveReceipt(receipt,request,owner);
                using var worker=new Worker(driver,application,true);
                lock(_gate) _launchWorkers[application]=(owner,worker);
                bool Allowed()
                {
                    lock(_gate) if(_stopped || _revoked.Contains(owner))return false;
                    try {return !cancellation.IsCancellationRequested && LoadPolicy().Item2.AsSpan().SequenceEqual(bytes);}
                    catch{return false;}
                }
                var result=await worker.CallAsync("launch_app",new JsonObject{["path"]=application,["start_minimized"]=true},Allowed,cancellation).ConfigureAwait(false);
                if(!Allowed() || result["isError"]?.GetValue<bool>()==true ||
                    result["structuredContent"]?["pid"] is not JsonValue p || !p.TryGetValue<int>(out var launchedPid))
                    throw Fail("cua_launch_unverified_or_refused: inspect actual state; do not replay");
                if(!ApprovedApp(launchedPid,policy,owner).Item1.Equals(application,StringComparison.OrdinalIgnoreCase))throw Fail("cua_launch_target_mismatch");
                FinishReceipt(receipt,request,"exited");
                return Output(new JsonObject {["pid"]=launchedPid,["application"]=application,["windows"]=Windows(launchedPid),["request_id"]=request,["background_only"]=true});
            }
            finally { lock(_gate){_launching.Remove(application);_launchWorkers.Remove(application);} }
        }
        if (name == "cua_list_apps")
        {
            var apps = new JsonArray();
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try { var (app,_) = ApprovedApp(proc.Id,policy,owner); apps.Add(new JsonObject { ["pid"] = proc.Id, ["application"] = app, ["name"] = proc.ProcessName }); }
                    catch { }
                }
            }
            return Output(new JsonObject { ["apps"] = apps, ["approved_launch_applications"] = new JsonArray(policy.grants.Where(g=>(g.launchClients??[]).Contains(owner,StringComparer.Ordinal)&&AllowedApplication(g.application)).Select(g=>(JsonNode?)JsonValue.Create(g.application)).ToArray()) });
        }
        if (name == "cua_list_windows")
        {
            var pid = (int)Integer(args["pid"], int.MaxValue);
            _ = ApprovedApp(pid,policy,owner);
            return Output(new JsonObject { ["windows"] = Windows(pid) });
        }
        if (name == "cua_list_sessions")
        {
            Sweep();
            var list = new JsonArray();
            lock (_gate)
                if (owner != SharedOwner)
                    foreach (var session in _sessions.Values.Where(s => s.Owner == owner))
                        list.Add(new JsonObject { ["session_id"] = session.Id, ["pid"] = session.Target.Pid, ["window_id"] = session.Target.Window,["kind"]="native" });
            lock(_gate)if(owner!=SharedOwner)foreach(var e in _extensions.Values.Where(x=>x.Owner==owner))list.Add(new JsonObject{["session_id"]=e.Id,["profile_id"]=e.Profile.id,["kind"]=e.Profile.kind});
            return Output(new JsonObject { ["sessions"] = list, ["shared_session_ids_hidden"] = owner == SharedOwner });
        }
        if (name == "cua_create_session")
        {
            AcquireRuntimeLock();
            var pid = (int)Integer(args["pid"], int.MaxValue);
            var window = Integer(args["window_id"], long.MaxValue);
            var target = GetTarget(pid,window,policy,owner);
            Sweep();
            lock (_gate)
            {
                if (_stopped || _revoked.Contains(owner) || _sessions.Count + _extensions.Count + _launching.Count >= 8 || _launching.Contains(target.Application) || _sessions.Values.Any(s => s.Target.Pid == pid))
                    throw Fail("cua_target_busy_or_capacity");
                var worker = new Worker(CheckedDriver(policy),target.Application);
                var created = new Session(owner,target,bytes,worker);
                _sessions.Add(created.Id,created);
                return Output(new JsonObject { ["session_id"] = created.Id, ["pid"] = pid, ["window_id"] = window,
                    ["background_only"] = true, ["idle_timeout_seconds"] = 300 });
            }
        }
        var sessionId = args["session_id"]?.GetValue<string>() ?? throw Fail("cua_invalid_session");
        Session s;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId,out var selected) || selected.Owner != owner || selected.Busy || _stopped ||
                Environment.TickCount64 - selected.Last > 300000) throw Fail("cua_unknown_unauthorized_or_busy_session");
            s = selected; s.Busy = true;
        }
        try
        {
            bool mutation = name == "cua_act";
            if (!StillAllowed(s,mutation) || cancellation.IsCancellationRequested) { s.Snapshot=null; throw Fail("cua_target_unavailable_or_user_active"); }
            var driverArgs = new JsonObject { ["pid"] = s.Target.Pid, ["window_id"] = s.Target.Window };
            string driverTool;
            (CommandReceiptLedger Ledger,string Id)? actionReceipt=null;
            if (mutation)
            {
                if (s.Snapshot is not { } snapshot || args["operation"] is not JsonValue op || !op.TryGetValue<string>(out var operation) ||
                    args["parameters"] is not JsonObject parameters) throw Fail("cua_observe_before_action");
                ValidateAction(operation,parameters);
                if(operation!="set_window_frame") snapshot.Validate(parameters); else snapshot.Validate(new JsonObject());
                var request=RequestId(args["request_id"]);var receipt=Ledger();
                ReserveReceipt(receipt,request,owner);
                actionReceipt=(receipt,request);
                foreach (var (key,value) in parameters)
                    if (key != "capture_id" || operation == "click") driverArgs[key] = value?.DeepClone();
                if (operation is not ("invoke_menu" or "set_window_frame") && snapshot.Revision is long revision) driverArgs["ui_tree_revision"] = revision;
                if(operation!="set_window_frame")driverArgs["delivery_mode"] = "background";
                if (operation is not ("set_value" or "invoke_menu" or "set_window_frame")) driverArgs["scope"] = "window";
                driverTool = operation;
                s.Snapshot = null;
            }
            else if (name == "cua_verify")
            {
                var expect = VerifyPredicates(args["expect"]);
                int timeout = 3000;
                if (args.ContainsKey("timeout_ms") && (args["timeout_ms"] is not JsonValue value ||
                    !value.TryGetValue<int>(out timeout) || timeout is < 0 or > 6000))
                    throw Fail("cua_invalid_verify_timeout");
                driverTool = "verify_state";
                driverArgs["expect"] = expect;
                driverArgs["timeout_ms"] = timeout;
                driverArgs["include_screenshot"] = false;
                s.Snapshot = null;
            }
            else
            {
                if (name != "cua_observe" || (args.ContainsKey("include_screenshot") &&
                    (args["include_screenshot"] is not JsonValue b || !b.TryGetValue<bool>(out _)))) throw Fail("cua_invalid_observe");
                driverTool = "get_window_state";
                driverArgs["include_screenshot"] = args["include_screenshot"]?.GetValue<bool>() ?? true;
                driverArgs["include_accessibility_tree"] = true;
                driverArgs["max_image_dimension"] = 1600;
                driverArgs["tree_format"] = "both";
                driverArgs["max_elements"] = 250;
                s.Snapshot = null;
            }
            try
            {
                var result = await s.Worker.CallAsync(driverTool, driverArgs, () => StillAllowed(s,mutation), cancellation).ConfigureAwait(false);
                if (!StillAllowed(s,mutation) || cancellation.IsCancellationRequested) throw Fail("cua_action_outcome_unknown");
                if (result["isError"]?.GetValue<bool>() == true)
                {
                    var message = (result["content"] as JsonArray)?.FirstOrDefault()?["text"]?.GetValue<string>() ?? "background unavailable";
                    throw Fail("cua_driver_rejected: " + message[..Math.Min(message.Length,2000)]);
                }
                if (result["content"] is not JsonArray blocks) throw Fail("cua_driver_invalid_content");
                var safe = new JsonArray();
                foreach (var item in blocks)
                {
                    if (item is not JsonObject block) continue;
                    if (block["type"]?.GetValue<string>() == "text" && block["text"] is JsonValue t && t.TryGetValue<string>(out var text))
                    {
                        if (Encoding.UTF8.GetByteCount(text) > 2_000_000) throw Fail("cua_driver_text_too_large");
                        safe.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    }
                    else if (!mutation && block["type"]?.GetValue<string>() == "image" &&
                        block["mimeType"]?.GetValue<string>() is "image/png" or "image/jpeg" &&
                        block["data"] is JsonValue d && d.TryGetValue<string>(out var data) && data.Length <= 8_000_000)
                    {
                        _ = Convert.FromBase64String(data);
                        safe.Add(new JsonObject { ["type"] = "image", ["mimeType"] = block["mimeType"]!.DeepClone(), ["data"] = data });
                    }
                }
                if (safe.Count == 0) throw Fail("cua_driver_invalid_content");
                var structured = result["structuredContent"] as JsonObject ?? new JsonObject();
                if (structured.ContainsKey("foreground_step")) throw Fail("cua_driver_reported_foreground_step");
                if (name == "cua_observe") s.Snapshot = new Snapshot(structured,safe);
                var metadata = (JsonObject)structured.DeepClone();
                foreach (var key in new[] {"screenshot","screenshot_base64","image","image_data","screenshot_file_path"}) metadata.Remove(key);
                if (Encoding.UTF8.GetByteCount(metadata.ToJsonString()) > 2_000_000) throw Fail("cua_driver_metadata_too_large");
                if(actionReceipt is { } ar) FinishReceipt(ar.Ledger,ar.Id,"exited");
                return new ToolCallOutput(safe, new JsonObject { ["session_id"] = sessionId, ["operation"] = driverTool,
                    ["background_only"] = true, ["verification_required"] = mutation, ["driver_result"] = metadata });
            }
            catch { Retire(sessionId); throw; }
        }
        finally { lock (_gate) { s.Busy = false; s.Last = Environment.TickCount64; } }
    }

    internal sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _buffer = new byte[65536];
        private int _offset, _length, _id, _closed;
        private bool _initialized;
        private readonly string _manifestDirectory;
        private readonly WindowsJob? _job;
        private readonly bool _isExtension;
        internal Worker(string path,string application,bool launch=false,ExtensionRuntime? runtime=null,ExtensionProfile? profile=null)
        {
            _isExtension=runtime is not null;
            _manifestDirectory = Path.Combine(Path.GetTempPath(),"ctcmcp-cua-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_manifestDirectory);
            var manifestPath = Path.Combine(_manifestDirectory,"capability.json");
            try
            {
                var manifest = new {
                    version = 3, expires_after = "2h", idle_timeout = "10m",
                    allow = new { tools = new[] {"check_permissions","get_window_state","click","type_text","press_key","scroll","drag","set_value","invoke_menu","verify_state","set_window_frame"}.Concat(launch?new[]{"launch_app"}:Array.Empty<string>()).ToArray() },
                    resources = new { apps = new[] { new { executable = application, launch, windows = "all" } },
                        desktop = new { display = false } }
                };
                File.WriteAllText(manifestPath,profile is null?JsonSerializer.Serialize(manifest):JsonSerializer.Serialize(profile));
                var info = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                info.ArgumentList.Add(runtime?.worker??"mcp");
                var keep = new[] {"SYSTEMROOT","WINDIR","SYSTEMDRIVE","USERPROFILE","USERNAME","APPDATA","LOCALAPPDATA","TEMP","TMP","PATH","PATHEXT","COMSPEC","PROGRAMDATA","HOMEDRIVE","HOMEPATH"};
                foreach (var key in info.Environment.Keys.Where(k => !keep.Contains(k,StringComparer.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
                info.Environment["CUA_DRIVER_PERMISSION_MODE"] = "bounded";
                info.Environment["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"] = manifestPath;
                info.Environment["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] = "1";
                info.Environment["DO_NOT_TRACK"] = "1";
                info.Environment["CUA_DRIVER_RS_TELEMETRY_ENABLED"] = "false";
                if(runtime is not null){
                    info.Environment["CTCMCP_CUA_EXTENSION_MANIFEST"]=manifestPath;
                    info.Environment["PLAYWRIGHT_BROWSERS_PATH"]=Path.Combine(Path.GetDirectoryName(runtime.worker)!,"browsers");
                }
                _process = Process.Start(info) ?? throw Fail("cua_driver_unavailable");
                // Attach desktop workers to kill-on-close supervision. One-shot
                // launch workers must not kill the launched user application.
                if (!launch) _job=WindowsJob.CreateAndAssign(_process);
            }
            catch
            {
                try { Directory.Delete(_manifestDirectory,true); } catch { }
                throw;
            }
            _input = _process.StandardInput.BaseStream;
            _output = _process.StandardOutput.BaseStream;
            // Drain stderr without retaining private desktop content.
            _ = _process.StandardError.BaseStream.CopyToAsync(Stream.Null).ContinueWith(_ => {}, TaskScheduler.Default);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed,1) != 0) return;
            _stop.Cancel();
            if(_isExtension){try{_input.Close();_process.WaitForExit(2000);}catch{}}
            try { _job?.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.Kill(entireProcessTree:_job is not null); } catch { }
            try { _process.WaitForExit(2000); } catch { }
            try { _process.Dispose(); } catch { }
            try { Directory.Delete(_manifestDirectory,true); } catch { }
        }
        private async Task SendAsync(JsonObject message, CancellationToken token)
        {
            var data = Encoding.UTF8.GetBytes(message.ToJsonString()+"\n");
            await _input.WriteAsync(data,token).ConfigureAwait(false);
            await _input.FlushAsync(token).ConfigureAwait(false);
        }
        private async Task<JsonObject> ReadAsync(int id, CancellationToken token)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_offset == _length)
                {
                    _length = await _output.ReadAsync(_buffer,token).ConfigureAwait(false); _offset = 0;
                    if (_length == 0) throw Fail("cua_driver_disconnected");
                }
                var newline = Array.IndexOf(_buffer,(byte)10,_offset,_length-_offset);
                var end = newline < 0 ? _length : newline;
                if (line.Length + end - _offset > 12_000_000) throw Fail("cua_driver_response_too_large");
                line.Write(_buffer,_offset,end-_offset);
                _offset = newline < 0 ? _length : newline+1;
                if (newline < 0) continue;
                var reply = JsonNode.Parse(line.ToArray()) as JsonObject ?? throw Fail("cua_driver_protocol_error");
                line.SetLength(0);
                if (reply["id"] is JsonValue v && v.TryGetValue<int>(out var actual) && actual == id)
                    return reply["result"] as JsonObject ?? throw Fail("cua_driver_rpc_error");
                if (reply.ContainsKey("id")) throw Fail("cua_driver_protocol_error");
            }
        }
        private async Task<JsonObject> RpcAsync(string method, JsonObject args, CancellationToken token)
        {
            var id = ++_id;
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args },token).ConfigureAwait(false);
            return await ReadAsync(id,token).ConfigureAwait(false);
        }
        internal async Task<JsonObject> CallAsync(string tool, JsonObject args, Func<bool> allowed, CancellationToken cancellation)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation,_stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var abort = deadline.Token.Register(() => { try { if (!_process.HasExited) _process.Kill(entireProcessTree:_job is not null); } catch { } });
            using var watchdog = new Timer(_ => { try { if (!allowed()) deadline.Cancel(); } catch { try { deadline.Cancel(); } catch { } } }, null, 0, 100);
            try
            {
                if (!allowed()) throw Fail("cua_session_revoked");
                if (!_initialized)
                {
                    var init = await RpcAsync("initialize",new JsonObject { ["protocolVersion"] = "2025-06-18",
                        ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "ChatToComputerMCP", ["version"] = "1" } },deadline.Token).ConfigureAwait(false);
                    if (init["protocolVersion"]?.GetValue<string>() != "2025-06-18" || init["serverInfo"] is not JsonObject info ||
                        info["name"]?.GetValue<string>() != (_isExtension?"ctcmcp-cua-extension":"cua-driver") || info["version"]?.GetValue<string>() != (_isExtension?"1.0.0":"0.61.0"))
                        throw Fail("cua_driver_protocol_or_version_mismatch");
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },deadline.Token).ConfigureAwait(false);
                    _initialized = true;
                }
                return await RpcAsync("tools/call",new JsonObject { ["name"] = tool, ["arguments"] = args },deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw Fail("cua_driver_timeout_or_revoked: action outcome may be unknown; observe again in a new session"); }
        }
    }
}
