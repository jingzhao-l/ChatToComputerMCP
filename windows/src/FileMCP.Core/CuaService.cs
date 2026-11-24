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

/// Per-dispatch foreground continuity latch. It cannot move or restore focus.
internal sealed class CuaForegroundContinuity
{
    private readonly Func<long?> _read;
    private readonly long? _initial;
    private readonly object _gate=new();
    private bool _interrupted;
    internal CuaForegroundContinuity(Func<long?> read){_read=read;_initial=read();}
    internal bool Permits()
    {
        lock(_gate){
            if(_interrupted||_initial is null or 0||_read()!=_initial){_interrupted=true;return false;}
            return true;
        }
    }
    internal void Invalidate(){lock(_gate)_interrupted=true;}
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
    internal sealed record Target(int Pid,long Window,string Application,long Launched)
    {
        internal bool SameProcess(Target other)=>Pid==other.Pid&&Launched==other.Launched&&Application.Equals(other.Application,StringComparison.OrdinalIgnoreCase);
    }
    internal sealed record WindowGeometry(int Left,int Top,int Right,int Bottom,bool Visible,bool Minimized,uint Dpi)
    {
        internal bool PixelEligible=>Visible&&!Minimized&&Dpi>0&&Right>Left&&Bottom>Top;
    }
    private sealed class Session(string owner, Target target, byte[] policyBytes, Worker worker)
    {
        internal readonly string Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        internal readonly string Owner = owner;
        internal readonly Target Target = target;
        internal readonly byte[] PolicyBytes = policyBytes;
        internal readonly Worker Worker = worker;
        internal long Last = Environment.TickCount64;
        internal bool Busy;
        internal bool Retiring;
        internal readonly TaskCompletionSource<bool> Retired=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Snapshot? Snapshot;
    }
    internal sealed class Snapshot
    {
        internal readonly string Id;
        internal readonly string ObservationId=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        internal readonly long? Revision;
        internal readonly string? CaptureId;
        internal readonly uint Width, Height;
        internal readonly long ObservedAt = Environment.TickCount64;
        internal readonly HashSet<string> Tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string,JsonObject> _elementEvidence=new(StringComparer.Ordinal);
        internal readonly WindowGeometry? Geometry;
        internal readonly bool TreeGrounded;
        internal bool PixelGrounded=>CaptureId is not null&&Geometry?.PixelEligible==true;
        internal bool Actionable=>TreeGrounded||PixelGrounded;
        internal Snapshot(JsonObject structured, JsonArray blocks,WindowGeometry? geometry=null,bool includeElements=true)
        {
            Id = structured["snapshot_id"]?.GetValue<string>() ?? "";
            if (Id.Length>0&&!Regex.IsMatch(Id, "^s[0-9a-f]{8}$")) throw Fail("cua_invalid_snapshot_id");
            Geometry=geometry;TreeGrounded=includeElements&&Id.Length>0;
            if (TreeGrounded&&structured["ui_tree_revision"] is JsonValue revision && revision.TryGetValue<long>(out var r)) Revision = r;
            if(TreeGrounded&&structured["elements"] is JsonArray elements){
                var duplicates=new HashSet<string>(StringComparer.Ordinal);
                foreach(var element in elements)
                    if(element is JsonObject row&&row["element_token"] is JsonValue value&&value.TryGetValue<string>(out var token)&&
                       token.StartsWith(Id+":",StringComparison.Ordinal)&&Regex.IsMatch(token,@"\As[0-9a-f]{8}:[0-9]+\z")){
                        if(!_elementEvidence.TryAdd(token,row))duplicates.Add(token);
                    }
                foreach(var token in duplicates)_elementEvidence.Remove(token);
                Tokens.UnionWith(_elementEvidence.Keys);
            }
            var image = blocks.OfType<JsonObject>().FirstOrDefault(b =>
                b["type"]?.GetValue<string>() == "image" && b["mimeType"]?.GetValue<string>() == "image/png");
            if (image?["data"] is JsonValue d && d.TryGetValue<string>(out var data) &&
                structured["capture_id"] is JsonValue capture && capture.TryGetValue<string>(out var captureId) && !string.IsNullOrEmpty(captureId)&&Encoding.UTF8.GetByteCount(captureId)<=1024)
            {
                var bytes = Convert.FromBase64String(data);
                if(bytes.Length>=24&&bytes.AsSpan(0,16).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82})){
                    uint w=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)),h=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
                    if(w is >=1 and <=8192&&h is >=1 and <=8192&&
                       (!structured.ContainsKey("screenshot_width")||structured["screenshot_width"] is JsonValue sw&&sw.TryGetValue<uint>(out var rw)&&rw==w)&&
                       (!structured.ContainsKey("screenshot_height")||structured["screenshot_height"] is JsonValue sh&&sh.TryGetValue<uint>(out var rh)&&rh==h)){
                        Width=w;Height=h;CaptureId=captureId;
                    }
                }
            }
        }
        internal JsonObject? ValidatedControlArguments(string operation,JsonObject args)
        {
            var result=(JsonObject)args.DeepClone();
            if(args["element_token"] is JsonValue value&&value.TryGetValue<string>(out var token)){
                if(!_elementEvidence.TryGetValue(token,out var row))return null;
                if(row["enabled"] is JsonValue e&&e.TryGetValue<bool>(out var enabled)&&!enabled)return null;
            }
            // Windows default click performs the native Invoke/Toggle/
            // SelectionItem/ExpandCollapse pattern ladder. "press" is the
            // portable spelling for that default, not an unsupported action.
            if(operation=="click"&&args["action"]?.GetValue<string>()=="press")result.Remove("action");
            return result;
        }
        internal void ValidatePixelGeometry(WindowGeometry? current){if(!PixelGrounded||current!=Geometry)throw Fail("cua_window_geometry_changed: observe the window again");}
        internal void ValidateObservationId(JsonNode? raw){if(raw is not JsonValue v||!v.TryGetValue<string>(out var id)||id!=ObservationId)throw Fail("cua_stale_or_foreign_observation");}
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
        internal bool Retiring;
        internal readonly string? GuestEpoch=guestEpoch;
        internal readonly TaskCompletionSource<bool> Retired=new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        ["type_text"] = ["element_token","text","x","y","capture_id","delay_ms"],
        ["press_key"] = ["key","modifiers","element_token","x","y","capture_id"],
        ["hotkey"] = ["keys","element_token","x","y","capture_id"],
        ["scroll"] = ["element_token","direction","amount","x","y","capture_id","by"],
        ["drag"] = ["from_x","from_y","to_x","to_y","duration_ms","capture_id","steps","button"],
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
            Def("cua_create_session", "Acquire an exclusive background window session, or explicitly transfer an idle owned session to another window in the same process using previous_session_id. Re-observe before input.",
                new() { ["pid"] = Integer(), ["window_id"] = Integer(),["previous_session_id"]=String() }, ["pid","window_id"], false),
            Def("cua_list_sessions", "List own session status. Shared connections cannot enumerate session IDs.", new(), [], true),
            Def("cua_observe", "Observe the exact leased window: accessibility tree, image, or both. Only a returned observation_id can ground input.",
                new() { ["session_id"] = String(), ["include_screenshot"] = Bool(),["include_accessibility_tree"]=Bool(),["query"]=String(),["max_elements"]=Integer(),["max_depth"]=Integer(),["max_image_dimension"]=Integer(),["timeout_ms"]=Integer() }, ["session_id"], false),
            Def("cua_act", "Perform one background-only action; optional post_observe requests a fresh same-window observation in the remaining time budget. No foreground fallback.",
                new() { ["session_id"] = String(), ["request_id"] = String(), ["observation_id"]=String(), ["operation"] = new JsonObject { ["type"] = "string",
                    ["enum"] = new JsonArray(Actions.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) },
                    ["parameters"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                    ["post_observe"]=new JsonObject{["type"]="object",["additionalProperties"]=false,["properties"]=new JsonObject{
                        ["include_screenshot"]=Bool(),["include_accessibility_tree"]=Bool(),["query"]=String(),["max_elements"]=Integer(),["max_depth"]=Integer(),["max_image_dimension"]=Integer(),["timeout_ms"]=Integer()}} },
                ["session_id","request_id","observation_id","operation","parameters"], false),
            Def("cua_verify", "Verify bounded element predicates in the leased window; unknown is not success.",
                new() { ["session_id"] = String(), ["expect"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["items"] = new JsonObject { ["type"] = "object" } },
                    ["timeout_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 6000 },["stable_samples"]=Integer() }, ["session_id","expect"], true),
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
        byte[] bytes;
        using(var stream=new FileStream(_policyPath,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)){
            if(stream.Length is <=0 or >65536)throw Fail("cua_disabled: invalid policy size");
            bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
        }
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
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    private static bool OwnsTopLevelWindow(int pid,long window){
        var hwnd=new IntPtr(window);
        return IsWindow(hwnd)&&GetWindowThreadProcessId(hwnd,out uint owner)!=0&&owner==(uint)pid&&GetAncestor(hwnd,2)==hwnd;
    }
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
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private static WindowGeometry? ReadGeometry(Target target)
    {
        var hwnd=new IntPtr(target.Window);
        if(!IsWindow(hwnd)||GetWindowThreadProcessId(hwnd,out uint pid)==0||pid!=target.Pid||!GetWindowRect(hwnd,out var r))return null;
        return new WindowGeometry(r.Left,r.Top,r.Right,r.Bottom,IsWindowVisible(hwnd),IsIconic(hwnd),GetDpiForWindow(hwnd));
    }
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
            var ownerWindow=GetWindow(hwnd,4); // GW_OWNER
            var row=new JsonObject { ["window_id"] = hwnd.ToInt64(), ["title"] = text.ToString(),["bounds"]=bounds,["on_screen"]=IsWindowVisible(hwnd)&&!IsIconic(hwnd),["minimized"]=IsIconic(hwnd),["enabled"]=IsWindowEnabled(hwnd) };
            if(ownerWindow!=IntPtr.Zero&&GetWindowThreadProcessId(ownerWindow,out var ownerPid)!=0&&ownerPid==pid)row["owner_window_id"]=ownerWindow.ToInt64();
            list.Add(row);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    private static Target GetTarget(int pid, long window, Policy policy, string owner)
    {
        var (app, start) = ApprovedApp(pid, policy, owner);
        if(!OwnsTopLevelWindow(pid,window))
            throw Fail("cua_window_not_owned_or_unavailable");
        return new Target(pid, window, app, start);
    }
    private bool StillAllowed(Session s, bool mutation,bool requireWindow=true)
    {
        lock (_gate)
            if (_stopped || s.Retiring || !s.Worker.IsAlive || s.Worker.AgeSeconds>=6900 || !_sessions.TryGetValue(s.Id, out var live) || !ReferenceEquals(live, s) || _revoked.Contains(s.Owner)) return false;
        try
        {
            var (policy, bytes) = LoadPolicy();
            if(!bytes.AsSpan().SequenceEqual(s.PolicyBytes))return false;
            var (app,start)=ApprovedApp(s.Target.Pid,policy,s.Owner);
            if(!app.Equals(s.Target.Application,StringComparison.OrdinalIgnoreCase)||start!=s.Target.Launched)return false;
            if(requireWindow&&!OwnsTopLevelWindow(s.Target.Pid,s.Target.Window))return false;
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
            if(!ExtensionAllowed(e))RetireExtension(e.Id);else if(expired)RetireExtension(e.Id,true);
        }
        foreach (var s in all)
        {
            bool expired;
            lock (_gate) expired = !s.Busy && Environment.TickCount64 - s.Last > 300000;
            if (!StillAllowed(s, false,requireWindow:false)) Retire(s.Id); else if(expired)Retire(s.Id,true);
        }
    }
    private bool Retire(string id,bool onlyIdle=false,bool waitForCleanup=false)
    {
        Session? s;bool ownsRetirement;
        lock(_gate){
            if(!_sessions.TryGetValue(id,out s))return true;
            ownsRetirement=!s.Retiring;
            if(ownsRetirement){
                if(onlyIdle&&(s.Busy||Environment.TickCount64-s.Last<=300000))return false;
                s.Retiring=true;
            }
        }
        if(!ownsRetirement)return !waitForCleanup||s.Retired.Task.Wait(TimeSpan.FromSeconds(5));
        try{s.Worker.Dispose();}finally{lock(_gate)_sessions.Remove(id);s.Retired.TrySetResult(true);}
        return true;
    }
    internal bool HasLiveWork(){lock(_gate)return _sessions.Count>0||_extensions.Count>0||_launching.Count>0;}
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
            if (key is "x" or "y" or "from_x" or "from_y" or "to_x" or "to_y" or "count" or "amount" or "duration_ms" or "delay_ms" or "steps")
            {
                if (value is not JsonValue number || !number.TryGetValue<double>(out var n) || !double.IsFinite(n) || n < 0 || n > 32768)
                    throw Fail("cua_invalid_coordinate_or_amount");
            }
            else if (key is "modifiers" or "path" or "keys")
            {
                if (value is not JsonArray a || a.Count > 16 ||
                    a.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out var str) || string.IsNullOrEmpty(str) || Encoding.UTF8.GetByteCount(str) > 256))
                    throw Fail("cua_invalid_array");
            }
            else if (value is not JsonValue text || !text.TryGetValue<string>(out var s) ||
                Encoding.UTF8.GetByteCount(s) > (key is "text" or "value" ? 16000 : 1024))
                throw Fail("cua_invalid_string");
        }
        foreach (var (key,min,max) in new[] { ("count",1,3),("amount",1,50),("duration_ms",0,3000),("delay_ms",0,100),("steps",1,60) })
            if (args[key] is JsonValue n && (!n.TryGetValue<int>(out var integer) || integer < min || integer > max))
                throw Fail("cua_invalid_integer_action_parameter");
        if (args["button"] is JsonValue button && !new[] {"left","right","middle"}.Contains(button.GetValue<string>(),StringComparer.Ordinal))
            throw Fail("cua_invalid_button");
        if (args["action"] is JsonValue action && !new[] {"press","expand"}.Contains(action.GetValue<string>(),StringComparer.Ordinal))
            throw Fail("cua_invalid_element_action");
        if(args["by"] is JsonValue by&&by.GetValue<string>() is not ("line" or "page"))throw Fail("cua_invalid_scroll_unit");
        bool token = args.ContainsKey("element_token"), xy = args.ContainsKey("x") && args.ContainsKey("y");
        if(args.ContainsKey("action")&&(!token||name!="click"||args.ContainsKey("button")||args.ContainsKey("count")))throw Fail("cua_ambiguous_element_action");
        if(name=="type_text"&&args["text"] is JsonValue textValue&&textValue.TryGetValue<string>(out var pacedText)&&
            (pacedText.Contains('\0')||(long)pacedText.Length*(args["delay_ms"]?.GetValue<int>()??0)>3000))throw Fail("cua_text_pacing_budget_exceeded");
        if(xy&&!args.ContainsKey("capture_id"))throw Fail("cua_coordinate_capture_required");
        if (token && (args.ContainsKey("x") || args.ContainsKey("y") || args.ContainsKey("capture_id") || args.ContainsKey("count")) ||
            args.ContainsKey("x") != args.ContainsKey("y")) throw Fail("cua_ambiguous_action_target");
        bool valid = name switch
        {
            "click" => token != xy && (token ? !args.ContainsKey("x") && !args.ContainsKey("y") : args.ContainsKey("capture_id")),
            "type_text" => token != xy && args.ContainsKey("text"),
            "set_value" => token && args.ContainsKey("value"),
            "press_key" => args["key"] is JsonValue key && ValidKey(key.GetValue<string>()) && ValidModifiers(args["modifiers"] as JsonArray ?? new()),
            "hotkey" => args["keys"] is JsonArray {Count:>=2 and <=4} keys && ValidModifiers(new JsonArray(keys.Take(keys.Count-1).Select(k=>k?.DeepClone()).ToArray())) && keys[^1] is JsonValue last && ValidKey(last.GetValue<string>()),
            "scroll" => args["direction"] is JsonValue dir && new[] {"up","down","left","right"}.Contains(dir.GetValue<string>(),StringComparer.Ordinal) && token != xy,
            "drag" => new[] {"from_x","from_y","to_x","to_y","capture_id"}.All(args.ContainsKey),
            "invoke_menu" => args["path"] is JsonArray { Count: > 0 },
            _ => false
        };
        if (!valid) throw Fail("cua_action_requires_exact_snapshot_target_and_parameters");
        if(name is "press_key" or "hotkey"){
            var mods=name=="hotkey"?((JsonArray)args["keys"]!).Take(((JsonArray)args["keys"]!).Count-1).Select(x=>x!.GetValue<string>()):((JsonArray?)args["modifiers"]??new()).Select(x=>x!.GetValue<string>());
            var key=name=="hotkey"?((JsonArray)args["keys"]!)[^1]!.GetValue<string>():args["key"]!.GetValue<string>();
            if(mods.Contains("alt")&&key is "tab" or "escape" or "f4" || mods.Contains("ctrl")&&key=="escape")throw Fail("cua_global_shortcut_not_allowed");
        }
    }

    private static bool ValidKey(string key)=>Regex.IsMatch(key,"\\A(return|enter|tab|escape|up|down|left|right|space|delete|backspace|home|end|pageup|pagedown|f([1-9]|1[0-2])|[a-zA-Z0-9])\\z");
    private static bool ValidModifiers(JsonArray mods){
        var names=mods.Select(m=>m?.GetValue<string>()??"").ToArray();
        return names.Length<=3&&names.Distinct(StringComparer.Ordinal).Count()==names.Length&&names.All(m=>m is "ctrl" or "shift" or "alt");
    }
    internal static JsonObject NativeActionEvidence(JsonObject structured)
    {
        var result=new JsonObject();
        foreach(var key in new[]{"effect","verified","path","verify","code","characters","cell_value"})
            if(structured[key] is JsonValue v&&(v.TryGetValue<string>(out var text)?Encoding.UTF8.GetByteCount(text)<=1024&&(key=="cell_value"||Regex.IsMatch(text,@"\A[A-Za-z0-9_.-]{1,128}\z")):v.TryGetValue<bool>(out _)||v.TryGetValue<double>(out _)))
                result[key]=v.DeepClone();
        return result;
    }
    internal static string? RecoverableNativeRefusal(JsonObject structured)
    {
        if(structured.ContainsKey("foreground_step")||structured["code"] is not JsonValue c||!c.TryGetValue<string>(out var code)||
            structured["effect"] is not JsonValue e||!e.TryGetValue<string>(out var effect))return null;
        if((code is "background_unavailable" or "background_occluded" or "background_uipi_blocked")&&effect=="refused")return code;
        return code=="option_not_found"&&effect=="none"?code:null;
    }
    internal static JsonObject ObservationParameters(JsonObject args)
    {
        foreach(var key in new[]{"include_screenshot","include_accessibility_tree"})
            if(args.ContainsKey(key)&&(args[key] is not JsonValue b||!b.TryGetValue<bool>(out _)))throw Fail("cua_invalid_observe");
        bool image=args["include_screenshot"]?.GetValue<bool>()??true,tree=args["include_accessibility_tree"]?.GetValue<bool>()??true;
        if(!image&&!tree)throw Fail("cua_observation_requires_image_or_tree");
        if(!tree&&new[]{"query","max_elements","max_depth","timeout_ms"}.Any(args.ContainsKey))throw Fail("cua_tree_options_require_accessibility_tree");
        var output=new JsonObject{["include_screenshot"]=image,["include_accessibility_tree"]=tree,["tree_format"]="both"};
        if(args.ContainsKey("query")){
            if(args["query"] is not JsonValue v||!v.TryGetValue<string>(out var q)||string.IsNullOrEmpty(q)||Encoding.UTF8.GetByteCount(q)>512)throw Fail("cua_invalid_observation_query");
            output["query"]=q;
        }
        foreach(var (key,min,max,defaultValue) in new[]{("max_elements",1,1500,250),("max_depth",1,50,25),("max_image_dimension",320,1920,1600),("timeout_ms",100,4000,1000)}){
            int n=defaultValue;
            if(args.ContainsKey(key)&&(args[key] is not JsonValue v||!v.TryGetValue<int>(out n)||n<min||n>max))throw Fail("cua_invalid_observation_budget");
            output[key]=n;
        }
        return output;
    }
    internal static JsonObject? PostObservationParameters(JsonNode? raw)
    {
        if(raw is null)return null;
        if(raw is not JsonObject source||source.Any(kv=>kv.Key is not ("include_screenshot" or "include_accessibility_tree" or "query" or "max_elements" or "max_depth" or "max_image_dimension" or "timeout_ms")))
            throw Fail("cua_invalid_post_observe");
        var options=(JsonObject)source.DeepClone();
        if(!options.ContainsKey("include_screenshot"))options["include_screenshot"]=false;
        if(options.ContainsKey("include_accessibility_tree")&&(options["include_accessibility_tree"] is not JsonValue tree||!tree.TryGetValue<bool>(out _)))throw Fail("cua_invalid_observe");
        if(options["include_accessibility_tree"]?.GetValue<bool>()!=false){
            if(!options.ContainsKey("max_elements"))options["max_elements"]=150;
            if(!options.ContainsKey("timeout_ms"))options["timeout_ms"]=750;
        }
        if(!options.ContainsKey("max_image_dimension"))options["max_image_dimension"]=1280;
        return ObservationParameters(options);
    }
    internal static (JsonArray,JsonObject) ObservationContent(JsonObject reply)
    {
        if(reply["isError"]?.GetValue<bool>()==true||reply["content"] is not JsonArray {Count:>0} blocks||
            reply["structuredContent"] is not JsonObject structured||structured.ContainsKey("foreground_step"))
            throw Fail("cua_post_observation_unavailable");
        var safe=new JsonArray();
        foreach(var item in blocks){
            if(item is not JsonObject block)continue;
            if(block["type"]?.GetValue<string>()=="text"&&block["text"] is JsonValue t&&t.TryGetValue<string>(out var text)&&Encoding.UTF8.GetByteCount(text)<=2_000_000)
                safe.Add(new JsonObject{["type"]="text",["text"]=text});
            else if(block["type"]?.GetValue<string>()=="image"&&block["mimeType"]?.GetValue<string>() is "image/png" or "image/jpeg"&&
                block["data"] is JsonValue d&&d.TryGetValue<string>(out var data)&&data.Length<=8_000_000){
                _=Convert.FromBase64String(data);safe.Add(new JsonObject{["type"]="image",["mimeType"]=block["mimeType"]!.DeepClone(),["data"]=data});
            }
        }
        if(safe.Count==0)throw Fail("cua_post_observation_unavailable");
        var metadata=(JsonObject)structured.DeepClone();
        foreach(var key in new[]{"screenshot","screenshot_base64","image","image_data","screenshot_file_path","escalation"})metadata.Remove(key);
        if(Encoding.UTF8.GetByteCount(metadata.ToJsonString())>2_000_000)throw Fail("cua_driver_metadata_too_large");
        return(safe,metadata);
    }
    internal static JsonArray VerifyPredicates(JsonNode? raw)
    {
        if (raw is not JsonArray predicates || predicates.Count is < 1 or > 8)
            throw Fail("cua_invalid_verify_predicates");
        foreach (var node in predicates)
        {
            if(node is JsonObject w&&w.Count==1&&w["window"] is JsonObject window){
                if(window.Count==0||window.Any(kv=>kv.Key is not ("exists" or "bounds")))throw Fail("cua_invalid_window_predicate");
                if(window.ContainsKey("exists")&&(window["exists"] is not JsonValue x||!x.TryGetValue<bool>(out var exists)||!exists))throw Fail("cua_invalid_window_exists");
                if(window.ContainsKey("bounds")){
                    if(window["bounds"] is not JsonObject bounds||!new[]{"x","y","width","height"}.All(bounds.ContainsKey))throw Fail("cua_invalid_window_bounds");
                    foreach(var (k,rawBound) in bounds){
                        if(k is not ("x" or "y" or "width" or "height" or "tolerance_px")||rawBound is not JsonValue num||!num.TryGetValue<double>(out var d)||!double.IsFinite(d)||
                            d<(k=="tolerance_px"?0:k is "width" or "height"?1:-32768)||d>(k=="tolerance_px"?100:32768))throw Fail("cua_invalid_window_bounds");
                    }
                }
                continue;
            }
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
        lock(_gate)if(_stopped||e.Retiring||!e.Worker.IsAlive||_revoked.Contains(e.Owner)||!_extensions.TryGetValue(e.Id,out var live)||!ReferenceEquals(live,e))return false;
        try{return (e.Profile.kind!="guest"||(e.GuestEpoch is not null&&GuestEpoch()==e.GuestEpoch))&&LoadPolicy().Item2.AsSpan().SequenceEqual(e.PolicyBytes);}catch{return false;}
    }
    private bool RetireExtension(string id,bool onlyIdle=false,bool waitForCleanup=false)
    {
        ExtensionSession? e;bool ownsRetirement;
        lock(_gate){
            if(!_extensions.TryGetValue(id,out e))return true;
            ownsRetirement=!e.Retiring;
            if(ownsRetirement){
                if(onlyIdle&&(e.Busy||Environment.TickCount64-e.Last<=300000))return false;
                e.Retiring=true;
            }
        }
        if(!ownsRetirement)return !waitForCleanup||e.Retired.Task.Wait(TimeSpan.FromSeconds(5));
        try{e.Worker.Dispose();}
        finally{lock(_gate)_extensions.Remove(id);e.Retired.TrySetResult(true);}
        return true;
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
            if(!RetireExtension(id,waitForCleanup:true))throw Fail("extension_cleanup_pending");
            return Output(new JsonObject{["closed"]=true});
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
            if(!_extensions.TryGetValue(sessionId,out var e)||e.Owner!=owner||e.Busy||e.Retiring||Environment.TickCount64-e.Last>300000)
                throw Fail("unknown_or_busy_extension_session");
            session=e;session.Busy=true;
        }
        try
        {
            var browserOps=new[]{"observe","navigate","click","fill","press","select","scroll","verify","wait_for","back","forward","hover","drag","tab_list","tab_open","tab_select","tab_close","dialog_prepare","upload","download"};
            var guestOps=new[]{"cua_capabilities","cua_list_apps","cua_list_windows","cua_launch_app","cua_create_session","cua_observe","cua_act","cua_verify","cua_stop_session","cua_receipt_status"};
            if(!ExtensionAllowed(session)){RetireExtension(sessionId,waitForCleanup:true);throw Fail("extension_operation_not_allowed");}
            if(!(session.Profile.kind=="browser"?browserOps:guestOps).Contains(op,StringComparer.Ordinal)||cancellation.IsCancellationRequested)
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
            return Output(new JsonObject { ["enabled"] = enabled, ["background_only"] = true, ["driver"] = "cua-driver", ["driver_version"] = "0.61.0", ["driver_mode"] = "bounded_direct",["foreground_continuity_guard"]=true,["same_process_window_transfer"]=true,
                ["identity"] = _relayOnly?"dedicated_relay_credential":_guestMode?"dedicated_guest_connection":owner == SharedOwner ? "shared_connection_not_chat_identity" : "verified_oauth_client", ["conversation_identity_verified"]=false,["relay_only"]=_relayOnly,["host_tools_allowed"]=!_guestMode&&!_relayOnly,["requires_verified_client"]=requiresVerified,
                ["operations"] = new JsonArray(Actions.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["native_element_actions"]=new JsonArray("press","expand"),["advertised_custom_element_actions"]=false,["paced_text_budget_ms"]=3000,["native_worker_max_age_seconds"]=6900,["post_action_observation"]=true,
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
            if(!Retire(id,waitForCleanup:true))throw Fail("cua_cleanup_pending");
            return Output(new JsonObject { ["stopped"] = true });
        }
        var (policy,bytes) = LoadPolicy();
        if(!_guestMode&&policy.requireVerifiedClient==true&&owner==SharedOwner)throw Fail("cua_verified_client_required");
        if (!policy.grants.Any(g => g.clients.Contains(owner, StringComparer.Ordinal))) throw Fail("cua_client_not_approved");
        if(name=="cua_launch_app")
        {
            var application=args["application"]?.GetValue<string>() ?? "";
            if(!AllowedApplication(application) || !policy.PermitsLaunch(application,owner)) throw Fail("cua_launch_not_approved");
            var focusGuard=_guestMode?null:new CuaForegroundContinuity(()=>GetForegroundWindow().ToInt64());
            if(!_guestMode){
                if(focusGuard?.Permits()!=true)throw Fail("cua_target_in_use_by_user");
                _=GetWindowThreadProcessId(GetForegroundWindow(),out uint foregroundPid);
                try{
                    using var foreground=Process.GetProcessById((int)foregroundPid);
                    if(foreground.MainModule?.FileName is not string foregroundExe||foregroundExe.Equals(application,StringComparison.OrdinalIgnoreCase))
                        throw Fail("cua_target_in_use_by_user");
                }catch{throw Fail("cua_target_in_use_by_user_or_unknown_foreground");}
            }
            var request=RequestId(args["request_id"]); var receipt=Ledger(); var driver=CheckedDriver(policy);
            lock(_gate)
            {
                if(_stopped || _revoked.Contains(owner) || _launching.Contains(application) || _sessions.Count+_extensions.Count+_launching.Count>=8 ||
                    _sessions.Values.Any(s=>s.Target.Application.Equals(application,StringComparison.OrdinalIgnoreCase)))
                    throw Fail("cua_target_busy_or_capacity");
                _launching.Add(application);
            }
            bool reserved=false;
            try
            {
                ReserveReceipt(receipt,request,owner);reserved=true;
                using var worker=new Worker(driver,application,true);
                lock(_gate) _launchWorkers[application]=(owner,worker);
                bool Allowed()
                {
                    lock(_gate) if(_stopped || _revoked.Contains(owner))return false;
                    try {return !cancellation.IsCancellationRequested && (focusGuard?.Permits()??true) && LoadPolicy().Item2.AsSpan().SequenceEqual(bytes);}
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
            catch { if(reserved)try{FinishReceipt(receipt,request,"interrupted");}catch{} throw; }
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
            if(!args.ContainsKey("previous_session_id"))Sweep();
            lock (_gate)
            {
                if(_stopped||_revoked.Contains(owner)||cancellation.IsCancellationRequested||
                    !LoadPolicy().Item2.AsSpan().SequenceEqual(bytes)||GetTarget(pid,window,policy,owner)!=target)
                    throw Fail("cua_target_unavailable_or_revoked");
                if(args.ContainsKey("previous_session_id")){
                    if(args["previous_session_id"] is not JsonValue v||!v.TryGetValue<string>(out var prior)||
                        !_sessions.TryGetValue(prior,out var old)||old.Owner!=owner||old.Busy||old.Retiring||!old.Worker.IsAlive||old.Worker.AgeSeconds>=6900||
                        !old.PolicyBytes.AsSpan().SequenceEqual(bytes)||Environment.TickCount64-old.Last>300000||
                        !old.Target.SameProcess(target)||old.Target.Window==window)
                        throw Fail("cua_window_transfer_not_allowed");
                    var replacement=new Session(owner,target,bytes,old.Worker);
                    _sessions.Remove(prior);_sessions.Add(replacement.Id,replacement);
                    old.Retired.TrySetResult(true);
                    return Output(new JsonObject{["session_id"]=replacement.Id,["pid"]=pid,["window_id"]=window,
                        ["transferred"]=true,["observation_required"]=true,["background_only"]=true,["idle_timeout_seconds"]=300});
                }
                if (_sessions.Count + _extensions.Count + _launching.Count >= 8 || _launching.Contains(target.Application) || _sessions.Values.Any(s => s.Target.Pid == pid))
                    throw Fail("cua_target_busy_or_capacity");
                var worker = new Worker(CheckedDriver(policy),target.Application);
                var created = new Session(owner,target,bytes,worker);
                _sessions.Add(created.Id,created);
                return Output(new JsonObject { ["session_id"] = created.Id, ["pid"] = pid, ["window_id"] = window,["observation_required"]=true,
                    ["background_only"] = true, ["idle_timeout_seconds"] = 300 });
            }
        }
        var sessionId = args["session_id"]?.GetValue<string>() ?? throw Fail("cua_invalid_session");
        Session s;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId,out var selected) || selected.Owner != owner || selected.Busy || selected.Retiring || _stopped ||
                Environment.TickCount64 - selected.Last > 300000) throw Fail("cua_unknown_unauthorized_or_busy_session");
            s = selected; s.Busy = true;
        }
        try
        {
            bool mutation = name == "cua_act";
            var focusGuard=mutation&&!_guestMode?new CuaForegroundContinuity(()=>GetForegroundWindow().ToInt64()):null;
            if (!StillAllowed(s,mutation) || cancellation.IsCancellationRequested || focusGuard?.Permits()==false) { s.Snapshot=null; throw Fail("cua_target_unavailable_or_user_active"); }
            var driverArgs = new JsonObject { ["pid"] = s.Target.Pid, ["window_id"] = s.Target.Window };
            string driverTool;
            (CommandReceiptLedger Ledger,string Id)? actionReceipt=null;
            Snapshot? coordinateSnapshot=null;WindowGeometry? observedGeometry=null;
            var priorWindows=new HashSet<long>();
            if(mutation&&args.ContainsKey("post_observe")&&args["post_observe"] is null)throw Fail("cua_invalid_post_observe");
            var postOptions=mutation?PostObservationParameters(args["post_observe"]):null;
            var combinedDeadline=Environment.TickCount64+9000;
            if (mutation)
            {
                if (s.Snapshot is not { } snapshot || args["operation"] is not JsonValue op || !op.TryGetValue<string>(out var operation) ||
                    args["parameters"] is not JsonObject parameters) throw Fail("cua_observe_before_action");
                snapshot.ValidateObservationId(args["observation_id"]);
                ValidateAction(operation,parameters);
                var normalized=snapshot.ValidatedControlArguments(operation,parameters)??throw Fail("cua_control_disabled_or_action_not_advertised");
                if(operation!="set_window_frame") snapshot.Validate(parameters); else snapshot.Validate(new JsonObject());
                if(parameters.ContainsKey("capture_id")){
                    try{snapshot.ValidatePixelGeometry(ReadGeometry(s.Target));}catch{s.Snapshot=null;throw;}coordinateSnapshot=snapshot;
                }
                var request=RequestId(args["request_id"]);var receipt=Ledger();
                ReserveReceipt(receipt,request,owner);
                actionReceipt=(receipt,request);
                priorWindows=Windows(s.Target.Pid).Select(w=>w!["window_id"]!.GetValue<long>()).ToHashSet();
                foreach (var (key,value) in normalized)
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
                if(args.ContainsKey("stable_samples")){
                    if(args["stable_samples"] is not JsonValue sample||!sample.TryGetValue<int>(out var n)||n<1||n>5)throw Fail("cua_invalid_stable_samples");
                    driverArgs["stable_samples"]=n;
                }
                driverArgs["include_screenshot"] = false;
                s.Snapshot = null;
            }
            else
            {
                if (name != "cua_observe" || (args.ContainsKey("include_screenshot") &&
                    (args["include_screenshot"] is not JsonValue b || !b.TryGetValue<bool>(out _)))) throw Fail("cua_invalid_observe");
                driverTool = "get_window_state";
                foreach(var (key,value) in ObservationParameters(args))driverArgs[key]=value?.DeepClone();
                observedGeometry=ReadGeometry(s.Target);
                s.Snapshot = null;
            }
            try
            {
                var result = await s.Worker.CallAsync(driverTool, driverArgs, () => (focusGuard?.Permits()??true)&&StillAllowed(s,mutation), cancellation,()=>coordinateSnapshot?.ValidatePixelGeometry(ReadGeometry(s.Target))).ConfigureAwait(false);
                if (!StillAllowed(s,mutation) || cancellation.IsCancellationRequested || focusGuard?.Permits()==false) throw Fail("cua_action_outcome_unknown: foreground changed or target became unavailable; observe actual state before any new action");
                if(result["isError"]?.GetValue<bool>()==true){
                    var failure=result["structuredContent"] as JsonObject??new();
                    if(mutation&&RecoverableNativeRefusal(failure) is string code&&actionReceipt is {} refused){
                        FinishReceipt(refused.Ledger,refused.Id,"launch_failed");
                        var outcome=new JsonObject{["session_id"]=sessionId,["operation"]=driverTool,["action_outcome"]="refused",["action_dispatched"]=false,["verified"]=false,["code"]=code,["timing_ms"]=new JsonObject{["worker_call_ms"]=s.Worker.LastCallMs,["handshake_ms"]=s.Worker.LastHandshakeMs},
                            ["observation_required"]=true,["background_only"]=true,["foreground_fallback"]=false,["verification_required"]=false};
                        if(code=="option_not_found"&&failure["options"] is JsonArray options)
                            outcome["options"]=new JsonArray(options.Take(100).OfType<JsonValue>().Where(v=>v.TryGetValue<string>(out _))
                                .Select(v=>{var label=v.GetValue<string>();return (JsonNode?)JsonValue.Create(label[..Math.Min(256,label.Length)]);}).ToArray());
                        return Output(outcome);
                    }
                    throw Fail("cua_driver_rejected: inspect actual window state; no foreground fallback or automatic retry");
                }
                if (result["content"] is not JsonArray {Count:>0} blocks) throw Fail("cua_driver_invalid_content");
                var safe = new JsonArray();
                foreach (var item in blocks)
                {
                    if (item is not JsonObject block) continue;
                    if (block["type"]?.GetValue<string>() == "text" && block["text"] is JsonValue t && t.TryGetValue<string>(out var text))
                    {
                        if (Encoding.UTF8.GetByteCount(text) > 2_000_000) throw Fail("cua_driver_text_too_large");
                        if(!mutation)safe.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    }
                    else if (!mutation && block["type"]?.GetValue<string>() == "image" &&
                        block["mimeType"]?.GetValue<string>() is "image/png" or "image/jpeg" &&
                        block["data"] is JsonValue d && d.TryGetValue<string>(out var data) && data.Length <= 8_000_000)
                    {
                        _ = Convert.FromBase64String(data);
                        safe.Add(new JsonObject { ["type"] = "image", ["mimeType"] = block["mimeType"]!.DeepClone(), ["data"] = data });
                    }
                }
                if (!mutation&&safe.Count == 0) throw Fail("cua_driver_invalid_content");
                var structured = result["structuredContent"] as JsonObject ?? new JsonObject();
                if (structured.ContainsKey("foreground_step")) throw Fail("cua_driver_reported_foreground_step");
                if(name=="cua_observe"){
                    var geometry=observedGeometry==ReadGeometry(s.Target)?observedGeometry:null;
                    var observation=new Snapshot(structured,safe,driverArgs["include_screenshot"]?.GetValue<bool>()==true?geometry:null,driverArgs["include_accessibility_tree"]?.GetValue<bool>()==true);
                    s.Snapshot=observation.Actionable?observation:null;
                }
                var metadata=mutation?NativeActionEvidence(structured):(JsonObject)structured.DeepClone();
                foreach (var key in new[] {"screenshot","screenshot_base64","image","image_data","screenshot_file_path","escalation"}) metadata.Remove(key);
                if (Encoding.UTF8.GetByteCount(metadata.ToJsonString()) > 2_000_000) throw Fail("cua_driver_metadata_too_large");
                if(actionReceipt is { } ar){FinishReceipt(ar.Ledger,ar.Id,"exited");actionReceipt=null;}
                var header=new JsonObject { ["session_id"] = sessionId, ["operation"] = driverTool,
                    ["background_only"] = true, ["verification_required"] = mutation,["timing_ms"]=new JsonObject{["worker_call_ms"]=s.Worker.LastCallMs,["handshake_ms"]=s.Worker.LastHandshakeMs},["worker_age_seconds"]=s.Worker.AgeSeconds };
                if(mutation){
                    var now=Windows(s.Target.Pid).Select(w=>w!["window_id"]!.GetValue<long>()).ToHashSet();
                    header["new_window_ids"]=new JsonArray(now.Except(priorWindows).Order().Select(id=>(JsonNode?)JsonValue.Create(id)).ToArray());
                    header["closed_window_ids"]=new JsonArray(priorWindows.Except(now).Order().Select(id=>(JsonNode?)JsonValue.Create(id)).ToArray());
                    header["action_outcome"]="dispatched_unverified";header["verified"]=false;header["foreground_fallback"]=false;header["observation_required"]=true;header["driver_evidence"]=metadata.DeepClone();
                }
                if(name=="cua_observe"){
                    header["pixel_actions_available"]=s.Snapshot?.PixelGrounded??false;
                    header["element_actions_available"]=s.Snapshot?.Tokens.Count>0;
                    header["observation_only"]=s.Snapshot is null;
                    if(s.Snapshot is {} observation)header["observation_id"]=observation.ObservationId;
                }
                JsonObject? postMetadata=null;
                if(postOptions is not null){
                    header["post_observe_status"]="not_started";
                    var remaining=combinedDeadline-Environment.TickCount64;
                    if(remaining>=1250&&!cancellation.IsCancellationRequested&&focusGuard?.Permits()!=false&&StillAllowed(s,false)){
                        try{
                            postOptions["pid"]=s.Target.Pid;postOptions["window_id"]=s.Target.Window;
                            var geometryBefore=ReadGeometry(s.Target);
                            var reply=await s.Worker.CallAsync("get_window_state",postOptions,()=> (focusGuard?.Permits()??true)&&StillAllowed(s,false),
                                cancellation,timeoutMs:(int)remaining).ConfigureAwait(false);
                            if(cancellation.IsCancellationRequested||focusGuard?.Permits()==false||!StillAllowed(s,false))throw Fail("cua_post_observation_revoked");
                            var (content,details)=ObservationContent(reply);
                            var geometry=geometryBefore==ReadGeometry(s.Target)?geometryBefore:null;
                            var next=new Snapshot(details,content,postOptions["include_screenshot"]?.GetValue<bool>()==true?geometry:null,postOptions["include_accessibility_tree"]?.GetValue<bool>()==true);
                            s.Snapshot=next.Actionable?next:null;
                            header["post_observe_status"]="observed";
                            header["post_observe_timing_ms"]=new JsonObject{["worker_call_ms"]=s.Worker.LastCallMs,["handshake_ms"]=s.Worker.LastHandshakeMs};
                            header["observation_only"]=s.Snapshot is null;
                            header["pixel_actions_available"]=s.Snapshot?.PixelGrounded??false;
                            header["element_actions_available"]=s.Snapshot?.Tokens.Count>0;
                            if(s.Snapshot is {} observation){header["observation_id"]=observation.ObservationId;header["observation_required"]=false;}
                            postMetadata=details;
                            foreach(var block in content)safe.Add(block?.DeepClone());
                        }catch{
                            s.Snapshot=null;Retire(sessionId);
                            header["post_observe_status"]="unavailable_session_retired";
                        }
                    }else header["post_observe_status"]="skipped_budget_or_target_unavailable";
                }
                var summary=(JsonObject)header.DeepClone();summary["driver_result"]=metadata;
                if(postMetadata is not null)summary["post_observation"]=postMetadata;
                safe.Insert(0,new JsonObject{["type"]="text",["text"]=header.ToJsonString()});
                return new ToolCallOutput(safe,summary);
            }
            catch { if(actionReceipt is {} ar)try{FinishReceipt(ar.Ledger,ar.Id,"interrupted");}catch{} Retire(sessionId); throw; }
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
        private readonly Dictionary<string,JsonObject> _toolSchemas=new(StringComparer.Ordinal);
        private bool _initialized;
        private readonly long _started=Environment.TickCount64;
        internal long AgeSeconds=>(Environment.TickCount64-_started)/1000;
        internal long LastCallMs{get;private set;}
        internal long LastHandshakeMs{get;private set;}
        private readonly ManualResetEventSlim _disposed=new(false);
        private readonly string _manifestDirectory;
        private readonly WindowsJob? _job;
        private readonly bool _isExtension;
        internal Worker(string path,string application,bool launch=false,ExtensionRuntime? runtime=null,ExtensionProfile? profile=null)
        {
            _isExtension=runtime is not null;
            _manifestDirectory = Path.Combine(Path.GetTempPath(),"ctcmcp-cua-"+Guid.NewGuid().ToString("N"));
            CommandReceiptLedger.EnsurePrivateFolder(_manifestDirectory);
            var manifestPath = Path.Combine(_manifestDirectory,"capability.json");
            Process? started=null;
            try
            {
                var manifest = new {
                    version = 3, expires_after = "2h", idle_timeout = "10m",
                    allow = new { tools = new[] {"check_permissions","get_window_state","click","type_text","press_key","hotkey","scroll","drag","set_value","invoke_menu","verify_state","set_window_frame"}.Concat(launch?new[]{"launch_app"}:Array.Empty<string>()).ToArray() },
                    resources = new { apps = new[] { new { executable = application, launch, windows = "all" } },
                        desktop = new { display = false } }
                };
                File.WriteAllText(manifestPath,profile is null?JsonSerializer.Serialize(manifest):JsonSerializer.Serialize(profile));
                var info = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                info.ArgumentList.Add(runtime?.worker??"mcp");
                if(runtime is null)info.ArgumentList.Add("--direct");
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
                _process = started = Process.Start(info) ?? throw Fail("cua_driver_unavailable");
                // Attach desktop workers to kill-on-close supervision. One-shot
                // launch workers must not kill the launched user application.
                if (!launch) _job=WindowsJob.CreateAndAssign(_process);
            }
            catch
            {
                if(started is not null){
                    try{if(!started.HasExited)started.Kill(entireProcessTree:true);started.WaitForExit(2000);}catch{}
                    started.Dispose();
                }
                try { Directory.Delete(_manifestDirectory,true); } catch { }
                throw;
            }
            _input = _process.StandardInput.BaseStream;
            _output = _process.StandardOutput.BaseStream;
            // Drain stderr without retaining private desktop content.
            _ = _process.StandardError.BaseStream.CopyToAsync(Stream.Null).ContinueWith(_ => {}, TaskScheduler.Default);
        }
        internal bool IsAlive {get{try{return Volatile.Read(ref _closed)==0&&!_process.HasExited;}catch{return false;}}}
        public void Dispose()
        {
            if(Interlocked.Exchange(ref _closed,1)!=0){_disposed.Wait();return;}
            try{
            _stop.Cancel();
            if(_isExtension){try{_input.Close();_process.WaitForExit(2000);}catch{}}
            try { _job?.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.Kill(entireProcessTree:_job is not null); } catch { }
            try { _process.WaitForExit(2000); } catch { }
            try { _process.Dispose(); } catch { }
            try { Directory.Delete(_manifestDirectory,true); } catch { }
            }finally{_disposed.Set();}
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
        internal static JsonObject ValidatedDriverArguments(string tool,JsonObject args,JsonObject schema)
        {
            if(schema["properties"] is not JsonObject properties)throw Fail("cua_driver_schema_missing");
            // The Windows implementation may omit the optional revision check;
            // the gateway still requires its own fresh, one-use snapshot. Never
            // strip pid/window, delivery_mode, scope or requested action data.
            var result=(JsonObject)args.DeepClone();
            if(!properties.ContainsKey("ui_tree_revision"))result.Remove("ui_tree_revision");
            if(result.Any(kv=>!properties.ContainsKey(kv.Key))||
                schema["required"] is JsonArray required&&required.Any(k=>!result.ContainsKey(k!.GetValue<string>())))
                throw Fail("cua_driver_contract_mismatch: "+tool);
            return result;
        }
        internal async Task<JsonObject> CallAsync(string tool, JsonObject args, Func<bool> allowed, CancellationToken cancellation,Action? beforeDispatch=null,int timeoutMs=10000)
        {
            var started=Environment.TickCount64;
            LastHandshakeMs=0;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation,_stop.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs,0,10000)));
            using var abort = deadline.Token.Register(() => { try { if (!_process.HasExited) _process.Kill(entireProcessTree:_job is not null); } catch { } });
            int checking=0;
            using var watchdog=new Timer(_=>{
                if(Interlocked.Exchange(ref checking,1)!=0)return;
                try{if(!allowed())deadline.Cancel();}
                catch{try{deadline.Cancel();}catch{}}
                finally{Volatile.Write(ref checking,0);}
            },null,0,100);
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
                    if(!_isExtension){
                        var listed=await RpcAsync("tools/list",new(),deadline.Token).ConfigureAwait(false);
                        if(listed["tools"] is not JsonArray definitions||definitions.Count is <1 or >256||listed["nextCursor"] is not null)throw Fail("cua_driver_schema_missing");
                        foreach(var item in definitions){
                            if(item?["name"] is not JsonValue name||!name.TryGetValue<string>(out var id)||item["inputSchema"] is not JsonObject schema||!_toolSchemas.TryAdd(id,schema))throw Fail("cua_driver_schema_invalid");
                        }
                    }
                    _initialized = true;LastHandshakeMs=Environment.TickCount64-started;
                }
                var checkedArgs=args;
                if(!_isExtension){
                    if(!_toolSchemas.TryGetValue(tool,out var schema))throw Fail("cua_driver_tool_not_advertised: "+tool);
                    checkedArgs=ValidatedDriverArguments(tool,args,schema);
                }
                if(!allowed())throw Fail("cua_session_revoked");
                deadline.Token.ThrowIfCancellationRequested();
                beforeDispatch?.Invoke();
                return await RpcAsync("tools/call",new JsonObject { ["name"] = tool, ["arguments"] = checkedArgs },deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw Fail("cua_driver_timeout_or_revoked: action outcome may be unknown; observe again in a new session"); }
            finally{LastCallMs=Environment.TickCount64-started;}
        }
    }
}
