# ThreadQuay product vision

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
ThreadQuay is a local host gateway, not an LLM service or AI companion.
A conversation's agent identity belongs to its client and is not determined
by ThreadQuay's product name. Future extensions should support workspace
admission, fair scheduling, process ownership, observability and approval
rather than unrestricted shell access.
