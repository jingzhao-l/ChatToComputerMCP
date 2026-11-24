// Appended to LocalMCPServer.swift by the fixture so discovery remains a
// private production implementation rather than a public debug capability.
extension LocalMCPServer {
    func contractFixtureDefinitions() -> [[String: Any]] { toolDefinitions(for: nil) }
}

func dumpBaseToolContract(root: URL) throws {
    var profiles: [String: [[String: Any]]] = [:]
    for profile in ["default", "full", "read_only"] {
        let writable = profile != "read_only"
        let full = profile == "full"
        let server = try LocalMCPServer(
            port: 1, allowedDirectory: root.path, gitUserName: "", gitUserEmail: "",
            enableCommands: full, enableGitPush: full, enableCodexHistory: full,
            defaultWorkspacePermissions: WorkspacePermissions(allowWrite: writable, allowShell: full,
                allowGitPush: full, allowCodexHistory: full),
            localAuthToken: String(repeating: "a", count: 64), log: { _ in })
        profiles[profile] = server.contractFixtureDefinitions().filter {
            !($0["name"] as? String ?? "").hasPrefix("cua_")
        }
        server.stop()
    }
    let data = try JSONSerialization.data(withJSONObject: profiles, options: [.sortedKeys])
    print(String(decoding: data, as: UTF8.self))
}
