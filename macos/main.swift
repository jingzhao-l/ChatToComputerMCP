import AppKit

if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--command-worker" {
    do { try SupervisedCommandJob.runWorker(directory: URL(fileURLWithPath: CommandLine.arguments[2])); exit(0) }
    catch { exit(1) }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
