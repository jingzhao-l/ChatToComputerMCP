import AppKit
import Foundation

/// Manages only the independent ChatToComputerMCP login item. Never touches
/// the historical local.filemcp service, tunnel profiles or Keychain items.
enum ChatToComputerLaunchAgent {
    static let label = "local.chat-to-computer-mcp"
    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }
    static var isConfigured: Bool {
        FileManager.default.fileExists(atPath: plistURL.path)
    }
    static var isLoaded: Bool { serviceLoaded(label) }

    /// Preserve only deliberately configured network overrides across GUI
    /// changes. Never copy runtime API keys or unrelated launchd environment.
    static let proxyEnvironmentKeys: Set<String> = [
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "all_proxy", "no_proxy",
        "CONTROL_PLANE_HTTP_PROXY", "TUNNEL_CLIENT_HTTP_PROXY",
        "MCP_HTTP_PROXY", "HARPOON_HTTP_PROXY", "CA_BUNDLE",
        "PROXY_CHECK_INTERVAL",
    ]
    static func launchEnvironment(previous: [String: String], autoConnect: Bool) -> [String: String] {
        var result = previous.filter { proxyEnvironmentKeys.contains($0.key) && !$0.value.isEmpty }
        result["FILEMCP_AUTO_CONNECT"] = autoConnect ? "1" : "0"
        return result
    }

    private static func serviceLoaded(_ serviceLabel: String) -> Bool {
        let tool = Process()
        tool.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        tool.arguments = ["print", "gui/\(getuid())/\(serviceLabel)"]
        tool.standardOutput = Pipe()
        tool.standardError = Pipe()
        do { try tool.run(); tool.waitUntilExit(); return tool.terminationStatus == 0 }
        catch { return false }
    }
    static func settings() -> (enabled: Bool, autoConnect: Bool, always: Bool) {
        guard let data = try? Data(contentsOf: plistURL),
              let value = try? PropertyListSerialization.propertyList(from: data, format: nil),
              let plist = value as? [String: Any],
              plist["Label"] as? String == label else {
            return (false, false, false)
        }
        let env = plist["EnvironmentVariables"] as? [String: String] ?? [:]
        return (true, env["FILEMCP_AUTO_CONNECT"] == "1", plist["KeepAlive"] as? Bool == true)
    }
    /// Configure next login without bootstrapping a *second* copy of a GUI
    /// currently running in the user's foreground.
    static func setNextLogin(enabled: Bool, autoConnect: Bool, always: Bool) throws {
        let manager = FileManager.default
        let file = plistURL
        let parent = file.deletingLastPathComponent()
        try manager.createDirectory(at: parent, withIntermediateDirectories: true)
        if !enabled {
            // This removes the future-login registration. A service already
            // loaded in the current session remains alive until sign out or
            // explicit terminal opt-out; never unexpectedly kill the UI.
            if manager.fileExists(atPath: file.path) {
                try manager.removeItem(at: file)
            }
            return
        }
        guard let appURL = Bundle.main.bundleURL as URL?,
              let executable = Bundle.main.executableURL else {
            throw NSError(domain: label, code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Application bundle is unavailable."])
        }
        let source = appURL.path
        guard source.contains("ChatToComputerMCP.app"),
              !source.contains("/.building-"), !source.contains("/dist/"),
              !source.contains("/build/") else {
            throw NSError(domain: label, code: 2, userInfo: [
                NSLocalizedDescriptionKey: "Install the application to a stable folder first using the npm installer. The development build is not a safe login item."])
        }
        if autoConnect && serviceLoaded("local.filemcp") {
            throw NSError(domain: label, code: 4, userInfo: [
                NSLocalizedDescriptionKey: "Original FileMCP remains active. Automatic connection may collide with its tunnel. Configure an independent profile and deliberately disable the old service before enabling auto-connect."])
        }
        var previousEnvironment: [String: String] = [:]
        if manager.fileExists(atPath: file.path) {
            // Never overwrite another service's launch plist.
            let current = settings()
            guard current.enabled else {
                throw NSError(domain: label, code: 3, userInfo: [
                    NSLocalizedDescriptionKey: "An unrecognized login item already uses this label."])
            }
            let data = try Data(contentsOf: file)
            let value = try PropertyListSerialization.propertyList(from: data, format: nil)
            if let plist = value as? [String: Any],
               let environment = plist["EnvironmentVariables"] as? [String: String] {
                previousEnvironment = environment
            }
        }
        let logDir = manager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/ChatToComputerMCP")
        try manager.createDirectory(at: logDir, withIntermediateDirectories: true)
        let keepAlive: Any = always ? true : ["SuccessfulExit": false]
        let data: [String: Any] = [
            "Label": label, "ProgramArguments": [executable.path],
            "RunAtLoad": true, "KeepAlive": keepAlive,
            "EnvironmentVariables": launchEnvironment(previous: previousEnvironment, autoConnect: autoConnect),
            "StandardOutPath": logDir.appendingPathComponent("launchagent.stdout.log").path,
            "StandardErrorPath": logDir.appendingPathComponent("launchagent.stderr.log").path
        ]
        let encoded = try PropertyListSerialization.data(
            fromPropertyList: data, format: .xml, options: 0)
        try encoded.write(to: file, options: .atomic)
        try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}
