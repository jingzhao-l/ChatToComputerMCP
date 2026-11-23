# Install from npm source package

The npm package contains the open-source application source and vendored,
pinned tool binaries as the GitHub repository. It compiles the native app
**on the user's computer** rather than downloading a prebuilt application.

## Quick installation from npm

Requires Node.js 24 or newer. On macOS with Xcode Command Line Tools, or Windows with .NET 8 SDK:

    npx --yes chat-to-computer-mcp@latest install

This performs a local build in a release-versioned folder in the user's home.
It does NOT launch the app, enable shell permissions, store API tokens, alter
the old FileMCP instance or set up background startup.

Open after installation:

    npx --yes chat-to-computer-mcp@latest open

For repeat use:

    npm install --global chat-to-computer-mcp
    chat-to-computer-mcp install
    chat-to-computer-mcp open

To rebuild after quitting the installed instance:

    chat-to-computer-mcp install --rebuild

A previous successful release is retained. Running macOS apps are not replaced.

### Local macOS bundle signing and Keychain continuity

The native source installer seals the complete .app using ad-hoc signing by
 default (no Apple developer account needed), and verifies it with
`codesign --verify --deep --strict`. Ad-hoc signing is **not** Developer ID
signing or notarization. To preserve Keychain access when upgrading an
application signed by an existing Apple developer team, explicitly choose the
same locally installed signing identity for the new source build:

    CHAT_TO_COMPUTER_CODESIGN_IDENTITY='Apple Development: Your Name (TEAMID)' chat-to-computer-mcp install

This option uses the current Mac's signing identity without exporting keys or
credentials. Switching signing identity can cause a Keychain consent prompt.
Never replace a currently running app or service in place.

## Updating native applications

Both native GUIs offer **Check for updates** and **Install update** buttons,
alongside optional automatic checks/installation while the app is running.
All automatic modes are OFF by default, and neither the current app nor its
AI tasks are restarted by an update.

For a current CLI:

    chat-to-computer-mcp update check
    chat-to-computer-mcp update install
    chat-to-computer-mcp update enable --auto-install
    chat-to-computer-mcp update disable

An old 0.1.0 CLI does not have these commands; use a fresh npx invocation.
See [update guide](UPDATES.md) for versioned install directories and safe
activation.

## Optional background service (macOS)

Enable login launch with crash recovery:

    chat-to-computer-mcp launchagent enable

Always reopen even when the app exits normally:

    chat-to-computer-mcp launchagent enable --always

Automatic MCP tunnel connection is off by default; enable it explicitly with
--auto-connect. This is refused while the original FileMCP LaunchAgent is
running to prevent profile and credential collision. Configure an independent
profile and port before migrating a production connection.

Inspect and opt out:

    chat-to-computer-mcp launchagent status
    chat-to-computer-mcp launchagent disable

The Mac GUI offers equivalent next-login settings in Workspaces. To avoid
duplicate instances, the current GUI session is never restarted by the toggle.

## Private release candidates / offline source

Until npm Registry publication, install a prebuilt tarball:

    npm install --global ./chat-to-computer-mcp-0.4.0.tgz
    chat-to-computer-mcp install

A tarball can be attached to a GitHub release. When the GitHub repository
is private, users require access.

## Connect to ChatGPT after installation

Installing the application does not create the OpenAI Secure MCP Tunnel or
register the ChatGPT Plugin automatically. Follow the
[complete ChatGPT connection guide](CONNECT_CHATGPT.md) (or
[简体中文教程](CONNECT_CHATGPT.zh-CN.md)) for creating the tunnel,
associating the ChatGPT workspace, obtaining a restricted runtime Platform
API key, connecting from the native GUI and validating a real read-only MCP
tool call.

## Windows login supervisor (optional)

Windows provides an independent per-user login supervisor, matching the
Mac LaunchAgent's user-level lifecycle. It uses the user's own HKCU Run
registration, starts minimized to tray after sign-in, restarts abnormal
exits, and may optionally relaunch after normal Quit. It is NOT a
pre-login or privileged Windows system service.

Native Windows Settings offers explicit **Start on login**, **Auto-connect**,
**Always-on**, and **Apply login settings** controls. Nothing is registered
during normal npm install.

From an installed Windows CLI:

    chat-to-computer-mcp startup enable
    chat-to-computer-mcp startup enable --auto-connect --always
    chat-to-computer-mcp startup status
    chat-to-computer-mcp startup disable

Auto-connect is OFF by default and uses only existing approved Windows
Credential Manager and workspace settings. If FileMCP's legacy user login
registration exists, competing auto-connect is refused. An app update
refreshes only the already-enabled ChatToComputerMCP registration on disk,
never restarts a currently running process.

## Safety and support

- macOS local compile needs Swift and Apple SDK development tools.
  A local build is not a signed and notarized public distribution.
- Windows needs .NET 8 SDK, Windows PowerShell, and native Windows.
- The app is not an official OpenAI product.
- Approve local workspaces and permissions explicitly before connecting.
- Cwd collision checking is cooperative; shell processes are not sandboxed.
- The installer never exports, resets, or migrates existing credentials.
- It never modifies local.filemcp or the existing production proxy.

Before installing, confirm the package and version on the npm registry with
`npm view chat-to-computer-mcp version`. Do not confuse the GitHub
source prerelease with a published npm release.
