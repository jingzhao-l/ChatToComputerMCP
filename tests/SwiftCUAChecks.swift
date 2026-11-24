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
        try snap.validate(["element_token":"s00000001:1"])
        try snap.validate(["x":99,"y":79,"capture_id":"cap"])
        expectFailure("foreign element") { try snap.validate(["element_token":"s00000002:1"]) }
        expectFailure("same snapshot unknown row") { try snap.validate(["element_token":"s00000001:2"]) }
        expectFailure("stale capture") { try snap.validate(["x":5,"y":5,"capture_id":"old"]) }
        expectFailure("out of bounds coordinate") { try snap.validate(["x":100,"y":0,"capture_id":"cap"]) }
        _ = try CUAService.verifyPredicates([["element":["selector":["role":"button","label_contains":"OK"],"exists":true]]])
        expectFailure("false absence claim") { _ = try CUAService.verifyPredicates([["element":["selector":["role":"button"],"exists":false]]]) }
        expectFailure("arbitrary nested selector") { _ = try CUAService.verifyPredicates([["element":["selector":["pid":1],"exists":true]]]) }
        expectFailure("numeric boolean") { _ = try CUAService.verifyPredicates([["element":["selector":["role":"button"],"exists":1]]]) }

        let worker = try CUAWorker(driver:fixture,application:"com.example.Fixture")
        let result = try worker.call("get_window_state",["pid":1,"window_id":2],allowed:{true})
        let structured = result["structuredContent"] as! [String:Any]
        let echo = structured["echo"] as! [String:Any]
        precondition(echo["window_id"] as? Int == 2)
        worker.close()
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
