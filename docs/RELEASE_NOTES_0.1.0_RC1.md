# ChatToComputerMCP 0.1.0 RC1 — source installer preview

This is a private GitHub prerelease intended to validate the source-only
npm package, local compilation, native GUI, and optional macOS LaunchAgent.
It is not yet a public npm Registry release or an Apple-notarized distribution.

## Implemented

- macOS AppKit with Overview, Workspaces, Connection, Tasks and Activity pages.
- Multiple explicitly approved disjoint workspaces; scoped MCP operations;
  configurable global 4–16 command capacity and targeted cancellation.
- Windows WPF GUI, approved workspace routing, shared capacity and Tasks page.
  Mac-hosted Windows-targeting .NET 8 build passed with zero warnings/errors.
- npm installer compiles native application from package source on user device;
  previous releases retained, no automatic service or shell permission enabled.
- Separate optional LaunchAgent local.chat-to-computer-mcp: login startup,
  restart-on-failure, optional always-on and optional auto-connect.
- Apache-2.0 license, upstream attribution and pinned vendor licensing.

## Tests actually performed

- Mac native build and 150 GUI layout cases.
- Mac HTTP/MCP tool integration, legacy/modern protocols, OAuth, cancellation.
- Source tarball audited: required Mac/Windows/vendor and license files; no
  generated Windows bin/obj, npm auth data, or embedded credentials.
- npm CLI Node tests, including installation directory regression, passed.
- Clean sandbox HOME compiled and installed from extracted npm tarball; CLI
  status reports installed=yes with executable at correct app bundle path.
- LaunchAgent preview verified; conflicting auto-connect with active original
  FileMCP refused without writing a plist.

## Outstanding release gates

- npm Registry publishing requires npm account auth. npm whoami is HTTP 401,
  so npx @latest is not available from the public registry yet.
- Windows real-device tests not executed. GitHub Actions jobs fail BEFORE
  starting, with annotation saying recent account payments failed or the
  account spending limit needs to be increased. Billing & plans must be fixed.
- Per-AI-client verified identities, fine-grained client authorization and
  truly durable process reattachment after application restart are not yet
  implemented; an unverified client-info name must never grant access.
- Public Mac Developer ID signing/notarization remain pending.

## Production safety

The active Mac FileMCP app, Keychain, proxy, existing tunnel profile and
local.filemcp LaunchAgent remain unchanged. The new background service is
opt-in and auto-connect off by default. This prerelease must not be mistaken
for a tested in-place production upgrade.
