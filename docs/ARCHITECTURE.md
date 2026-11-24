# Architecture

ChatToComputerMCP is a local-first native MCP gateway. Models and cloud
conversations remain external. All file, command and optional window actions
run on the explicitly approved local computer.

## Components and trust boundaries

```text
Remote cloud conversation / MCP client
    | OpenAI Secure MCP Tunnel (shared principal)
    | or Cloudflare HTTPS + OAuth (verified client principal)
    v
Loopback HTTP MCP gateway (macOS Swift / Windows C#)
    |-- identity and authorization; bounded HTTP admission
    |-- approved workspace catalog and direct workspace_id routing
    |     |-- per-root file, code search and safe Git
    |     |-- shared command budget, FIFO, cancel/output/receipts
    |     '-- optional host Shell or restricted Linux container
    |-- opt-in independent workers and live reattachment
    |-- optional native background CUA (local app grants)
    |     '-- separately granted isolated browser / explicit Guest
    '-- native GUI, local history, audit and update admission
```

Local MCP and tunnel health/admin listeners bind to loopback. Both platform
HTTP implementations cap accepted active sockets at 32 and bound complete
request receipt and response writing independently to 15 seconds. A tool
execution has its own lifecycle and timeout; it is not limited by HTTP
framing timeouts. Shutdown closes tracked sockets.

## Workspace and command lifecycle

One default plus up to 12 named roots are locally approved, canonical and
mutually disjoint. Omitting workspace_id selects the default; each scoped
tool accepts workspace_id directly, with workspace_call retained as a
compatibility wrapper. An idle additional root can be modified live.
Default-root/global changes require reconnect, and OAuth policy changes
invalidate prior tokens. Remote callers cannot approve new scopes.

The 4–16 (default 8) shared command budget covers both synchronous and
asynchronous jobs. FIFO queue admission is opt-in. Overlapping cwd trees and
mutations are guarded cooperatively; host Shell still has user-level OS
permissions. Optional restricted Linux containers need a trusted, locally
approved Docker image and engine; no fallback to host Shell.

Durable request tombstones precede dispatch; command receipts are scoped to
the authenticated connection principal. Ordinary history is local and
content capture defaults off. Explicitly enabled independent workers can
reattach a demonstrably live worker after a gateway restart, preserving the
original session and capacity reservation. Worker death, reboot, and
unlaunched queue entries are interrupted, not replayed. See
[worker lifecycle](INDEPENDENT_TASKS.md) and [workspace security](MULTI_WORKSPACE_SECURITY.md).

## Identity and authorization

Cloudflare OAuth uses locally approved client IDs, server-issued tokens,
PKCE, workspace grants, owner-scoped task access and revocation on both
platforms. The shared OpenAI Tunnel local authentication header does not
verify the origin of a particular conversation. A trusted client or cloud
platform must supply authenticated conversation claims. A lease or request
ID cannot substitute for a principal. See [verified access](VERIFIED_CLIENT_ACCESS.md).

## Native implementations

| | macOS | Windows |
| --- | --- | --- |
| Desktop | AppKit / Swift | WPF / .NET 8 |
| CPU targets | Apple Silicon, Intel | x64, ARM64 |
| Credential store | Keychain | Credential Manager / private OAuth state |
| User login supervision | launchd LaunchAgent | HKCU Run + user supervisor |
| Native process containment | process groups | Job Objects |
| Gateway interface | Shared base JSON Schema contract | Shared base JSON Schema contract |

Both GUIs have seven areas. The base tool interface is governed by the
[shared machine-readable contract](../contracts/mcp-tools.json) and
[generated inventory](BASE_TOOL_CONTRACT.md). Native implementation fixtures
compare schemas, annotations and permission visibility; actual runtime
regressions are separate. Windows historical .NET project names remain
internal implementation details.

## Installation, recovery and updates

The npm package provides tracked native source and pinned vendor binaries,
compiled locally; no postinstall starts services or grants permissions.
A new version is installed separately. Both GUI/CLI update paths share
preferences. Explicit activation closes new admission only when idle;
running/queued tasks and active CUA leases block activation. User-login
startup and reconnect are opt-in. No pre-login service or hidden updater
daemon is installed. macOS retains the legacy bundle/Keychain namespace for
credential continuity until explicit migration is complete. See
[updates](UPDATES.md), [migration](RELEASE_MIGRATION.md) and
[platform parity](PLATFORM_PARITY.md).

## Optional CUA

The host owns application grants, exact process/window leases, bounded
pinned driver subprocesses, one-use observations and durable action receipts.
The headless browser uses separate origin grants. Guest-only relays are
default-off until explicit local opt-in and never expose host workspace or
Shell tools. No automatic foreground/Guest fallback. Details and acceptance
limits are maintained in [CUA](CUA.md).
