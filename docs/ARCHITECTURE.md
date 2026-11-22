# Architecture

ChatToComputerMCP is a local-first native MCP workspace gateway. The application and its tools run on the user's Mac or Windows computer; models and cloud chat clients remain outside it.

## Components and trust boundaries

```text
Remote AI conversation / MCP client
        │
        │ approved tunnel; authentication depends on transport
        ▼
Outbound OpenAI Secure MCP Tunnel
(or Cloudflare connection on supported macOS setups)
        │
        ▼
Local loopback HTTP MCP endpoint
        │
        ├── tools/list and tools/call
        │        │
        │        ├── default workspace tools
        │        └── workspace_call → approved scoped tools
        │
        └── local desktop UI, logs and task controls
                              │
                        OS file system
```

The tunnel health/admin listener also binds to loopback, not a public interface. Remote callers must not be allowed to add workspaces or rewrite permission settings through MCP.

## Workspace catalog

- The **default** workspace is the compatibility scope for direct tool calls.
- The user may approve up to **12 additional roots** in the native UI.
- A scope is a stable ID, friendly name and canonical path.
- Root validation rejects invalid, overlapping or symlink-aliased folders.
- `list_workspaces` exposes the approved catalog to authenticated MCP callers.
- `workspace_call` routes an existing enabled tool to an approved non-default scope.
- Workspace additions/revocations are configured locally and take effect when the runtime reconnects.

The catalog is not per-AI-client authorization. The current shared connection identity may expose the same approved scopes to different remote conversations.

## Commands and task lifecycle

The default 8 concurrent slots can be configured within the 4–16 range. Sync and long-running commands share the admission budget across all scopes. Overlapping working-directory trees are rejected; independent subtrees can run simultaneously.

Long-running sessions carry IDs, output cursors, completion states, bounded output and targeted cancellation. A cancelled task must not automatically cancel a sibling in another independent cwd.

**Important limitations**

1. A cwd guard is cooperative. A user-privileged shell may still open absolute paths or spawn unrestricted child processes.
2. The current active session list is in the local runtime, not an authenticated cross-restart task supervisor. An app crash may interrupt running commands.
3. Caller-provided MCP `clientInfo` is not proof of a security identity. No per-client cryptographically verified scope grants are promised.

## Native applications

| Platform | Implementation | Current verification |
|---|---|---|
| macOS | AppKit/Swift app, loopback MCP, Secure MCP Tunnel, optional Cloudflare | Native builds, UI-state and MCP integration tests on Mac |
| Windows | WPF/.NET 8 app, native Windows APIs and Credential Manager, Secure MCP Tunnel | Windows-targeting compilation on Mac; real Windows execution still required |

Windows package paths still use the historical FileMCP .NET namespace/project names for source compatibility. The product-facing name and executable are ChatToComputerMCP.

## Local installation and background lifecycle

The npm wrapper contains application sources and pinned vendor tools. CLI `install` compiles into a versioned per-user release path, not npm's own executable directory. There is **no automatic postinstall** process.

On Mac, an optional dedicated `local.chat-to-computer-mcp` LaunchAgent may start the app after user login and restart it on abnormal exit. The `--always` policy additionally restarts normal exits. Tunnel auto-connect requires a separate opt-in; it is refused while the original `local.filemcp` service is active.

For credentials and safe migration, see [RELEASE_MIGRATION.md](RELEASE_MIGRATION.md) and [SECURITY.md](../SECURITY.md).

## Verification evidence and pending work

The 0.1.0 release has been verified with local Mac Swift/UI tests, npm tarball content checks and an isolated npm-source build/install. Windows cross-target compilation passed, **not** Windows real-device acceptance. Two separate security/reliability milestones remain:

- [Verified client identities and per-client grants](https://github.com/jingzhao-l/ChatToComputerMCP/issues/1)
- [Durable supervisor and recovery after restart](https://github.com/jingzhao-l/ChatToComputerMCP/issues/2)
