# Multi-workspace permissions and MCP routing

ChatToComputerMCP retains one **default** workspace for direct MCP tool calls,
and lets the local user approve up to 12 additional folders in the macOS GUI.

## GUI workflow

In Workspaces, set the default folder, then choose Add folders.
Each added folder receives a durable opaque ID and editable friendly name.
Use Rename, Remove, Reveal, or Copy AI scope to manage it.
Click Save workspace and reconnect to apply changed permissions.
Removing a workspace never deletes files on disk.

Changes to the workspace list while connected are disabled intentionally:
the running server must not silently gain new filesystem privileges.

The GUI also provides Command capacity, which applies to all approved
workspaces (4–16, default 8), and a Tasks page showing running sessions
and explicit targeted cancellation.

## MCP API

Existing direct tool calls continue to operate only in the default workspace.
When additional workspaces exist, two tools are offered:

- list_workspaces: approved id, name, path entries.
- workspace_call: supply workspace_id, tool_name and arguments.
  Example JSON: {"workspace_id":"second","tool_name":"read_file",
  "arguments":{"relative_path":"README.md"}}.

Clients cannot create a workspace or choose arbitrary absolute roots via MCP.
Every inner tool name uses the same enabled-tool checks as direct calls.
Recursive routing is disallowed. A scoped session ID must be read/cancelled
through the matching workspace_call context.

## Security boundaries

- Canonicalized workspace directories must exist and be mutually disjoint,
  including symlink aliases and parent/child overlap.
- Each scope has its own resolver, Git and mutation guard. One shared command
  admission budget limits concurrent commands across all scopes, including
  simultaneous synchronous and asynchronous requests.
- Cloudflare OAuth permission policy binds all canonical roots. Its local
  authorization page defaults to the default workspace and requires an
  explicit user choice for one additional workspace or all approved scopes.
  Verified clients cannot access unapproved workspaces or read/cancel
  sessions owned by another verified OAuth client. The Mac GUI can revoke
  the client's access/refresh token family. Permission changes require consent.
- OpenAI Secure MCP Tunnel still forwards a shared local authentication header,
  not a unique verified ChatGPT conversation identity. Do not claim these two
  modes provide equivalent per-conversation isolation.
- A working-directory guard is NOT an OS sandbox: shell commands may still
  access other folders with absolute paths.
- Windows now supports explicit scoped routing and a shared 4–16 command
  budget. A real Windows regression/GUI acceptance remains mandatory before
  claiming cross-platform release readiness.
