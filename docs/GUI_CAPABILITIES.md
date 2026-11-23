# Native GUI capabilities and design guide

The macOS UI is a native AppKit window with five pages using system controls,
persistent selected tab and light/dark appearances.

| Page | GUI controls |
|---|---|
| Overview | Folder and permission summary, tunnel route and quick actions |
| Workspaces | Default folder, add/rename/remove/reveal multiple approvals, copy scoped invocation, shell/Git/Codex permissions, 4–16 capacity, advanced profile/port/health/Git identity |
| Connection | Provider, credentials, OAuth pairing, connection save, connect/disconnect and managed reconnect |
| Tasks | Scoped command sessions, refresh, workspace context and single-session cancellation |
| Activity | Redacted log output, follow/scroll, copy and clear |

## UX and consent

- Shell and Git push remain separate privileged opt-in controls.
- Settings affecting permissions are locked while connected.
- Cancelling a task requires confirmation and targets only that session.
  Short synchronous commands are counted but cannot be cancelled here yet.
- AppKit displays the window before requesting Keychain authorization.
- Advanced network settings are progressively disclosed.
- System appearance, native keyboard focus and accessibility names remain.

## Next UX milestones

- Authenticated client identities, distinct permission approvals and audit.
- Mac Tasks now displays prior interrupted/unconfirmed command receipt counts and cautions against automatic replay. It is not full process recovery.
- Durable jobs, command output retention, progress and actual process reattachment after restart.
- Native Windows parity for workspaces, task management and permissions.
- Keyboard, VoiceOver, multi-display and screenshot regression expansion.


## Portable install and Windows parity

Mac Workspaces now provides optional LaunchAgent login/keep-alive controls.
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

macOS Workspaces and Windows Settings now include Check for updates,
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
