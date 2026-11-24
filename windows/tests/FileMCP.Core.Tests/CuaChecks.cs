using System.Diagnostics;
using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task TestCuaAsync(string root)
    {
        using var service = new CuaService(Path.Combine(root, "missing-cua-policy.json"));
        static JsonObject J(string value) => JsonNode.Parse(value)!.AsObject();
        async Task RefuseAsync(string label, Func<Task> action)
        {
            bool refused = false;
            try { await action(); } catch (FileMcpException) { refused = true; }
            Assert(refused, "CUA refuses " + label);
        }
        void Refuse(string label, Action action)
        {
            bool refused = false;
            try { action(); } catch (FileMcpException) { refused = true; }
            Assert(refused, "CUA refuses " + label);
        }
        var status = await service.CallAsync("cua_capabilities", new(), null, default);
        Assert(status.StructuredContent["enabled"]!.GetValue<bool>() == false, "CUA default disabled");
        Assert(status.StructuredContent["identity"]!.GetValue<string>() == "shared_connection_not_chat_identity", "CUA shared connection not chat identity");
        Assert(CuaService.Definitions().Count == 15, "CUA tool parity");
        await RefuseAsync("disabled app enumeration", () => service.CallAsync("cua_list_apps", new(), null, default));
        await RefuseAsync("remote policy mutation", () => service.CallAsync("cua_capabilities", J("""{"enabled":true}"""), null, default));
        await RefuseAsync("unknown session", () => service.CallAsync("cua_stop_session", J("""{"session_id":"fake"}"""), null, default));
        var actionID=new string('r',48);
        Assert(CuaService.RequestId(JsonValue.Create(actionID))==actionID,"CUA request identity");
        Refuse("short request ID",()=>CuaService.RequestId(JsonValue.Create("retry")));
        CuaService.ValidateAction("set_window_frame",J("""{"x":10,"y":20,"width":640,"height":480}"""));
        Refuse("invalid window geometry",()=>CuaService.ValidateAction("set_window_frame",J("""{"x":0,"y":0,"width":0,"height":480}""")));
        var receiptPath=Path.Combine(root,"test-cua-receipts.json");
        var firstLedger=new CommandReceiptLedger(receiptPath);
        firstLedger.Reserve("cua",actionID,"client-one");
        var reloaded=new CommandReceiptLedger(receiptPath);
        Refuse("durable replay",()=>reloaded.Reserve("cua",actionID,"client-one"));
        Refuse("foreign receipt",()=>reloaded.OwnReceipt("cua",actionID,"client-two"));
        CuaService.ValidateAction("click", J("""{"element_token":"s00000001:1"}"""));
        CuaService.ValidateAction("click", J("""{"x":2,"y":3,"capture_id":"cap"}"""));
        foreach (var (operation, json) in new[] {
            ("click", """{"element_token":"s00000001:1","delivery_mode":"foreground"}"""),
            ("click", """{"x":2,"y":3,"capture_id":"cap","scope":"desktop"}"""),
            ("click", """{"element_token":"s00000001:1","pid":1}"""),
            ("click", """{"element_token":"s00000001:1","target":{"kind":"desktop"}}"""),
            ("click", """{"x":2,"y":3}"""),
            ("drag", """{"from_x":2,"from_y":3,"to_x":4,"to_y":5}"""),
            ("run_script", """{"code":"x"}""")
        }) Refuse(json, () => CuaService.ValidateAction(operation, J(json)));
        var png = Convert.ToBase64String(new byte[]{137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,100,0,0,0,80});
        var snap = new CuaService.Snapshot(J("""{"snapshot_id":"s00000001","ui_tree_revision":7,"capture_id":"cap","elements":[{"element_token":"s00000001:1"}]}"""),
            new JsonArray(new JsonObject{["type"]="image",["mimeType"]="image/png",["data"]=png}));
        snap.ValidateObservationId(JsonValue.Create(snap.ObservationId));
        Refuse("foreign observation",()=>snap.ValidateObservationId(JsonValue.Create("other")));
        snap.Validate(J("""{"element_token":"s00000001:1"}"""));
        snap.Validate(J("""{"x":99,"y":79,"capture_id":"cap"}"""));
        Refuse("foreign token", () => snap.Validate(J("""{"element_token":"s00000002:1"}""")));
        Refuse("unknown row", () => snap.Validate(J("""{"element_token":"s00000001:2"}""")));
        Refuse("stale capture", () => snap.Validate(J("""{"x":2,"y":3,"capture_id":"old"}""")));
        Refuse("outside window", () => snap.Validate(J("""{"x":100,"y":0,"capture_id":"cap"}""")));
        var geometry=new CuaService.WindowGeometry(-100,40,700,640,true,false,144);
        var images=new JsonArray(new JsonObject{["type"]="image",["mimeType"]="image/png",["data"]=png});
        var visual=new CuaService.Snapshot(J("""{"capture_id":"cap"}"""),images,geometry,false);
        Assert(visual.Actionable&&visual.PixelGrounded&&!visual.TreeGrounded&&visual.Tokens.Count==0,"visual-only grounding");
        visual.ValidatePixelGeometry(geometry);
        visual.Validate(J("""{"x":5,"y":10,"capture_id":"cap"}"""));
        Refuse("window moved",()=>visual.ValidatePixelGeometry(geometry with{Left=-99}));
        Refuse("window minimized",()=>visual.ValidatePixelGeometry(geometry with{Minimized=true}));
        Refuse("window DPI changed",()=>visual.ValidatePixelGeometry(geometry with{Dpi=96}));
        Refuse("visual mode does not inherit AX",()=>visual.Validate(J("""{"element_token":"s00000001:1"}""")));
        Assert(!new CuaService.Snapshot(new(),images,geometry,false).Actionable,"image without capture is display only");
        Assert(!new CuaService.Snapshot(J("""{"capture_id":"cap","screenshot_width":101}"""),images,geometry,false).Actionable,"mismatched dimensions cannot ground action");
        var treeOnly=new CuaService.Snapshot(J("""{"snapshot_id":"s00000001","elements":[{"element_token":"s00000001:1"}]}"""),new());
        Assert(treeOnly.Actionable&&!treeOnly.PixelGrounded&&treeOnly.TreeGrounded,"accessibility-only grounding");
        Assert(CuaService.ObservationParameters(J("""{"include_accessibility_tree":false}"""))["include_accessibility_tree"]!.GetValue<bool>()==false,"visual-only observation options");
        Refuse("empty observation",()=>CuaService.ObservationParameters(J("""{"include_accessibility_tree":false,"include_screenshot":false}""")));
        Refuse("visual tree query",()=>CuaService.ObservationParameters(J("""{"include_accessibility_tree":false,"query":"button"}""")));
        Refuse("invalid observation bool",()=>CuaService.ObservationParameters(J("""{"include_accessibility_tree":1}""")));
        CuaService.VerifyPredicates(JsonNode.Parse("""[{"element":{"selector":{"role":"button"},"exists":true}}]"""));
        Refuse("unproven absence", () => CuaService.VerifyPredicates(JsonNode.Parse("""[{"element":{"selector":{"role":"button"},"exists":false}}]""")));
        Refuse("nested selector", () => CuaService.VerifyPredicates(JsonNode.Parse("""[{"element":{"selector":{"pid":1},"exists":true}}]""")));
        CuaService.ValidateAction("hotkey",J("""{"keys":["ctrl","s"],"element_token":"s00000001:1"}"""));
        CuaService.ValidateAction("type_text",J("""{"text":"test","element_token":"s00000001:1","delay_ms":25}"""));
        CuaService.ValidateAction("scroll",J("""{"direction":"down","element_token":"s00000001:1","by":"page"}"""));
        Refuse("global shortcut",()=>CuaService.ValidateAction("hotkey",J("""{"keys":["alt","tab"]}""")));
        Refuse("unbound pixel typing",()=>CuaService.ValidateAction("type_text",J("""{"x":1,"y":2,"text":"test"}""")));
        Refuse("invalid key",()=>CuaService.ValidateAction("press_key",J("""{"key":"superkey"}""")));
        var observation=CuaService.ObservationParameters(J("""{"query":"button|menu","max_elements":1000,"max_depth":40,"timeout_ms":3000}"""));
        Assert(observation["max_elements"]!.GetValue<int>()==1000,"bounded query");
        Refuse("unbounded tree",()=>CuaService.ObservationParameters(J("""{"max_elements":100000}""")));
        CuaService.VerifyPredicates(JsonNode.Parse("""[{"window":{"bounds":{"x":-100,"y":20,"width":640,"height":480,"tolerance_px":3}}}]"""));
        Refuse("invalid geometry predicate",()=>CuaService.VerifyPredicates(JsonNode.Parse("""[{"window":{"bounds":{"x":0,"y":0,"width":1,"height":-1}}}]""")));
        var normalized=CuaService.Worker.ValidatedDriverArguments("click",J("""{"pid":1,"window_id":2,"ui_tree_revision":7,"delivery_mode":"background"}"""),
            J("""{"properties":{"pid":{},"window_id":{},"delivery_mode":{}},"required":["pid","window_id"]}"""));
        Assert(!normalized.ContainsKey("ui_tree_revision")&&normalized["delivery_mode"]!.GetValue<string>()=="background","Windows revision omission is deliberate; target and mode retained");
        Refuse("unknown safety field",()=>CuaService.Worker.ValidatedDriverArguments("click",J("""{"pid":1,"delivery_mode":"background"}"""),J("""{"properties":{"pid":{}}}""")));
        var originalTarget=new CuaService.Target(100,1,@"C:\Fixture.exe",1000);
        Assert(originalTarget.SameProcess(originalTarget with {Window=2}),"same-process window transfer");
        Assert(!originalTarget.SameProcess(originalTarget with {Pid=101,Window=2}),"foreign PID transfer refused");
        Assert(!originalTarget.SameProcess(originalTarget with {Launched=1001,Window=2}),"recycled PID refused");
        Assert(!originalTarget.SameProcess(originalTarget with {Application=@"C:\Other.exe",Window=2}),"foreign executable refused");
        Assert(CuaService.RecoverableNativeRefusal(J("""{"code":"background_unavailable","effect":"refused"}"""))=="background_unavailable","typed refusal");
        Assert(CuaService.RecoverableNativeRefusal(J("""{"code":"option_not_found","effect":"none"}"""))=="option_not_found","typed missing option");
        Assert(CuaService.RecoverableNativeRefusal(J("""{"code":"background_occluded","effect":"unverifiable"}""")) is null,"uncertain action not recoverable");
        Assert(CuaService.RecoverableNativeRefusal(J("""{"code":"unknown","effect":"refused"}""")) is null,"unknown code not recoverable");
        Assert(CuaService.RecoverableNativeRefusal(J("""{"code":"background_unavailable","effect":"refused","foreground_step":true}""")) is null,"foreground report not recoverable");
        Assert(CuaService.RecoverableNativeRefusal(J("""{"code":"background_unavailable"}""")) is null,"missing effect not recoverable");
        Assert(CuaService.Definitions().Single(t=>t?["name"]?.GetValue<string>()=="cua_create_session")?["inputSchema"]?["properties"]?["previous_session_id"] is JsonObject,"transfer schema advertised");
        long? foreground=101;
        var continuity=new CuaForegroundContinuity(()=>foreground);
        Assert(continuity.Permits(),"initial foreground continuity");
        foreground=102;Assert(!continuity.Permits(),"foreground changed");
        foreground=101;Assert(!continuity.Permits(),"foreground return does not resume operation");
        var eventLatch=new CuaForegroundContinuity(()=>foreground);
        eventLatch.Invalidate();Assert(!eventLatch.Permits(),"activation event latch");
        foreground=null;Assert(!new CuaForegroundContinuity(()=>foreground).Permits(),"unknown foreground fails closed");
        var exe = Environment.ProcessPath!;
        Assert(!Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase), "CUA fixture requires test apphost");
        using (var worker = new CuaService.Worker(exe,exe))
        {
            var result = await worker.CallAsync("get_window_state",J("""{"pid":1,"window_id":2}"""),()=>true,default);
            Assert(result["structuredContent"]?["echo"]?["window_id"]?.GetValue<int>() == 2, "CUA bounded stdio handshake");
            await RefuseAsync("before-dispatch geometry guard",()=>worker.CallAsync("get_window_state",J("""{"pid":1,"window_id":2}"""),()=>true,default,()=>throw new FileMcpException("stale geometry")));
            await RefuseAsync("unadvertised driver tool",()=>worker.CallAsync("bring_to_front",new(),()=>true,default));
        }
        using (var worker = new CuaService.Worker(exe,exe))
        {
            var watch=Stopwatch.StartNew();
            await RefuseAsync("in-flight revoked call", () => worker.CallAsync("hang",new(),()=>watch.ElapsedMilliseconds<800,default));
            Assert(watch.ElapsedMilliseconds<4000,"CUA revocation latency");
        }
        service.Revoke("oauth-test");
        await RefuseAsync("revoked owner",()=>service.CallAsync("cua_capabilities",new(),"oauth-test",default));
        service.Stop();
        await RefuseAsync("stopped service",()=>service.CallAsync("cua_capabilities",new(),null,default));
        Console.WriteLine("windows-cua: policy/snapshot/transport fixture passed (not desktop acceptance)");
    }

    private static async Task<int> RunCuaFixtureAsync()
    {
        if(!Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[]{"mcp","--direct"}))return 4;
        if (Environment.GetEnvironmentVariable("CUA_DRIVER_PERMISSION_MODE") != "bounded" ||
            Environment.GetEnvironmentVariable("DO_NOT_TRACK") != "1") return 2;
        var path=Environment.GetEnvironmentVariable("CUA_DRIVER_CAPABILITY_MANIFEST_FILE");
        var manifest=JsonNode.Parse(await File.ReadAllTextAsync(path!))!;
        if (manifest["resources"]?["desktop"]?["display"]?.GetValue<bool>() != false ||
            manifest["resources"]?["apps"]?.AsArray().Count != 1) return 3;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request=JsonNode.Parse(line)!;
            if(request["id"] is null) continue;
            var method=request["method"]!.GetValue<string>();
            JsonObject result;
            if(method=="initialize") result=new(){["protocolVersion"]="2025-06-18",["serverInfo"]=new JsonObject{["name"]="cua-driver",["version"]="0.61.0"}};
            else if(method=="tools/list")result=new(){["tools"]=new JsonArray(
                new JsonObject{["name"]="get_window_state",["inputSchema"]=new JsonObject{["properties"]=new JsonObject{["pid"]=new JsonObject(),["window_id"]=new JsonObject()},["required"]=new JsonArray("pid","window_id")}},
                new JsonObject{["name"]="hang",["inputSchema"]=new JsonObject{["properties"]=new JsonObject()}})};
            else
            {
                var p=request["params"]!;
                if(p["name"]!.GetValue<string>()=="hang") await Task.Delay(60000);
                result=new(){["structuredContent"]=new JsonObject{["echo"]=p["arguments"]!.DeepClone()}};
            }
            Console.WriteLine(new JsonObject{["jsonrpc"]="2.0",["id"]=request["id"]!.DeepClone(),["result"]=result}.ToJsonString());
            await Console.Out.FlushAsync();
        }
        return 0;
    }
}
