import AppKit
import Darwin

if CommandLine.arguments.dropFirst() == ["--cua-guest-server"] {
    do {
        let base = CUAPolicy.defaultURL.deletingLastPathComponent()
        let config = base.appendingPathComponent("cua-guest.json")
        let tokenURL = base.appendingPathComponent("cua-guest-token")
        func privateData(_ url: URL) throws -> Data {
            var info=stat()
            guard lstat(url.path,&info)==0,(info.st_mode&S_IFMT)==S_IFREG,info.st_uid==getuid(),
                  info.st_mode&0o077==0,info.st_size>0,info.st_size<=4096 else {throw CUAFailure("Guest configuration must be private")}
            return try Data(contentsOf:url)
        }
        guard let settings=try JSONSerialization.jsonObject(with:privateData(config)) as? [String:Any],
              settings["isolatedDesktopConfirmed"] as? Bool==true,
              let port=settings["port"] as? UInt16, port>=1024,
              let token=String(data:try privateData(tokenURL),encoding:.utf8)?.trimmingCharacters(in:.whitespacesAndNewlines),
              token.range(of:"^[a-f0-9]{64}$",options:.regularExpression) != nil else {throw CUAFailure("Run local cua guest-init in the isolated guest first")}
        let sandbox=base.appendingPathComponent("guest-empty-workspace")
        try FileManager.default.createDirectory(at:sandbox,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let server=try LocalMCPServer(port:port,allowedDirectory:sandbox.path,gitUserName:"",gitUserEmail:"",enableCommands:false,localAuthToken:token,cuaGuest:true,log:{message in fputs(message,stderr)})
        try server.start()
        // Keep a local interactive session alive without the native GUI.
        signal(SIGINT,SIG_IGN);signal(SIGTERM,SIG_IGN)
        let interrupt=DispatchSource.makeSignalSource(signal:SIGINT,queue:.main)
        let terminate=DispatchSource.makeSignalSource(signal:SIGTERM,queue:.main)
        for source in [interrupt,terminate] {source.setEventHandler{server.stop();exit(0)};source.resume()}
        withExtendedLifetime((server,interrupt,terminate)){
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
