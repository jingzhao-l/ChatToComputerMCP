# ChatToComputerMCP product vision

## Goal
Cloud AI chat applications (including ChatGPT and Claude where they support
MCP transport) should access explicitly approved local development workspaces
without needing a standalone agent CLI to be the user-facing chat interface.

## Core invariants
- User-scoped folders and capabilities; shell off by default; secrets stay
  in OS credential storage and are never exported for migration.
- Independent worktrees, per-session concurrency, targeted cancellation and
  auditable conflict diagnostics. Cwd guards are cooperative, not a sandbox.
- Managed connection, restart/reconnect and rollback without deleting the
  original application or changing users' connection tokens.
- Standard protocols and genuinely verified client support, not unsupported
  claims of native ChatGPT or Claude integration.

## Product boundaries
ChatToComputerMCP is a local host gateway, not an LLM service or AI companion.
A conversation's agent identity belongs to its client and is not determined
by ChatToComputerMCP's product name. Future extensions should support workspace
admission, fair scheduling, process ownership, observability and approval
rather than unrestricted shell access.


## Multi-workspace desktop management

Mac supports a default workspace plus approved, named, disjoint folders.
The native Workspaces page grants/revokes these scopes; the Tasks page
inspects command status and targeted cancellation. Additional roots require
explicit workspace_call selection and cannot be created remotely.
