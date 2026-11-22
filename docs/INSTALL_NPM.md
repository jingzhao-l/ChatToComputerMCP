# Install from npm source package

The npm package contains the open-source application source and vendored,
pinned tool binaries as the GitHub repository. It compiles the native app
**on the user's computer** rather than downloading a prebuilt application.

## After npm publication

On macOS with Xcode Command Line Tools, or Windows with .NET 8 SDK:

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

    npm install --global ./chat-to-computer-mcp-0.1.0.tgz
    chat-to-computer-mcp install

A tarball can be attached to a GitHub release. When the GitHub repository
is private, users require access.

## Safety and support

- macOS local compile needs Swift and Apple SDK development tools.
  A local build is not a signed and notarized public distribution.
- Windows needs .NET 8 SDK, Windows PowerShell, and native Windows.
- The app is not an official OpenAI product.
- Approve local workspaces and permissions explicitly before connecting.
- Cwd collision checking is cooperative; shell processes are not sandboxed.
- The installer never exports, resets, or migrates existing credentials.
- It never modifies local.filemcp or the existing production proxy.

As of this revision, npm registry publication is pending npm account
authentication. The npm latest command cannot work until publication.
