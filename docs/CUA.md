# Background computer use (CUA)

The optional native gateway integrates the pinned upstream [Cua Driver](https://github.com/trycua/cua) 0.61.0 using private, bounded MCP stdio workers. It adds window-scoped GUI operations alongside existing filesystem and shell tools. It does not turn a cloud chat into a persistent autonomous agent or require the desktop GUI to be redesigned.

## Local setup

Use the CLI from the installed package or a development checkout:

    chat-to-computer-mcp cua install
    chat-to-computer-mcp cua status
    chat-to-computer-mcp cua grant --application com.apple.calculator --client shared-connection

An offline official release archive can be supplied with `cua install --archive <file>`; the platform-specific pinned SHA-256 is still required. Installation alone does not enable computer use or grant OS permissions. The Mac driver bundle's signature is verified. The complete upstream release payload and notices are retained.

macOS CUA requires macOS 14+ and user-approved Accessibility/Screen Recording for the responsible native host, as required by the OS. Windows requires an interactive Windows 10/11 desktop; use the exact absolute application executable path instead of a bundle ID. Open the intended app yourself before using CUA. OS permissions must be granted manually; never bypass system prompts.

For individually verified OAuth clients use the actual verified client ID instead of `shared-connection`. Shared tunnel connections do not prove a distinct chat identity: every chat with the shared connection can access its granted applications. The session token must remain private; shared clients cannot enumerate session tokens.

    chat-to-computer-mcp cua revoke --application com.apple.calculator --client shared-connection
    chat-to-computer-mcp cua disable

Disabling removes the local policy and invalidates existing leases. Already-dispatched native actions cannot be undone. Configuration is stored in the user's ChatToComputerMCP application-data directory, separate from shared workspaces.

## MCP workflow

| Tool | Purpose |
| --- | --- |
| cua_capabilities | Local opt-in status, supported operations and identity mode (not an OS permission probe). |
| cua_list_apps | Running applications explicitly granted to this client. |
| cua_list_windows | Windows owned by the approved PID. |
| cua_create_session | Exclusive lease for the exact PID/window; keep session_id private. |
| cua_list_sessions | Own leases for verified clients; shared session IDs are hidden. |
| cua_observe | Observe leased window's accessibility tree and optional image; use returned element tokens or capture-bound coordinates. |
| cua_act | One bounded background action: click, type_text, press_key, scroll, drag, set_value or invoke_menu. |
| cua_verify | Evaluate explicit element predicates in the leased window. Unknown is not success. |
| cua_stop_session | Revoke the lease and stop its worker. |

Example: call cua_capabilities, list approved apps and windows, then create a session with pid and window_id. Observe with session_id and include_screenshot=true. Call cua_act with session_id, operation="click" and parameters containing a current element_token, or x/y and the current capture_id. Observe again before another action. Verify with an expect array, for example an element selector with role, label_contains and exists=true. Stop the session when done.

CUA uses background delivery only, with no automatic foreground fallback. The gateway rejects arbitrary driver tools, desktop scope, target overrides, coordinates outside the latest window image, stale/foreign element tokens and unsupported parameters. Actions consume the current snapshot. Verification is separate; transport success alone does not prove a UI effect.

## Isolation and limits

- Local policy is opt-in and private; the cloud client has no policy-management tool. The driver executable is hash-checked before each lease. Bounded driver manifests permit only the explicitly approved application and required operations; the gateway adds exact-window checks. Driver workers use private pipes, not an exposed daemon.
- Eight simultaneous leases maximum, one per process. Process identity/start time and window ownership are checked again during calls. Mutating calls are refused while the target process is foreground; this is a best-effort user-priority check, not an OS-wide guarantee that every third-party application never activates itself.
- Five-minute idle expiration, bounded call timeout, no automatic action retry/replay. Revocation, process/window loss and native service shutdown retire leases. A short network interruption while the service remains alive does not by itself immediately retire every idle lease. An interrupted action may already have reached the OS: inspect actual state before retrying.
- The CUA grant boundary is not a security sandbox for arbitrary shell access by the same OS user. Existing shell capabilities have their own trust model.
- No full-desktop screenshots, global cursor/keyboard injection, automatic app launching, window geometry changes, foreground fallback, browser-specific or VM automation in this backend. Background support varies by application/OS. Unsupported operations fail rather than falling back to foreground.
- Driver image blocks are returned through MCP; whether a particular cloud chat renders or supplies these images to the model must be tested in that client.

## Validation

Run `bash tests/test_swift_cua.sh`, `python3 tests/check_platform_tool_contracts.py`, `npm test`, and the Windows native test suite. The stdio fixture is not a real desktop acceptance test.

The initial development verification uses the real signed macOS arm64 0.61.0 driver for handshake and bounded refusal tests, without attempting user-desktop input or granting OS permissions. Windows cross-target compilation is not native Windows acceptance. Real background action coverage and ordinary Chat client image/action round trips require the updated gateway, a permitted test window, and the relevant desktop/client.
