# Background computer use (CUA)

ChatToComputerMCP integrates the pinned upstream [Cua Driver](https://github.com/trycua/cua) 0.61.0 using private bounded MCP stdio workers on macOS and Windows. It also supports an optional pinned Playwright 1.65.1 headless browser and a dedicated guest CUA relay. These tools can be composed with ordinary workspace, shell and coding tools in the same chat. They do not implement a separate autonomous agent or require GUI changes.

## Execution priority and independent Guest opt-in

Direct native background window control is the primary computer-use path.
An independently authorized headless browser is appropriate for web-only work.
There is no automatic foreground fallback and no automatic Guest/VM fallback.

Independent Guest is **disabled by default**, including on upgrades with saved
guest profiles, credentials, VM registrations or dedicated relay definitions.
The local user must explicitly enable it in an interactive terminal:

    chat-to-computer-mcp cua guest-enable
    chat-to-computer-mcp cua guest-status
    chat-to-computer-mcp cua guest-disable

Type ENABLE at the local interactive prompt to confirm activation. Noninteractive
CLI activation is refused and there is no remote MCP activation tool. Enabling
does not create/start a VM, listener or forward and does not grant application
permissions. The independent private approval persists until the user disables
it. Enable separately on the host and on a dedicated Guest that runs the
guest-only listener. Until then, Guest provisioning/start, initialization,
registration, connection and serving commands refuse execution; saved Guest
profiles are omitted from remote extension discovery.

Disabling removes the approval, revokes Guest leases, and stops managed Guest
and relay listeners/SSH forwards on that computer. Re-enabling uses a fresh
approval epoch and cannot revive an old lease or restart a listener. Native
window CUA and headless browser leases are unaffected. Existing VM disks and
configuration remain intact, and separately running VMs are not powered off.
Status, stop and revocation operations remain available for cleanup. In-flight
actions already dispatched to another machine cannot be undone by revocation.

The capability response reports guest_enabled and preferred_execution; this
does not create a conversation identity. The interactive local switch and
private state file are safeguards against normal MCP/CLI use, not a security
boundary against arbitrary host code running as the same OS user. For
adversarial shell access, enforce a separate OS security boundary as described
in the verified-client and workspace security documentation.

## Native setup

    chat-to-computer-mcp cua install
    chat-to-computer-mcp cua status
    chat-to-computer-mcp cua grant --application com.apple.calculator --client shared-connection
    chat-to-computer-mcp cua grant --application com.apple.calculator --client shared-connection --allow-launch

The separate launch grant is optional. Windows uses the exact absolute application executable path rather than a bundle identifier. macOS requires 14+ and manual Accessibility/Screen Recording permissions for the responsible native host; Windows requires an interactive Windows 10/11 session. Installation alone does not grant OS permissions. An official offline archive can be supplied with `cua install --archive <file>`; pinned platform SHA-256 and Mac bundle signature checks remain mandatory.

For individually verified OAuth clients, use the verified client ID. Shared tunnel connections do not establish distinct chat identities: all chats sharing that connection have its grants. Session IDs are private capabilities; shared clients cannot enumerate them. To forbid shared-connection CUA entirely, locally run:

    chat-to-computer-mcp cua require-verified enable

This invalidates existing leases, rejects shared-connection requests and grants, and requires a verified OAuth client. The dedicated guest-only listener remains restricted to its own credential. This is verified **client** isolation, not verification of a cloud conversation identity. Capability responses explicitly report `conversation_identity_verified: false`. Do not enable this mode expecting a shared-only tunnel to continue working. See [identity boundaries](VERIFIED_CLIENT_ACCESS.md).

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
| cua_create_session | Exclusive PID/window lease; optional previous_session_id transfers an idle owned lease to another window of the exact same running process. |
| cua_list_sessions | Own leases for verified clients; shared session IDs are hidden. |
| cua_observe | Window accessibility tree and optional image; return current element and capture tokens. |
| cua_act | Background click, type_text, press_key, hotkey, scroll, drag, set_value, invoke_menu or set_window_frame. |
| cua_verify | Evaluate explicit element predicates; unknown is not success. |
| cua_receipt_status | Owner-bound durable action dispatch receipt. |
| cua_stop_session | Revoke lease and stop worker. |
| cua_extension_list | List locally granted browser and guest profiles. |
| cua_extension_open | Open an isolated browser or guest lease; unique request_id required. |
| cua_extension_call | Perform a supported operation in an existing extension lease. |
| cua_extension_close | Close extension lease and retire worker. |

List approved apps/windows, create a session for the selected PID and window, observe, and act using current element tokens or coordinates tied to the current capture_id. Every cua_act requires the observation_id returned by the latest cua_observe of that exact lease, plus a unique unpredictable 32–128-character request_id. The gateway observation nonce prevents cross-worker snapshot number collisions. Observe again before another action; snapshots expire after 30 seconds. Verify the actual UI state separately. A durable receipt with state exited confirms a successful dispatch response, not that the intended UI effect occurred. After interruption, query the original receipt and inspect actual state; never replay the same request ID or blindly retry with a new one.

### Native compatibility and recovery

Use cua_observe with query (case-insensitive, up to 512 bytes), max_elements
(1–1500), max_depth (1–50), max_image_dimension (320–1920), and timeout_ms
(100–4000) to inspect complex applications without unbounded tree walks.
A truncated tree is partial evidence, not proof that a control is missing.
For custom-rendered applications, pass include_accessibility_tree:false to
request a screenshot without walking the accessibility tree; pass
include_screenshot:false for tree-only inspection. At least one is required.
The gateway reports observation_only, pixel_actions_available, and
element_actions_available. A screenshot without a valid capture ID is display
evidence only, never a pixel action grant. The gateway independently checks
that window geometry is stable during capture and immediately before
coordinate dispatch; movement, resize, minimization, or a Windows DPI change
invalidates previous pixel coordinates. Tree-only actions remain available
when the driver supplies an actionable tree. Explicit native session closure
waits for cleanup rather than acknowledging release while a worker is retiring.

Native controls should use accessibility tokens first; use pixel coordinates
only with a current capture ID. Keyboard shortcuts are routed only through
the pinned driver's background mode. Global app-switch shortcuts are refused.
Supported modifiers are platform-specific (Mac cmd/shift/option/alt/ctrl;
Windows ctrl/shift/alt). The driver may refuse controls requiring real
foreground input; no escalation is performed. Window verification supports
bounds with tolerance_px (0–100) and stable_samples (1–5). An action receipt
whose state is interrupted does not prove that the action was not delivered.

For dialogs and additional application windows, inspect the returned
new_window_ids / closed_window_ids after a dispatch and confirm with
cua_list_windows. Windows also reports owner_window_id (same process only)
and enabled status. Use cua_create_session with the same pid, the new
window_id, and previous_session_id set to your current lease. Transfer is
atomic: no competing process lease is admitted, the old bearer is revoked,
and the returned session requires a new observation. A closed idle window
does not immediately release the process lease while the approved process
is still alive, allowing explicit transfer to a remaining window. If the
process exits, policy changes, or an uncertain action retires the lease,
create a new session only after inspecting actual state. If the transfer
reply is lost, verified clients can enumerate their own lease; shared
connections may need to wait for idle expiry. No other process or client
can be selected by the transfer operation.

Known typed driver refusals (background_unavailable, background_occluded,
background_uipi_blocked with effect=refused, or option_not_found with
effect=none) return action_outcome=refused and keep the idle lease. A fresh
observation and new request ID are required before any further input.
This is the pinned driver's no-effect report, not independent verification;
the gateway does not follow the driver's foreground escalation advice.
Unrecognized failures, interrupted strokes, and uncertain transport
outcomes retire the lease and record interrupted. Never blindly retry.
Successful dispatches report dispatched_unverified, not a verified UI effect.

Both native workers explicitly start the pinned driver with `mcp --direct` and never attach to an existing daemon. Host mutations and launches latch foreground continuity: changed or unknown foreground aborts the current dispatch and never restores user focus. macOS additionally watches workspace activation notifications; Windows checks the exact foreground HWND. Sampling/notification delivery can miss short transitions and cannot prevent all app-internal activation, so this is detection and refusal, not a universal no-interference guarantee.

Each native worker verifies the driver's version and advertised tools/list
contract before sending an action. Unsupported tools/parameters are refused
rather than guessed; Windows can omit an unavailable upstream revision
parameter but keeps the gateway's one-use snapshot and exact PID/window
binding. A closed/exited worker's lease is retired, and idle cleanup rechecks
admission atomically. Do not retry an uncertain mutation automatically.
Background does not establish zero interference for every third-party
application; app compatibility still requires real UI acceptance.

## Isolated browser

    chat-to-computer-mcp cua install
    chat-to-computer-mcp cua extension-install
    chat-to-computer-mcp cua browser-grant --profile web --origin https://example.com --client shared-connection

The optional runtime installs locked Playwright dependencies and their corresponding Chromium build. Repeat --origin to explicitly grant each additional HTTP(S) origin. Use cua_extension_open with profile_id and request_id, then cua_extension_call with session_id, operation, parameters and a unique request_id for mutations. Supported browser operations include navigate, back, forward, observe, click, fill, press, select, scroll, hover, verify, wait_for, tab_list, tab_open, tab_select, tab_close, dialog_prepare, drag, upload and download. Observe returns visible elements in permitted frames and optional PNG; actions use current tab/frame references. Navigation or tab changes invalidate stale references. Verify and bounded wait may check expected text or URL. Dialog preparation is one-shot and requires the exact expected dialog type and message. Close the session when finished.

Each lease owns a fresh headless browser context. It does not attach to the user's browser, use its login state or move its cursor. The worker uses a private authenticated loopback HTTP/HTTPS origin allowlist proxy plus browser request interception, including redirects. It is not an OS network sandbox. Arbitrary JavaScript, service workers and WebSockets are not exposed. Tab and frame control is bounded to granted origins. File operations are disabled by default; add `--allow-files` when granting the browser profile to permit bounded in-memory upload (16 KiB) and download results (128 KiB), without exposing arbitrary local paths. Downloads use temporary browser storage, not a general filesystem quota. Browser-specific limits fail closed. The installed worker directory is keyed by its source hash; install a new runtime explicitly and re-grant profiles when updating it.

## Dedicated guest execution

Only when the user explicitly requests independent Guest, first use the local guest-enable command on the host and the guest itself. Use a separately provisioned macOS or Windows VM or other independently managed desktop. The local-only provisioning command can create a VM from an existing Lume template or a detached standalone Hyper-V Gen2 VHDX:

    chat-to-computer-mcp cua vm provision --provider lume --template clean-template --name cua-guest
    chat-to-computer-mcp cua vm provision --provider hyper-v --template C:\VMs\clean.vhdx --name cua-guest --destination C:\VMs --switch Private

The Hyper-V path copies the disk into a new destination rather than attaching the source. It refuses existing names/destinations and differencing or attached source disks. Provisioning failure may leave a new partial VM or disk for local operator inspection; it never deletes existing resources. Templates must be trusted and prepared for the target hypervisor, without reusable CUA tokens, cloned SSH host identity or shared personal login credentials. Install/configure a unique guest OS identity, credentials, desktop and SSH server before registering the guest. The project does not install an OS from scratch, bypass OS setup/consent or attest that the supplied endpoint is hardware-isolated.

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

For an already registered guest profile, the local-only helper can establish the forward using a pre-verified known_hosts file and an explicit SSH identity:

    chat-to-computer-mcp cua guest-connect --profile vm --host guest.example --user guest --guest-port 18765 --known-hosts /absolute/known_hosts --identity /absolute/ssh_identity

It uses strict host-key checking, batch authentication, no agent/X11 forwarding, and binds exclusively to loopback. Run it in a dedicated foreground terminal; it does not install keys or auto-accept host identities. The native relay separately checks the guest token. Lume headless mode suppresses the local viewer, not necessarily the provider VNC server; configure the hypervisor network separately.

Open the guest profile with cua_extension_open. Call cua_extension_call using a native guest CUA tool name as operation and its arguments as parameters. Guest session IDs are bound to the outer lease and cannot be guessed or borrowed; actions and launch use unique inner request IDs in addition to outer mutation IDs. Only the bounded CUA allowlist is forwarded. One lease per guest endpoint; close on completion.

For an existing Lume (Mac) or Hyper-V (Windows) VM, add --vm-provider lume or hyper-v and --vm-name with the exact VM name when granting the profile. Local CLI `cua vm status|start|stop --profile vm` manages that pre-existing VM. Boot/login, installation, credential transfer and forwarding are operator tasks; the remote chat does not get unrestricted hypervisor control. Revoke a profile with `cua profile-revoke --profile vm --client shared-connection`.

## Safety and lifecycle

- Native background delivery only; no automatic foreground fallback or global keyboard/cursor injection. Host mutations are refused when the target is foreground. Application behavior varies; no OS-wide no-activation guarantee. An independent guest may have its own foreground window without affecting the host.
- Exact process identity/start time and window ownership, current snapshot references, bounded parameters, per-owner grants, private workers, eight total simultaneous native/browser/guest/launch slots, exclusive native PID and guest endpoint leases. No automatic replay.
- Five-minute idle expiry; bounded transport timeout, request cancellation where peer disconnect is observable, policy revocation, process loss and service shutdown retire the relevant leases. Short idle network interruption alone is not necessarily a lease revocation. A request already dispatched to an OS may have taken effect despite a lost response.
- Durable receipt tombstones are persisted before mutation dispatch and retained across service restarts. An exclusive interprocess state lock prevents two native server instances from operating the same CUA receipt authority concurrently. They record dispatch state, not successful UI verification.
- CUA grants are not a sandbox for unrestricted same-user shell access. Driver image blocks travel through MCP; a cloud client's multimodal rendering still needs client acceptance.

## Regression versus formal acceptance

Mac and Windows have matching tool definitions and native policy, snapshot and stdio fixtures. The pinned signed Mac driver handshake and bounded refusals are exercised separately. The P4 regression uses real Chromium through a native HTTP MCP server, exercises tabs, cross-frame references, dialogs, bounded file artifacts, drag, wait-for, receipt replay, strict verified-client denial and process-exclusive state, and uses two native processes for guest-only transport; it is not a physical VM test.

Run `bash tests/test_swift_cua.sh`, `python3 tests/check_platform_tool_contracts.py`, `npm test` and the Windows native test suite. To run the real browser regression, install the locked dependencies with npm ci --prefix cua-runtime --ignore-scripts, install Chromium using PLAYWRIGHT_BROWSERS_PATH set to cua-runtime/browsers, then run tests/test_swift_cua_p4.sh on Mac or tests/test_cua_p4.mjs with CTCMCP_CUA_TEST_SERVER set to the Windows test apphost. Both platform CI jobs include this regression.

Formal acceptance remains: real granted app actions/launch/window geometry on macOS and Windows, actual independent guest/VM transport and lifecycle, foreground non-interference across apps, and ordinary cloud Chat image/action round trips. Cross-target builds and two-process guest tests do not establish those results.

## Dedicated relay execution domains (no host tool fallback)

For high-isolation CUA, allocate a dedicated guest VM per trust domain. Do not
share guest disks, credentials, desktop, host folders, clipboard or device
passthrough between mutually untrusted domains. Put the entire development
gateway inside its own VM if it needs unrestricted native build tools. The
relay below deliberately exposes only guest CUA, not general guest shell.

After guest-init, guest-serve and authenticated SSH forwarding as above, reserve
a different local listener port and a unique guest credential for each domain:

    chat-to-computer-mcp cua relay-init --domain project-a --port 18801 --guest-port 18766 --token-file /absolute/private/guest-a-token --confirm-dedicated-guest
    chat-to-computer-mcp cua relay-serve --domain project-a
    chat-to-computer-mcp cua relay-status --domain project-a

Repeat with a different domain, listener port, forwarded port and guest
credential for another independent VM. Do not register the same guest with a
general-purpose host gateway. The local registry rejects duplicate relay ports
and guest credential digests. Each relay owns a private configuration,
independent random listener credential, receipt store, session authority and
exactly one approved Guest profile. Relay policy is validated again on each
operation. Adding host grants or additional profiles fails closed.

Connect each relay listener to its **own** trusted MCP transport/connector.
For Secure MCP Tunnel, create a distinct tunnel and local tunnel-client profile
targeting that relay's loopback port; configure its local authentication header
from the relay's private token file through the transport's environment secret
configuration. Do not paste the token into Chat, expose the listener publicly,
or reuse the ordinary gateway's credentials. The CLI provisions the local relay;
it does not create Platform tunnels or assign connector access automatically.

The relay permits only cua_capabilities, cua_extension_list/open/call/close and
cua_receipt_status. Tool discovery and direct dispatch enforce the same
immutable allowlist. No host shell, workspace/file/Git calls, wrapper routes,
host CUA or headless-browser fallback. Use cua_capabilities to check
relay_only=true, host_tools_allowed=false and identity=dedicated_relay_credential.
The conversation_identity_verified field remains false.

This is independently authenticated **execution-domain** access, not native
Chat conversation attestation. Anyone authorized to use a relay credential
shares that domain. Do not expose a privileged general-purpose host connector
to an untrusted caller at the same time. A trusted local operator must verify
that the guests and network forwarding targets really are distinct and
isolated; the relay cannot remotely attest a VM's hypervisor. Existing
same-OS-user processes with unrestricted host privileges are outside this
boundary. The dual-relay HTTP regression verifies default-off admission, opt-in revocation, epoch rotation, browser lease continuity, cross-credential denial,
foreign lease rejection, host-tool/wrapper refusal and policy-widening denial
using two independent guest listeners, not two physical VMs.
