# Native GUI capabilities and design guide

Both native apps now use seven functional areas. macOS adapts between a
wide sidebar and compact navigation; Windows uses the same wide/compact navigation pattern. Both support explicit light/dark selection, remembered window geometry, startup cancellation, and diagnostic log follow/copy/clear.

| Area | Controls |
|---|---|
| Overview | Connection and workspace summary, quick actions |
| Workspaces | Default and additional approved roots, scoped permissions, capacity |
| Connection | Provider, credentials, OAuth pairing, verified client grants and revocation |
| Tasks | Live short/long tasks, queued jobs, scoped cancellation, bounded output, durable history |
| Activity | Redacted diagnostics and private structured audit |
| Security | Global permission limits, history content opt-in, independent worker opt-in |
| Settings | Profile, ports, login behavior, updates and advanced preferences |

Shell, remote Git push, history content and independent workers are separate
opt-in permissions. Per-workspace restrictions include optional Linux
container execution. The user may alter idle additional workspaces live;
changes to global permissions require reconnection. The receipt journal
prevents replay and the optional worker supervisor can recover a live task
after gateway restart. Neither replays interrupted work.

Mac UI checks cover 210 appearance/provider/state/tab combinations. Windows
WPF compilation and native test code are verified separately from actual
device and external provider acceptance.

## Portable install and Windows parity

Mac Settings provides optional LaunchAgent login/keep-alive controls.
No background service is enabled implicitly by local npm compile or manual GUI
launch. The CLI can enable/disable the dedicated local.chat-to-computer-mcp
LaunchAgent without touching local.filemcp.

Windows Settings now includes additional named folder approvals and a shared
4–16 command cap. Windows Tasks lists scoped sessions and supports targeted
cancellation. Windows x64 integration and WPF smoke have now passed on a GitHub-hosted
Windows runner (run 45028574644). Native ARM64 execution and complete upgrade
and restart scenarios remain unverified; ARM64 packages build successfully.


## Expanded native controls in 0.3.0

- macOS Tasks: retain selection after refresh, inspect bounded stdout/stderr
  and exit codes, reveal the owning folder and explicitly copy command output.
  Output can contain secrets. The prior interrupted receipt count is not
  a subprocess reattachment mechanism.
- Windows Tasks: inspect bounded stdout/stderr and exit codes, reveal folder
  in Explorer, copy output and cancel only the selected long-running session.
- macOS Connection (Cloudflare OAuth): list server-verified client registrations,
  show user-approved scopes and revoke a client and its refresh-token family.
  Display names are self-reported labels, never security identities.
- Additional non-shell MCP tools on Mac and Windows: file_info,
  create_directory, copy_file and move_file. Paths stay in approved roots,
  source symlinks are refused and existing destinations are never overwritten.


## Native updates (0.3.0)

macOS Settings and Windows Settings now include Check for updates,
Install update, Check automatically and Install automatically controls.
These fetch a verified stable release from the official npm Registry and
compile a separate versioned native app. All automation is opt-in and
running AI tasks or tunnels are never restarted by the updater.

The native GUIs and CLI share Boolean update preferences. See
[UPDATES.md](UPDATES.md).


## Windows interrupted-command receipts

Windows Tasks now displays the count of previous commands whose terminal
state could not be established after restart. The local encrypted-user-context
store records only hashed request IDs and state; the GUI warns the user to
inspect effects rather than blindly rerun. This mirrors the Mac receipt
warning and does not claim to reattach an orphaned process.


## Windows optional login supervision

Windows Settings now has opt-in **Start on sign-in**, **Auto-connect**,
**Always-on** and **Apply login settings** controls, matching the macOS
LaunchAgent's user-facing behaviors. Windows uses HKCU Run plus a
non-privileged in-app supervisor; macOS uses native launchd. Both are
user-login services, not pre-login system daemons, and do not independently
reattach subprocesses after a reboot.

## Windows connection and desktop parity

Managed automatic connection on Windows checks OpenAI control-plane poll
health on system resume and distinguishes a ready local gateway from a
verified cloud connection. A stalled transport is eligible for idle recovery
without restarting the local MCP server. Both desktop apps support copying
workspace invocation instructions and configuration locking during connection
or account changes. Windows Cloudflare login can be cancelled; switching
accounts retains a backup of the previous certificate and restores it when
a failed login leaves no replacement. These operations still require actual
native Windows and provider acceptance.
