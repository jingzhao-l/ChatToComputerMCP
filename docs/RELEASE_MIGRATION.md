# Compatibility-safe installation and migration

ChatToComputerMCP uses a separate versioned installation directory, executable
and user-login service. Do not overwrite or silently take over a working
FileMCP deployment.

## Current boundaries

| Resource | macOS | Windows |
| --- | --- | --- |
| Product-facing name | ChatToComputerMCP | ChatToComputerMCP |
| Native startup | local.chat-to-computer-mcp LaunchAgent | dedicated user startup supervisor |
| Credential identity | legacy com.localfilesmcp.app for compatibility | ChatToComputerMCP/runtime-api-key |
| Settings/profile | macOS retains legacy bundle defaults and tunnel profile path for continuity | separate ChatToComputerMCP settings and tunnel profiles |
| Update | versioned builds, validated current pointer | versioned builds and verified newest release |

Windows internal FileMCP .NET namespaces do not imply shared runtime
credentials. On Mac, the retained Bundle ID and profile directory do imply
a compatibility boundary: do not run the original and new apps against the
same profile. Independent startup guards are not full credential separation.

## Normal upgrades

Use the official npm channel and [idle activation](UPDATES.md). Preserve the
currently running bundle and OS credential store. The installer does not
export credentials or change ACLs. If a Mac release is signed by an Apple
team, updates must use the same locally available team identity. A failed
build or incompatible signing identity does not activate an upgrade.

## Future independent Mac identity cutover

A separate product Bundle ID and Keychain namespace must be staged and tested
before becoming the default for existing installations. Migration requires
local explicit consent, clear ownership of the legacy configuration, secure
OS-authorized credential access (never credential export), validated new
settings/profile storage and proof that old settings remain intact. Refuse
automatic connection until authorization and the new runtime profile are
verified. Do not move the existing production symlink or launch agent while
tasks are active. A rejected Keychain prompt, failed migration or health check
must leave the old installed version and working connection available.

Acceptance requires signed identity continuity, actual Keychain access,
verified tunnel health and a real client call, workspace and task ownership
checks, and rollback. A namespace rename alone is not a migration.

## Optional CUA upgrades

Normal installs/updates do not automatically download driver or Chromium,
grant application permissions, enable independent Guest, or start a VM.
Existing experimental Guest profiles remain inert until local guest-enable.
Close active leases before activating an update. See [CUA](CUA.md).
