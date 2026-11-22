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
- Durable jobs, command output retention, progress and recovery across restart.
- Native Windows parity for workspaces, task management and permissions.
- Keyboard, VoiceOver, multi-display and screenshot regression expansion.


## Portable install and Windows parity

Mac Workspaces now provides optional LaunchAgent login/keep-alive controls.
No background service is enabled implicitly by local npm compile or manual GUI
launch. The CLI can enable/disable the dedicated local.chat-to-computer-mcp
LaunchAgent without touching local.filemcp.

Windows Settings now includes additional named folder approvals and a shared
4–16 command cap. Windows Tasks lists scoped sessions and supports targeted
cancellation. The native Windows implementation is compiled with a Windows
targeting pack on Mac; final Windows GUI/real-process acceptance must run on a
Windows runner or PC.
