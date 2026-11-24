import Foundation

@main struct SwiftCUAChecks {
    static func expectFailure(_ label: String, _ operation: () throws -> Void) {
        do { try operation(); fatalError("Expected refusal: " + label) }
        catch { print("PASS refuse: " + label) }
    }
    static func main() throws {
        let temp = URL(fileURLWithPath:CommandLine.arguments[1])
        let fixture = CommandLine.arguments[2]
        let service = CUAService(policyURL:temp.appendingPathComponent("missing.json"))
        defer { service.stop() }
        let (_,status) = try service.call("cua_capabilities",args:[:],owner:nil)
        precondition(status["enabled"] as? Bool == false)
        precondition(status["identity"] as? String == "shared_connection_not_chat_identity")
        expectFailure("default disabled") { _ = try service.call("cua_list_apps",args:[:],owner:nil) }
        expectFailure("remote policy mutation") { _ = try service.call("cua_capabilities",args:["enabled":true],owner:nil) }
        expectFailure("guessed session") { _ = try service.call("cua_stop_session",args:["session_id":"fake"],owner:nil) }
        let defs = CUAService.definitions()
        precondition(defs.count == 15 && Set(defs.compactMap { $0["name"] as? String }).count == defs.count)
        let actionID=String(repeating:"r",count:48)
        let validatedID=try CUAService.requestID(actionID); precondition(validatedID == actionID)
        expectFailure("short request ID") { _ = try CUAService.requestID("retry") }
        try CUAService.validateAction("set_window_frame",["x":10,"y":20,"width":640,"height":480])
        expectFailure("invalid window geometry") { try CUAService.validateAction("set_window_frame",["x":0,"y":0,"width":0,"height":480]) }
        precondition(!CUAService.allowedApplication("com.apple.loginwindow"))
        let receiptURL=temp.appendingPathComponent("test-cua-receipts.json")
        do { let ledger=try CommandReceiptLedger(fileURL:receiptURL); try ledger.reserve(scopeID:"cua",requestID:actionID,ownerID:"client-one") }
        let reloaded=try CommandReceiptLedger(fileURL:receiptURL)
        expectFailure("durable replay") { try reloaded.reserve(scopeID:"cua",requestID:actionID,ownerID:"client-one") }
        expectFailure("foreign receipt") { _ = try reloaded.ownReceipt(scopeID:"cua",requestID:actionID,ownerID:"client-two") }
        try CUAService.validateAction("click",["element_token":"s00000001:1"])
        try CUAService.validateAction("click",["x":2,"y":3,"capture_id":"capture-1"])
        try CUAService.validateAction("drag",["from_x":2,"from_y":3,"to_x":4,"to_y":5,"capture_id":"capture-1"])
        for (label,op,args) in [
            ("foreground injection","click",["element_token":"s00000001:1","delivery_mode":"foreground"]),
            ("desktop injection","click",["x":2,"y":3,"capture_id":"cap","scope":"desktop"]),
            ("target override","click",["element_token":"s00000001:1","pid":1]),
            ("nested target override","click",["element_token":"s00000001:1","target":["kind":"desktop"]]),
            ("arbitrary tool","run_script",["code":"x"]),
            ("unbound pixel click","click",["x":2,"y":3]),
            ("unbound drag","drag",["from_x":2,"from_y":3,"to_x":4,"to_y":5]),
            ("ambiguous token and coordinates","click",["element_token":"s00000001:1","x":2]),
            ("huge drag duration","drag",["from_x":2,"from_y":3,"to_x":4,"to_y":5,"capture_id":"cap","duration_ms":9999])
        ] as [(String,String,[String:Any])] {
            expectFailure(label) { try CUAService.validateAction(op,args) }
        }
        // PNG header fixture only; no captured personal data.
        let png = Data([137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,100,0,0,0,80])
        let snap = try CUASnapshot(structured:["snapshot_id":"s00000001","ui_tree_revision":7,"capture_id":"cap","elements":[["element_token":"s00000001:1"]]],blocks:[
            ["type":"image","mimeType":"image/png","data":png.base64EncodedString()]])
        try snap.validateObservationID(snap.observationID)
        expectFailure("foreign gateway observation") {try snap.validateObservationID("other")}
        try snap.validate(["element_token":"s00000001:1"])
        try snap.validate(["x":99,"y":79,"capture_id":"cap"])
        expectFailure("foreign element") { try snap.validate(["element_token":"s00000002:1"]) }
        expectFailure("same snapshot unknown row") { try snap.validate(["element_token":"s00000001:2"]) }
        expectFailure("stale capture") { try snap.validate(["x":5,"y":5,"capture_id":"old"]) }
        expectFailure("out of bounds coordinate") { try snap.validate(["x":100,"y":0,"capture_id":"cap"]) }
        let geometry=CUAWindowGeometry(x:-100,y:40,width:800,height:600,onScreen:true)
        let imageBlocks:[[String:Any]]=[["type":"image","mimeType":"image/png","data":png.base64EncodedString()]]
        let visual=try CUASnapshot(structured:["capture_id":"cap"],blocks:imageBlocks,geometry:geometry,includeElements:false)
        precondition(visual.actionable && visual.pixelGrounded && !visual.treeGrounded && visual.tokens.isEmpty)
        try visual.validatePixelGeometry(geometry)
        try visual.validate(["x":5,"y":10,"capture_id":"cap"])
        expectFailure("moved window invalidates screenshot coordinates"){try visual.validatePixelGeometry(CUAWindowGeometry(x:-99,y:40,width:800,height:600,onScreen:true))}
        expectFailure("off-screen window invalidates screenshot coordinates"){try visual.validatePixelGeometry(CUAWindowGeometry(x:-100,y:40,width:800,height:600,onScreen:false))}
        expectFailure("visual mode does not inherit AX elements"){try visual.validate(["element_token":"s00000001:1"])}
        let screenshotWithoutCapture=try CUASnapshot(structured:[:],blocks:imageBlocks,geometry:geometry,includeElements:false)
        precondition(!screenshotWithoutCapture.actionable)
        let mismatched=try CUASnapshot(structured:["capture_id":"cap","screenshot_width":101],blocks:imageBlocks,geometry:geometry,includeElements:false)
        precondition(!mismatched.actionable)
        let treeOnly=try CUASnapshot(structured:["snapshot_id":"s00000001","elements":[["element_token":"s00000001:1"]]],blocks:[],geometry:nil)
        precondition(treeOnly.treeGrounded && treeOnly.actionable && !treeOnly.pixelGrounded)
        let imageOnly=try CUAService.observationParameters(["include_accessibility_tree":false])
        precondition(imageOnly["include_screenshot"] as? Bool == true && imageOnly["include_accessibility_tree"] as? Bool == false)
        expectFailure("empty observation"){_ = try CUAService.observationParameters(["include_screenshot":false,"include_accessibility_tree":false])}
        expectFailure("visual mode refuses tree query"){_ = try CUAService.observationParameters(["include_accessibility_tree":false,"query":"button"])}
        expectFailure("invalid observation bool"){_ = try CUAService.observationParameters(["include_accessibility_tree":1])}
        print("PASS visual-only and accessibility-only grounding; independent window geometry admission")

        _ = try CUAService.verifyPredicates([["element":["selector":["role":"button","label_contains":"OK"],"exists":true]]])
        expectFailure("false absence claim") { _ = try CUAService.verifyPredicates([["element":["selector":["role":"button"],"exists":false]]]) }
        expectFailure("arbitrary nested selector") { _ = try CUAService.verifyPredicates([["element":["selector":["pid":1],"exists":true]]]) }
        expectFailure("numeric boolean") { _ = try CUAService.verifyPredicates([["element":["selector":["role":"button"],"exists":1]]]) }

        try CUAService.validateAction("hotkey",["keys":["cmd","s"],"element_token":"s00000001:1"])
        try CUAService.validateAction("type_text",["text":"test","element_token":"s00000001:1","delay_ms":25])
        try CUAService.validateAction("scroll",["direction":"down","element_token":"s00000001:1","by":"page"])
        expectFailure("global app switch") {try CUAService.validateAction("hotkey",["keys":["cmd","tab"]])}
        expectFailure("unbound coordinate typing") {try CUAService.validateAction("type_text",["x":1,"y":2,"text":"test"])}
        expectFailure("invalid key") {try CUAService.validateAction("press_key",["key":"superkey"])}
        let obs=try CUAService.observationParameters(["query":"button|menu","max_elements":1000,"max_depth":40,"timeout_ms":3000])
        precondition(obs["max_elements"] as? Int == 1000 && obs["query"] as? String == "button|menu")
        expectFailure("unbounded tree") {_ = try CUAService.observationParameters(["max_elements":100000])}
        expectFailure("numeric boolean observation budget") {_ = try CUAService.observationParameters(["max_depth":true])}
        _ = try CUAService.verifyPredicates([["window":["bounds":["x":-100,"y":20,"width":640,"height":480,"tolerance_px":3]]]])
        expectFailure("invalid geometry predicate") {_ = try CUAService.verifyPredicates([["window":["bounds":["x":0,"y":0,"width":1,"height":-1]]]])}
        expectFailure("schema refuses unknown key") {
            _ = try CUAWorker.validatedDriverArguments("click",["pid":1,"delivery_mode":"background"],schema:["properties":["pid":[:]]])
        }
        if let real=ProcessInfo.processInfo.environment["CTCMCP_REAL_CUA_DRIVER"] {
            let pinned=try CUAWorker(driver:real,application:"com.example.NonexistentFixture")
            defer{pinned.close()}
            let permissions=try pinned.call("check_permissions",["prompt":false,"probe_direct_capture":false],allowed:{true})
            precondition(permissions["isError"] as? Bool != true)
            print("PASS pinned actual Mac driver handshake, tools/list and read-only permission probe; no desktop input")
        }

        let processStart=Date(timeIntervalSince1970:1000)
        let oldWindow=CUATarget(pid:100,window:1,application:"com.example.Fixture",launched:processStart)
        precondition(oldWindow.sameProcess(as:CUATarget(pid:100,window:2,application:"com.example.Fixture",launched:processStart)))
        precondition(!oldWindow.sameProcess(as:CUATarget(pid:101,window:2,application:"com.example.Fixture",launched:processStart)))
        precondition(!oldWindow.sameProcess(as:CUATarget(pid:100,window:2,application:"com.example.Other",launched:processStart)))
        precondition(!oldWindow.sameProcess(as:CUATarget(pid:100,window:2,application:"com.example.Fixture",launched:processStart.addingTimeInterval(1))))
        precondition(CUAService.recoverableNativeRefusal(["code":"background_unavailable","effect":"refused"])=="background_unavailable")
        precondition(CUAService.recoverableNativeRefusal(["code":"option_not_found","effect":"none"])=="option_not_found")
        precondition(CUAService.recoverableNativeRefusal(["code":"background_occluded","effect":"unverifiable"])==nil)
        precondition(CUAService.recoverableNativeRefusal(["code":"unknown","effect":"refused"])==nil)
        precondition(CUAService.recoverableNativeRefusal(["code":"background_unavailable","effect":"refused","foreground_step":true])==nil)
        precondition(CUAService.recoverableNativeRefusal(["code":"background_unavailable"])==nil)
        let transferSchema=CUAService.definitions().first{$0["name"] as? String=="cua_create_session"}!["inputSchema"] as! [String:Any]
        precondition((transferSchema["properties"] as! [String:Any])["previous_session_id"] != nil)
        print("PASS same-process transfer identity and strict typed background refusal classification")
        var front:Int64?=101
        let continuity=CUAForegroundContinuity(read:{front})
        precondition(continuity.permits())
        front=102;precondition(!continuity.permits())
        front=101;precondition(!continuity.permits())
        let eventLatch=CUAForegroundContinuity(read:{front})
        eventLatch.invalidate();precondition(!eventLatch.permits())
        front=nil;precondition(!CUAForegroundContinuity(read:{front}).permits())
        print("PASS foreground continuity fails closed and never resumes after interruption")
        let worker = try CUAWorker(driver:fixture,application:"com.example.Fixture")
        let result = try worker.call("get_window_state",["pid":1,"window_id":2],allowed:{true})
        let structured = result["structuredContent"] as! [String:Any]
        let echo = structured["echo"] as! [String:Any]
        precondition(echo["window_id"] as? Int == 2)
        expectFailure("before-dispatch geometry guard refuses transport") { _ = try worker.call("get_window_state",["pid":1,"window_id":2],allowed:{true},beforeDispatch:{throw CUAFailure("stale geometry")}) }
        expectFailure("unadvertised driver tool") {_ = try worker.call("bring_to_front",[:],allowed:{true})}
        worker.close()
        precondition(!worker.isAlive)
        expectFailure("closed worker never replays") { _ = try worker.call("click",[:],allowed:{true}) }
        let hanging = try CUAWorker(driver:fixture,application:"com.example.Fixture")
        let start = Date()
        expectFailure("in-flight revocation interrupts driver transport") {
            _ = try hanging.call("hang",[:],allowed:{Date().timeIntervalSince(start) < 0.5})
        }
        hanging.close()
        precondition(Date().timeIntervalSince(start) < 3)
        let oversized = try CUAWorker(driver:fixture,application:"com.example.Fixture")
        expectFailure("bounded response frame") { _ = try oversized.call("oversize",[:],allowed:{true}) }
        oversized.close()
        service.revoke(owner:"oauth-client-1")
        expectFailure("revoked verified owner") { _ = try service.call("cua_capabilities",args:[:],owner:"oauth-client-1") }
        service.stop()
        expectFailure("shutdown rejects all calls") { _ = try service.call("cua_capabilities",args:[:],owner:nil) }
        print("PASS CUA gateway security, snapshot and bounded stdio fixture (not actual desktop acceptance)")
    }
}
