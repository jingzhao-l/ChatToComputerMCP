import AppKit
import Darwin

if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--command-worker" {
    do { try SupervisedCommandJob.runWorker(directory: URL(fileURLWithPath: CommandLine.arguments[2])); exit(0) }
    catch { exit(1) }
}

let relayDomain: String? = CommandLine.arguments.count == 3 && CommandLine.arguments[1] == "--cua-relay-server" ? CommandLine.arguments[2] : nil
if CommandLine.arguments.dropFirst() == ["--cua-guest-server"] || relayDomain != nil {
    do {
        guard let approvalEpoch = CUAGuestApproval.epoch() else { throw CUAFailure("Guest is disabled; enable it locally first") }
        var base = CUAPolicy.defaultURL.deletingLastPathComponent()
        if let domain=relayDomain {
            guard domain.range(of:"^[A-Za-z0-9_-]{1,48}$",options:.regularExpression) != nil else {throw CUAFailure("Invalid relay domain")}
            base=base.appendingPathComponent("cua-relays").appendingPathComponent(domain)
            var info=stat()
            guard lstat(base.path,&info)==0,(info.st_mode&S_IFMT)==S_IFDIR,info.st_uid==getuid(),info.st_mode&0o077==0 else {throw CUAFailure("Private relay directory required")}
        }
        let config = base.appendingPathComponent(relayDomain == nil ? "cua-guest.json" : "relay.json")
        let tokenURL = base.appendingPathComponent(relayDomain == nil ? "cua-guest-token" : "relay-token")
        func privateData(_ url: URL) throws -> Data {
            var info=stat()
            guard lstat(url.path,&info)==0,(info.st_mode&S_IFMT)==S_IFREG,info.st_uid==getuid(),
                  info.st_mode&0o077==0,info.st_size>0,info.st_size<=4096 else {throw CUAFailure("Guest configuration must be private")}
            return try Data(contentsOf:url)
        }
        guard let settings=try JSONSerialization.jsonObject(with:privateData(config)) as? [String:Any],
              (relayDomain == nil ? settings["isolatedDesktopConfirmed"] as? Bool==true : settings["dedicatedGuestConfirmed"] as? Bool==true && settings["domain"] as? String == relayDomain),
              let port=settings["port"] as? UInt16, port>=1024,
              let token=String(data:try privateData(tokenURL),encoding:.utf8)?.trimmingCharacters(in:.whitespacesAndNewlines),
              token.range(of:"^[a-f0-9]{64}$",options:.regularExpression) != nil else {throw CUAFailure("Run local cua guest-init in the isolated guest first")}
        let sandbox=base.appendingPathComponent("guest-empty-workspace")
        try FileManager.default.createDirectory(at:sandbox,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let server=try LocalMCPServer(port:port,allowedDirectory:sandbox.path,gitUserName:"",gitUserEmail:"",enableCommands:false,localAuthToken:token,cuaGuest:relayDomain == nil,cuaRelayOnly:relayDomain != nil,cuaPolicyURL:base.appendingPathComponent("cua-policy.json"),log:{message in fputs(message,stderr)})
        guard CUAGuestApproval.epoch() == approvalEpoch else { throw CUAFailure("Guest approval changed") }
        try server.start()
        // Keep a local interactive session alive without the native GUI.
        signal(SIGINT,SIG_IGN);signal(SIGTERM,SIG_IGN)
        let interrupt=DispatchSource.makeSignalSource(signal:SIGINT,queue:.main)
        let terminate=DispatchSource.makeSignalSource(signal:SIGTERM,queue:.main)
        for source in [interrupt,terminate] {source.setEventHandler{server.stop();exit(0)};source.resume()}
        let approvalWatch=DispatchSource.makeTimerSource(queue:.main)
        approvalWatch.schedule(deadline:.now(),repeating:.milliseconds(250))
        approvalWatch.setEventHandler {
            if CUAGuestApproval.epoch() != approvalEpoch { server.stop(); exit(0) }
        }
        approvalWatch.resume()
        withExtendedLifetime((server,interrupt,terminate,approvalWatch)){
            RunLoop.main.run()
        }
    } catch {
        fputs("CUA guest startup failed: \(error.localizedDescription)\n",stderr)
        exit(1)
    }
} else {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.regular)
    app.run()
}
