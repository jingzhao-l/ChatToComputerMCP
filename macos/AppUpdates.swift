import AppKit

/// Per-user update settings are deliberately independent from FileMCP,
/// tunnel profiles, Keychain, and auto-start configuration.
/// Only an explicit opt-in enables automatic check/install while the GUI runs.
struct DesktopUpdatePreferences: Codable {
    var schema = 1
    var checkAutomatically = false
    var installAutomatically = false
}

enum DesktopUpdater {
    private static let packageName = "chat-to-computer-mcp"
    private static let registry = URL(string: "https://registry.npmjs.org/chat-to-computer-mcp/latest")!

    static var settingsURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ChatToComputerMCP", isDirectory: true)
            .appendingPathComponent("update-settings.json")
    }

    static func readPreferences(at customFile: URL? = nil) throws -> DesktopUpdatePreferences {
        let file = customFile ?? settingsURL
        var entry = stat()
        if lstat(file.path, &entry) != 0 {
            guard errno == ENOENT else {
                throw NSError(domain: "ChatToComputerMCP updater", code: Int(errno))
            }
            return DesktopUpdatePreferences()
        }
        guard entry.st_mode & S_IFMT == S_IFREG else {
            throw NSError(domain: "ChatToComputerMCP updater", code: 2, userInfo: [
                NSLocalizedDescriptionKey: "Updater settings must be a regular file, never a symlink."])
        }
        let data = try Data(contentsOf: file)
        guard data.count <= 16_384 else {
            throw NSError(domain: "ChatToComputerMCP updater", code: 3)
        }
        let value = try JSONDecoder().decode(DesktopUpdatePreferences.self, from: data)
        guard value.schema == 1, !value.installAutomatically || value.checkAutomatically else {
            throw NSError(domain: "ChatToComputerMCP updater", code: 4)
        }
        return value
    }

    static func writePreferences(_ prefs: DesktopUpdatePreferences,
                                 to customFile: URL? = nil) throws {
        guard prefs.schema == 1, !prefs.installAutomatically || prefs.checkAutomatically else {
            throw NSError(domain: "ChatToComputerMCP updater", code: 5)
        }
        let target = customFile ?? settingsURL
        let folder = target.deletingLastPathComponent()
        try FileManager.default.createDirectory(
            at: folder, withIntermediateDirectories: true,
            attributes: [.posixPermissions: NSNumber(value: 0o700)])
        var entry = stat()
        if lstat(target.path, &entry) == 0, entry.st_mode & S_IFMT != S_IFREG {
            throw NSError(domain: "ChatToComputerMCP updater", code: 6, userInfo: [
                NSLocalizedDescriptionKey: "Refusing to overwrite symlinked updater settings"])
        }
        let content = try JSONEncoder().encode(prefs)
        let temporary = folder.appendingPathComponent(
            ".update-settings-\(UUID().uuidString).tmp")
        defer { try? FileManager.default.removeItem(at: temporary) }
        try content.write(to: temporary, options: .withoutOverwriting)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o600)], ofItemAtPath: temporary.path)
        guard rename(temporary.path, target.path) == 0 else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno),
                userInfo: [NSLocalizedDescriptionKey:
                    "Could not atomically save updater preferences"])
        }
    }

    static func validVersion(_ version: String) -> Bool {
        version.range(of: #"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"#,
                      options: .regularExpression) != nil
    }

    static func isNewer(_ candidate: String, than current: String) -> Bool {
        guard validVersion(candidate), validVersion(current) else { return false }
        let lhs = candidate.split(separator: ".").compactMap { UInt64($0) }
        let rhs = current.split(separator: ".").compactMap { UInt64($0) }
        guard lhs.count == 3, rhs.count == 3 else { return false }
        for (a,b) in zip(lhs,rhs) {
            if a != b { return a > b }
        }
        return false
    }

    static func currentVersion() -> String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? "0.0.0"
    }

    static func alreadyInstalled(_ version: String) -> Bool {
        guard validVersion(version) else { return false }
        let executable = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(
                ".local/share/chat-to-computer-mcp/releases/\(version)/ChatToComputerMCP.app/Contents/MacOS/ChatToComputerMCP")
        return FileManager.default.isExecutableFile(atPath: executable.path)
    }

    /// Only official npm registry, no private keys or ChatGPT credentials.
    static func check(completion: @escaping (Result<String, Error>) -> Void) {
        var request = URLRequest(url: registry)
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.timeoutInterval = 15
        URLSession.shared.dataTask(with: request) { data, response, error in
            if let error {
                DispatchQueue.main.async { completion(.failure(error)) }
                return
            }
            do {
                guard (response as? HTTPURLResponse)?.statusCode == 200,
                      let data, data.count <= 512_000,
                      let body = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      body["name"] as? String == packageName,
                      let version = body["version"] as? String, validVersion(version) else {
                    throw NSError(domain: "ChatToComputerMCP updater", code: 7,
                        userInfo: [NSLocalizedDescriptionKey: "Unexpected npm registry response"])
                }
                DispatchQueue.main.async { completion(.success(version)) }
            } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }.resume()
    }

    private static func npmExecutable() -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let inherited = (ProcessInfo.processInfo.environment["PATH"] ?? "")
            .split(separator: ":").map(String.init)
        var locations = inherited + [
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin",
            home + "/.volta/bin", home + "/.local/bin",
            home + "/Library/pnpm"
        ]
        // Node installed through nvm is not normally on LaunchServices PATH.
        let nvmVersions = home + "/.nvm/versions/node"
        if let entries = try? FileManager.default.contentsOfDirectory(atPath: nvmVersions) {
            locations += entries.sorted().reversed().prefix(10).map {
                nvmVersions + "/" + $0 + "/bin"
            }
        }
        for folder in locations {
            let executable = folder + "/npm"
            if FileManager.default.isExecutableFile(atPath: executable) {
                return URL(fileURLWithPath: executable)
            }
        }
        return nil
    }

    /// Never let npm exec search the application's inherited cwd for a
    /// same-named, potentially older, locally linked package.
    static func isolatedNpmWorkingDirectory() throws -> URL {
        let location = FileManager.default.temporaryDirectory
            .appendingPathComponent("ctcmcp-gui-official-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(
            at: location, withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700])
        return location
    }

    /// Installs a *different versioned app bundle* without terminating the
    /// active GUI, reconnecting the tunnel or changing permissions.
    static func install(version: String,
                        completion: @escaping (Result<Void, Error>) -> Void) {
        guard validVersion(version) else {
            completion(.failure(NSError(domain: "ChatToComputerMCP updater", code: 8)))
            return
        }
        guard let npm = npmExecutable() else {
            completion(.failure(NSError(domain: "ChatToComputerMCP updater", code: 9,
                userInfo: [NSLocalizedDescriptionKey:
                    "npm was not found. Install Node.js or use the update command in Terminal."])))
            return
        }
        DispatchQueue.global(qos: .utility).async {
            do {
                let process = Process()
                process.executableURL = npm
                process.arguments = [
                    "exec", "--yes", "--registry=https://registry.npmjs.org/",
                    "--package=chat-to-computer-mcp@" + version, "--",
                    "chat-to-computer-mcp", "install"
                ]
                // npm exec can select an older local package when launched
                // from a development repo or an app's inherited working folder.
                // Resolve the requested official version from a fresh directory
                // containing no package manifest; never touch the live app.
                let isolated = try isolatedNpmWorkingDirectory()
                defer { try? FileManager.default.removeItem(at: isolated) }
                process.currentDirectoryURL = isolated
                var env = ProcessInfo.processInfo.environment
                let pathPrefix = npm.deletingLastPathComponent().path
                env["PATH"] = pathPrefix + ":/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" +
                    (env["PATH"] ?? "")
                process.environment = env
                process.standardInput = FileHandle.nullDevice
                process.standardOutput = FileHandle.nullDevice
                process.standardError = FileHandle.nullDevice
                try process.run()
                process.waitUntilExit()
                guard process.terminationStatus == 0, alreadyInstalled(version) else {
                    throw NSError(domain: "ChatToComputerMCP updater",
                        code: Int(process.terminationStatus),
                        userInfo: [NSLocalizedDescriptionKey:
                            "Local update build failed. Use 'chat-to-computer-mcp update install' in Terminal to inspect the build error."])
                }
                DispatchQueue.main.async { completion(.success(())) }
            } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }
    }
}
