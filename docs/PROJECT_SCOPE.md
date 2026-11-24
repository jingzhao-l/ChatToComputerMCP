# ChatToComputerMCP product vision

Cloud AI conversations, including ChatGPT and Claude where compatible MCP
transport is available, should use explicitly approved local development
workspaces and optional background computer use without requiring a separate
agent CLI as their chat interface.

## Core capabilities and boundaries

- Native macOS/Windows loopback gateway: OpenAI Secure MCP Tunnel or
  Cloudflare, scoped local tools, code search, Git and optional host Shell.
- One default workspace and up to 12 named disjoint approvals; each tool call
  can select its scope. Shared 4–16 command capacity, FIFO queue, targeted
  cancellation, durable receipts, local command history and opt-in live
  independent-worker reattachment after gateway restart.
- Verified Cloudflare OAuth client grants and revocation; shared OpenAI
  Tunnel is not an authenticated cloud-conversation principal. Shell cwd
  protection is cooperative, not an OS sandbox.
- Optional direct background native window control and separately granted
  isolated browser. Independent Guest is off by default and only explicitly
  enabled locally; never automatic foreground/Guest fallback.
- Native desktop GUI, optional per-user login supervision, safe versioned
  npm source installation and updates. No forced restart of active jobs.

ChatToComputerMCP is neither an LLM service nor an autonomous AI companion.
It does not promise every remote-management feature, zero interference in
all third-party apps or platform-verified per-conversation identity.
User grants are not expanded by remote MCP requests. See [architecture](ARCHITECTURE.md),
[tool contract](BASE_TOOL_CONTRACT.md), [CUA](CUA.md) and [platform parity](PLATFORM_PARITY.md).
