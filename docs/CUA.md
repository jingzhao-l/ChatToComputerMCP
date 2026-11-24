# Background computer use (CUA)

ChatToComputerMCP integrates the pinned upstream [Cua Driver](https://github.com/trycua/cua) 0.61.0 using private bounded MCP stdio workers on macOS and Windows. It also supports an optional pinned Playwright 1.65.1 headless browser and a dedicated guest CUA relay. These tools can be composed with ordinary workspace, shell and coding tools in the same chat. They do not implement a separate autonomous agent or require GUI changes.

## Native setup

    chat-to-computer-mcp cua install
    chat-to-computer-mcp cua status
    chat-to-computer-mcp cua grant --application com.apple.calculator --client shared-connection
    chat-to-computer-mcp cua grant --application com.apple.calculator --client shared-connection --allow-launch

The separate launch grant is optional. Windows uses the exact absolute application executable path rather than a bundle identifier. macOS requires 14+ and manual Accessibility/Screen Recording permissions for the responsible native host; Windows requires an interactive Windows 10/11 session. Installation alone does not grant OS permissions. An official offline archive can be supplied with `cua install --archive <file>`; pinned platform SHA-256 and Mac bundle signature checks remain mandatory.

For individually verified OAuth clients, use the verified client ID. Shared tunnel connections do not establish distinct chat identities: all chats sharing that connection have its grants. Session IDs are private capabilities; shared clients cannot enumerate them.

    chat-to-computer-mcp cua revoke --application com.apple.calculator --client shared-connection
    chat-to-computer-mcp cua disable

Local policy changes invalidate leases. Already dispatched actions cannot be undone. Policy and receipt storage are private application data, separate from workspace files.

## MCP tools

| Tool | Purpose |
| --- | --- |
| cua_capabilities | Opt-in status, identity, platform capabilities and permission status where supported. |
| cua_list_apps | Approved running applications and separately approved launch targets. |
| cua_launch_app | Launch an explicitly approved application; separate launch grant and unique request_id required. |
| cua_list_windows | Windows of the approved PID. |
| cua_create_session | Exclusive PID/window lease. |
| cua_list_sessions | Own leases for verified clients; shared session IDs are hidden. |
| cua_observe | Window accessibility tree and optional image; return current element and capture tokens. |
| cua_act | Background click, type_text, press_key, scroll, drag, set_value, invoke_menu or set_window_frame. |
| cua_verify | Evaluate explicit element predicates; unknown is not success. |
| cua_receipt_status | Owner-bound durable action dispatch receipt. |
| cua_stop_session | Revoke lease and stop worker. |
| cua_extension_list | List locally granted browser and guest profiles. |
| cua_extension_open | Open an isolated browser or guest lease; unique request_id required. |
| cua_extension_call | Perform a supported operation in an existing extension lease. |
| cua_extension_close | Close extension lease and retire worker. |

List approved apps/windows, create a session for the selected PID and window, observe, and act using current element tokens or coordinates tied to the current capture_id. Every cua_act requires a unique unpredictable 32–128-character request_id. Observe again before another action; snapshots expire after 30 seconds. Verify the actual UI state separately. A durable receipt with state exited confirms a successful dispatch response, not that the intended UI effect occurred. After interruption, query the original receipt and inspect actual state; never replay the same request ID or blindly retry with a new one.

## Isolated browser

    chat-to-computer-mcp cua install
    chat-to-computer-mcp cua extension-install
    chat-to-computer-mcp cua browser-grant --profile web --origin https://example.com --client shared-connection

The optional runtime installs locked Playwright dependencies and their corresponding Chromium build. Repeat --origin to explicitly grant each additional HTTP(S) origin. Use cua_extension_open with profile_id and request_id, then cua_extension_call with session_id, operation, parameters and a unique request_id for mutations. Supported browser operations are navigate, back, forward, observe, click, fill, press, select, scroll, hover and verify. Observe returns visible top-level elements and optional PNG; actions use current references. Verify may check expected text or URL. Close the session when finished.

Each lease owns a fresh headless browser context. It does not attach to the user's browser, use its login state or move its cursor. The worker uses a private loopback HTTP/HTTPS origin allowlist proxy plus browser request interception, including redirects. It is not an OS network sandbox. Arbitrary JavaScript, service workers, WebSockets, popups, arbitrary file uploads and downloads are not exposed. Multi-tab and cross-frame browser control are not implemented. Browser-specific limits fail closed.

## Dedicated guest execution

Use a separately provisioned macOS or Windows VM or other independently managed desktop. The project does not create VM images, install guest operating systems or attest that the supplied endpoint is hardware-isolated.

Inside the dedicated guest, install the native gateway and CUA driver, grant only the desired apps, then configure and run the CUA-only listener:

    chat-to-computer-mcp cua install
    chat-to-computer-mcp cua grant --application com.apple.calculator --client shared-connection --allow-launch
    chat-to-computer-mcp cua guest-init --port 18765 --confirm-isolated-desktop
    chat-to-computer-mcp cua guest-serve

The confirmation is an operator assertion, not VM attestation. The listener binds only to guest loopback. It exposes native CUA tools only, not shell, filesystem or nested extension tools. Configure the required OS permissions and interactive desktop in the guest. Securely transfer only its dedicated generated credential file from application data to a private location on the host (not an approved shared workspace). Establish a trusted local forwarding transport, for example SSH with verified guest host identity:

    ssh -N -L 127.0.0.1:18766:127.0.0.1:18765 guest-host

On the host:

    chat-to-computer-mcp cua extension-install
    chat-to-computer-mcp cua guest-grant --profile vm --port 18766 --token-file /private/path/guest-token --client shared-connection

Open the guest profile with cua_extension_open. Call cua_extension_call using a native guest CUA tool name as operation and its arguments as parameters. Guest session IDs are bound to the outer lease and cannot be guessed or borrowed; actions and launch use unique inner request IDs in addition to outer mutation IDs. Only the bounded CUA allowlist is forwarded. One lease per guest endpoint; close on completion.

For an existing Lume (Mac) or Hyper-V (Windows) VM, add --vm-provider lume or hyper-v and --vm-name with the exact VM name when granting the profile. Local CLI `cua vm status|start|stop --profile vm` manages that pre-existing VM. Boot/login, installation, credential transfer and forwarding are operator tasks; the remote chat does not get unrestricted hypervisor control. Revoke a profile with `cua profile-revoke --profile vm --client shared-connection`.

## Safety and lifecycle

- Native background delivery only; no automatic foreground fallback or global keyboard/cursor injection. Host mutations are refused when the target is foreground. Application behavior varies; no OS-wide no-activation guarantee. An independent guest may have its own foreground window without affecting the host.
- Exact process identity/start time and window ownership, current snapshot references, bounded parameters, per-owner grants, private workers, eight total simultaneous native/browser/guest/launch slots, exclusive native PID and guest endpoint leases. No automatic replay.
- Five-minute idle expiry; bounded transport timeout, request cancellation where peer disconnect is observable, policy revocation, process loss and service shutdown retire the relevant leases. Short idle network interruption alone is not necessarily a lease revocation. A request already dispatched to an OS may have taken effect despite a lost response.
- Durable receipt tombstones are persisted before mutation dispatch and retained across service restarts. They record dispatch state, not successful UI verification.
- CUA grants are not a sandbox for unrestricted same-user shell access. Driver image blocks travel through MCP; a cloud client's multimodal rendering still needs client acceptance.

## Regression versus formal acceptance

Mac and Windows have matching tool definitions and native policy, snapshot and stdio fixtures. The pinned signed Mac driver handshake and bounded refusals are exercised separately. The P4 regression uses real Chromium through a native HTTP MCP server and two native processes for guest-only transport; it is not a physical VM test.

Run `bash tests/test_swift_cua.sh`, `python3 tests/check_platform_tool_contracts.py`, `npm test` and the Windows native test suite. To run the real browser regression, install the locked dependencies with npm ci --prefix cua-runtime --ignore-scripts, install Chromium using PLAYWRIGHT_BROWSERS_PATH set to cua-runtime/browsers, then run tests/test_swift_cua_p4.sh on Mac or tests/test_cua_p4.mjs with CTCMCP_CUA_TEST_SERVER set to the Windows test apphost. Both platform CI jobs include this regression.

Formal acceptance remains: real granted app actions/launch/window geometry on macOS and Windows, actual independent guest/VM transport and lifecycle, foreground non-interference across apps, and ordinary cloud Chat image/action round trips. Cross-target builds and two-process guest tests do not establish those results.
