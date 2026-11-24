# Multi-workspace permissions and MCP routing

ChatToComputerMCP retains one **default** workspace for backward-compatible MCP tool calls,
and lets the local user approve up to 12 additional folders in the Mac or Windows GUI.

## GUI workflow

In Workspaces, set the default folder, then choose Add folders.
Each added folder receives a durable opaque ID and editable friendly name.
Use Rename, Remove, Reveal, or Copy AI scope to manage it.
While disconnected, save workspace settings before connecting. While connected, the local GUI can add, rename, revoke, or change permissions for additional workspaces without restarting the tunnel. A changed scope must be idle, and the catalog is published only after settings persistence succeeds. New scopes default to read-only. The default root and global permission switches still require reconnection. OAuth catalog changes require all active operations to finish and explicitly invalidate the old access and refresh tokens; clients must authorize again. Removing a workspace never deletes files on disk.

The GUI also provides Command capacity, which applies to all approved
workspaces (4–16, default 8), and a Tasks page showing running sessions
and explicit targeted cancellation.

## MCP API

Discovery is always published, even with only the default workspace.
All scoped MCP tools accept an optional top-level workspace_id. Omitting it
selects the default workspace. Different calls can independently select
different approved workspaces without changing another client's context.

- list_workspaces: approved ID, name, canonical path and allowed_tools entries.
- read_file, grep, git_status, start_command and other scoped tools:
  supply workspace_id alongside the ordinary arguments.
  Example read_file arguments: {"workspace_id":"second","relative_path":"README.md"}.
- workspace_call remains available for clients using the earlier wrapper.

After upgrading the app, refresh the MCP client's imported tool schema
if it still presents an older catalog. Server-side availability alone
does not prove that a particular cloud client has imported the schema.

Clients cannot create a workspace or choose arbitrary absolute roots via MCP.
Every inner tool name uses the same enabled-tool checks as direct calls.
Recursive routing is disallowed. A scoped session ID must be read/cancelled
with the same workspace_id (or matching legacy wrapper context).

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

## Optional FIFO command queue

Pass `queue_if_busy: true` to `start_command` to opt into one global FIFO
queue across authorized workspaces. A queued response is nonterminal: keep
polling with the same workspace and session ID. Queue waiting does not consume
the execution timeout. `command_capacity_status` includes `queued_total`,
separate from running commands. Up to 64 queued requests are admitted; later
immediate submissions cannot jump the queue. Queued commands can be cancelled
before launch, and their directory is revalidated when scheduled. An earlier
blocked directory can delay independent later work, intentionally preserving
FIFO order. Queued or running work also prevents changing affected workspace
permissions. Runtime restart marks unfinished local records interrupted and
never automatically replays queued commands.

## Optional isolated Linux Shell

The local workspace permission dialog supports an immutable local Docker
image ID (`sha256:` plus 64 lowercase hex digits). This is a distinct Linux
execution environment, not native macOS/Windows shell emulation. It requires
local Docker Desktop Linux engine and a locally trusted image containing
`/bin/sh` and GNU `/usr/bin/timeout`; no automatic image download or host-shell
fallback is allowed. The image must support the host architecture or Docker's
configured emulation and allow the non-root container UID 65532 to write the
approved bind mount. The gateway uses an isolated Docker CLI config and local
socket/pipe, a read-only container root, one workspace mount (no recursive
submounts), bounded tmpfs, disabled network, dropped capabilities, no privilege
escalation, and process/memory/CPU limits. No host Git push or Codex import is
published for that workspace; other host Git tools use safe mode. Docker
container output is streamed through the usual bounded command channel.
Cancellation and timeout request removal of the named container, with an
in-container timeout as a second bound. A trusted Docker engine and image are
prerequisites, not a guarantee against kernel or engine vulnerabilities.
`list_workspaces` and `workspace_context` report the selected shell execution
mode. The regular host Shell still has full user privileges.

Real Docker validation can be enabled explicitly using a locally prepared
image with `MCP_TEST_ISOLATED_IMAGE=sha256:...` in the command integration tests.
Without a local engine, tests cover invalid-image rejection and absence of
host fallback, not actual container isolation.
