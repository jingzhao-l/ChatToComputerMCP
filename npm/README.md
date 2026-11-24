# npm source installer

For an existing public npm registry release, users can compile and install
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

The @latest npx command requires a real npm registry version. Automatic future
releases use the GitHub Actions OIDC workflow; see docs/PUBLISHING.md.

Optional native computer use: run `chat-to-computer-mcp cua help` after installing. The pinned driver and optional browser runtime are installed separately; no desktop permissions are granted by npm. Guest is disabled by default. See [CUA documentation](../docs/CUA.md).
