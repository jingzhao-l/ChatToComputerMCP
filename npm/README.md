# npm source installer

After the first public npm registry release, users can compile and install
the desktop application in one command:

    npx --yes chat-to-computer-mcp@latest install

A release candidate can be packed with npm pack and installed from the tarball.
The npm package includes the checked-in Swift/C# source and pinned vendor tools,
not an opaque executable downloader.

* macOS requires Xcode Command Line Tools and Swift.
* Windows requires .NET 8 SDK and Windows PowerShell.
* npm install does NOT automatically compile, run or restart a service.
* CLI install compiles the app locally and retains previous releases on rebuild.
* Local builds are not Apple notarized or automatically trusted by Keychain.
* LaunchAgent is opt-in with launchagent enable and explicit disable command.
* KeepAlive defaults to restart-on-crash; --always also restarts a normal exit.
* Auto-connect is off unless --auto-connect is explicitly requested, and that
  mode refuses an active original local.filemcp launchd service.
* No original Keychain, proxy, tunnel profile, or old launchagent is overwritten.

The package must actually be published before the @latest npx command works.
Registry publication requires a separately configured npm account.
