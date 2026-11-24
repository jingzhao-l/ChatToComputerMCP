import Foundation

func runNativeUpdateChecks() throws {
    precondition(DesktopUpdater.validVersion("0.4.0"))
    precondition(DesktopUpdater.validVersion("0.3.0"))
    precondition(DesktopUpdater.isNewer("0.3.0", than: "0.2.1"))
    precondition(!DesktopUpdater.isNewer("0.2.0", than: "0.10.0"))
    for invalid in ["v0.3.0", "0.3.0-stage", "../etc/passwd",
                    "0.3.0;rm -rf /", "00.3.0", "0.3"] {
        precondition(!DesktopUpdater.validVersion(invalid), invalid)
    }
    let isolated = try DesktopUpdater.isolatedNpmWorkingDirectory()
    defer { try? FileManager.default.removeItem(at: isolated) }
    precondition(!FileManager.default.fileExists(
        atPath: isolated.appendingPathComponent("package.json").path))
    let isolatedPermissions = try FileManager.default.attributesOfItem(atPath: isolated.path)
    precondition((isolatedPermissions[.posixPermissions] as? NSNumber)?.intValue == 0o700)
    precondition(isolated.lastPathComponent.hasPrefix("ctcmcp-gui-official-update-"))
    print("PASS: native GUI updater isolates official npm package selection")

    let home = FileManager.default.temporaryDirectory.appendingPathComponent(
        "ctcmcp-update-settings-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    defer {try? FileManager.default.removeItem(at: home)}
    let file = home.appendingPathComponent("private/update-settings.json")
    let original = try DesktopUpdater.readPreferences(at: file)
    precondition(!original.checkAutomatically && !original.installAutomatically,
                 "updater must be disabled by default")
    let enabled = DesktopUpdatePreferences(schema: 1,
        checkAutomatically: true, installAutomatically: true)
    try DesktopUpdater.writePreferences(enabled, to: file)
    let roundTrip = try DesktopUpdater.readPreferences(at: file)
    precondition(roundTrip.checkAutomatically && roundTrip.installAutomatically)
    let permissions = try FileManager.default.attributesOfItem(atPath: file.path)
    precondition((permissions[.posixPermissions] as? NSNumber)?.intValue == 0o600)
    try DesktopUpdater.writePreferences(
        DesktopUpdatePreferences(schema: 1, checkAutomatically: false,
                                 installAutomatically: false), to: file)
    let disabled = try DesktopUpdater.readPreferences(at: file)
    precondition(!disabled.checkAutomatically)
    try Data("{corrupted".utf8).write(to: file, options: .atomic)
    do {
        _ = try DesktopUpdater.readPreferences(at: file)
        preconditionFailure("Corrupted update policy was silently reset")
    } catch { }
    try FileManager.default.removeItem(at: file)
    let outside = home.appendingPathComponent("outside.json")
    try "untouched".write(to: outside, atomically: true, encoding: .utf8)
    try FileManager.default.createSymbolicLink(at: file, withDestinationURL: outside)
    do {
        try DesktopUpdater.writePreferences(enabled, to: file)
        preconditionFailure("Updater policy wrote through symbolic link")
    } catch { }
    let after = try String(contentsOf: outside, encoding: .utf8)
    precondition(after == "untouched")
    print("PASS: native update policy is opt-in, atomic, 0600, reversible, no symlink overwrite")
    for version in ["../escape", "99.99.99"] {
        do {
            _ = try DesktopUpdater.activationBundle(version: version, releases: home)
            preconditionFailure("Missing/invalid release accepted")
        } catch {}
    }
    let alias = home.appendingPathComponent("1.2.3")
    try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: home)
    do {
        _ = try DesktopUpdater.activationBundle(version: "1.2.3", releases: home)
        preconditionFailure("Linked release accepted")
    } catch {}
    print("PASS: update activation rejects invalid, missing and linked releases")
}
