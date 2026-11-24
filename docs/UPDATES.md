# Updating ChatToComputerMCP

ChatToComputerMCP uses **source-only npm releases**. Updates download official
published sources and compile a new native app on the user's own computer.

## Command-line updater

Once you have a CLI with update support:

    chat-to-computer-mcp update check
    chat-to-computer-mcp update install
    chat-to-computer-mcp update status
    chat-to-computer-mcp update enable
    chat-to-computer-mcp update enable --auto-install
    chat-to-computer-mcp update disable

`update check` retrieves npm's stable `latest` version; `update install`
fetches that exact version, compiles the native app locally and verifies that
the executable exists. Version 0.4.3 and newer accept both npm 11 and npm 12
Registry JSON formats and select the exact official package from a private,
manifest-free temporary directory. This prevents a same-named CLI in the
developer's current repository from silently overriding the requested version.
An old CLI from 0.1.0 does NOT have these subcommands:

    npx --yes chat-to-computer-mcp@latest update check
    npx --yes chat-to-computer-mcp@latest update install

To update a globally installed npm CLI separately:

    npm install --global chat-to-computer-mcp@latest

## Native desktop update controls

macOS Settings and Windows Settings have **Check for updates** and
**Install update** buttons, plus:

- **Check automatically**: optional check at launch and about every 12 hours.
- **Install automatically**: further opt-in to compile new published releases
  without interrupting the current application or its AI tasks.

Both are **off by default**. Automatic checks run only while the desktop app is
running, not from an always-on updater daemon.

The CLI and desktop apps share settings, with no credentials in the files:

- macOS: `~/Library/Application Support/ChatToComputerMCP/update-settings.json`
- Windows: `%LOCALAPPDATA%\ChatToComputerMCP\update-settings.json`

## Versioned installation and activation

A successful update is placed in a separate directory:

- Mac: `~/.local/share/chat-to-computer-mcp/releases/<version>/ChatToComputerMCP.app`
- Windows: `%LOCALAPPDATA%\ChatToComputerMCP\releases\<version>\ChatToComputerMCP.exe`

On Mac the installer switches a validated, atomic `releases/current` symlink
only after a complete build. Its dedicated optional LaunchAgent can target
that stable link at the **next login**. An already loaded service is NOT
restarted. Existing launch settings are preserved for the next login.

On Windows the CLI selects the highest successfully installed release.

**Quit the current application and reopen it after your AI tasks finish**
to activate the newly installed native app. Activation refuses while active or queued tasks remain. Independent worker
mode is separate from the updater; no update restarts or replays commands. Use the old retained version directory only after
verifying compatibility with existing local data.

## Security and failure behavior

- Never install an invalid, prerelease or unverified version string.
- Refuse a falsely successful installer that did not create the expected app.
- Preserve the existing version when compilation fails.
- Never export Keychain/Windows Credential Manager secrets.
- If an existing Mac release uses an Apple signing team, the installer first
  tries to reuse the **same locally available signing certificate**. If it is
  missing, ambiguous, or cannot be used, the update fails without switching
  the active release. You can explicitly set
  `CHAT_TO_COMPUTER_CODESIGN_IDENTITY` instead. A different or ad-hoc signing
  team is always rejected before activating the new version, protecting
  Keychain identity.
- Seal and strictly verify macOS .app bundles after building their resources.
  A first-time local build without a prior signing identity uses ad-hoc signing
  (not notarized).
- Never replace, restart or take over the old production FileMCP service.
- Do not add an npm postinstall hook or silently enable LaunchAgent or Shell.
- Native builds can be slow and require Swift/Xcode or .NET SDK as appropriate.

See [Installation](INSTALL_NPM.md) and [Troubleshooting](TROUBLESHOOTING.md).

### Explicit idle activation

After installation, the desktop **Activate update…** action validates the
versioned release, then atomically closes new local tool admission only if
all accepted requests and queued/running commands are finished and no active CUA work/lease remains. A busy
runtime refuses activation; if scheduling the relaunch fails, admission is
restored. The helper waits for the old app to exit before opening the selected
release, and never kills a process. Automatic installation still does not
restart the app. The new app may need an explicit tunnel connection.
