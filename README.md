<p align="center">
  <img src="assets/branding/chat-to-computer-mcp/wordmark.svg" width="640" alt="ChatToComputerMCP">
</p>

<h1 align="center">ChatToComputerMCP</h1>

<p align="center">
  <a href="./README.md"><strong>English</strong></a> ·
  <a href="./README.zh-CN.md"><strong>简体中文</strong></a>
</p>

<p align="center"><strong>Give AI conversations a permission-controlled path to your computer.</strong><br>
One local gateway · Multiple AI clients · Multiple workspaces · Native desktop controls.</p>

<p align="center">
  <a href="https://www.npmjs.com/package/chat-to-computer-mcp"><img alt="npm version" src="https://img.shields.io/npm/v/chat-to-computer-mcp?logo=npm&color=cb3837"></a>
  <a href="https://www.npmjs.com/package/chat-to-computer-mcp"><img alt="npm downloads" src="https://img.shields.io/npm/dt/chat-to-computer-mcp?label=Downloads&color=2ea44f"></a>
  <a href="#-quick-start"><img alt="Quick start" src="https://img.shields.io/badge/Quick_Start-local_build-blue"></a>
  <img alt="macOS and Windows" src="https://img.shields.io/badge/Platforms-macOS_%7C_Windows-5969ff">
  <a href="./LICENSE"><img alt="Apache-2.0" src="https://img.shields.io/badge/License-Apache--2.0-yellow"></a>
</p>

> ⭐ If this project helps you, a GitHub Star helps more developers discover it.

---

## 📑 Contents

- [About](#-about)
- [At a glance](#-at-a-glance)
- [Quick start](#-quick-start)
- [Installation and CLI](#-installation-and-cli)
- [Workspace workflow](#-workspace-workflow)
- [Desktop experience](#-desktop-experience)
- [Architecture](#-architecture)
- [MCP tool examples](#-mcp-tool-examples)
- [Permissions and security](#-permissions-and-security)
- [Platform verification](#-platform-verification)
- [Build and test](#-build-and-test)
- [Related projects](#-related-projects)
- [FAQ](#-faq)
- [Documentation](#-documentation)
- [Contributing and license](#-contributing-and-license)

---

## 💡 About

Cloud AI chats often understand what needs doing in a repository, but cannot access the machine where the work lives. Multiple conversations then compete for one desktop, one working directory, or one long-running command.

**ChatToComputerMCP** is a **local-first Model Context Protocol (MCP) gateway** that provides approved filesystem, search, Git and optional shell tools to compatible cloud AI clients. It is **not** a new AI model, a hosted computer, or an IDE replacement.

```text
ChatGPT / Claude / another compatible MCP client
                         │ authenticated tunnel
                         ▼
                ChatToComputerMCP
                macOS / Windows GUI
                         │
                  approved folders
              ┌──────────┼──────────┐
              ▼          ▼          ▼
            Repo A     Repo B    Project C
           scoped local tools and command sessions
```

**The user approves which folders exist as workspaces.** MCP clients cannot grant themselves new directory access. The path guard is application-level containment, not a shell sandbox.

## ✨ At a glance

| Capability | What it does |
|---|---|
| **Multiple workspaces** | One default workspace plus up to 12 user-approved, named, non-overlapping roots. |
| **Scoped MCP operations** | `list_workspaces` discovers approved scopes; `workspace_call` selects one explicitly. |
| **Concurrent commands** | One shared configurable 4–16 command budget (default 8); overlapping cwd trees are rejected. |
| **Task controls** | Command IDs, bounded output, status and targeted cancellation. |
| **Native GUI** | Mac: Overview, Workspaces, Connection, Tasks, Activity. Windows: connection, workspace and task management. |
| **Connection providers** | OpenAI Secure MCP Tunnel on Mac/Windows; Cloudflare alternative on Mac. |
| **Local installation** | npm ships native source and pinned vendor binaries; installation **compiles locally**. |
| **Optional background mode** | macOS login LaunchAgent, crash recovery, optional always-on and explicit auto-connect. |

## 🚀 Quick start

### 1. Install from npm

Requires **Node.js 20+** and **macOS with Xcode Command Line Tools/Swift** or **Windows with .NET 8 SDK and Windows PowerShell**.

```bash
npx --yes chat-to-computer-mcp@latest install
```

This builds and installs locally. It does **not** start a service, enable shell access or replace an existing FileMCP installation.

### 2. Open the desktop app

```bash
npx --yes chat-to-computer-mcp@latest open
```

### 3. Approve a workspace and connect

Use the desktop **Workspaces** page to select a default directory, add approved folders, and review tool permissions. Under **Connection**, configure a supported MCP tunnel and connect it from a compatible AI client.

### 4. Work from your AI chat

For example: *“Show my approved workspaces, open the second repository and investigate its failing tests.”* The connected client can then use the scoped tools you approved.

> Installing the app does not secretly configure ChatGPT, Claude or other third-party clients. MCP connector availability and authorization depend on the individual client.

## 📦 Installation and CLI

```bash
# Persistent global CLI (optional)
npm install -g chat-to-computer-mcp
chat-to-computer-mcp install
chat-to-computer-mcp open
chat-to-computer-mcp status
```

| Command | Purpose |
|---|---|
| `install` | Build into a versioned per-user app directory. |
| `install --rebuild` | Build again after the installed app exits; keep the previous version as backup. |
| `open` | Open the installed native app. |
| `status` | Show installed app path and, on Mac, LaunchAgent status. |
| `launchagent enable` | Mac: optional login launch and abnormal-exit recovery. |
| `launchagent enable --always` | Mac: relaunch even after normal Quit (explicit opt-in). |
| `launchagent enable --auto-connect` | Mac: connect the tunnel on login if no conflicting legacy service is active. |
| `launchagent disable` | Mac: disable only the new independent service. |

### Optional macOS keep-alive

```bash
chat-to-computer-mcp launchagent enable
chat-to-computer-mcp launchagent status
chat-to-computer-mcp launchagent disable
```

This is a **per-user login agent**, not a system daemon available before login. Auto-connect is **off by default**. The Mac GUI also offers next-login options without restarting another running app. See [Install guide](docs/INSTALL_NPM.md).

## 🗂 Workspace workflow

1. Select the default workspace (direct MCP tools remain scoped to it).
2. Use **Add folders…** to approve extra existing directories; names and IDs persist.
3. Review shell, Git push and other privileged permissions before saving.
4. Set a global 4–16 concurrent-command limit shared by all workspaces.
5. Save changes and reconnect before newly granted/revoked permissions take effect.
6. Inspect jobs and cancel only the selected long-running session under **Tasks**.

Removing a workspace does **not** delete its files. Parent/child workspace overlaps and canonical symlink aliases are refused. See [Multi-workspace permissions](docs/MULTI_WORKSPACE_SECURITY.md).

## 🖥 Desktop experience

| Page | Key controls |
|---|---|
| **Overview** | Connection summary, active workspace and permissions |
| **Workspaces** | Add, rename, remove and reveal folders; command capacity; privileged tools; optional login agent |
| **Connection** | Tunnel provider, endpoint and credentials, connect/disconnect and OAuth actions |
| **Tasks** | Running command sessions, workspace context, refresh and targeted cancellation |
| **Activity** | Redacted logs, copy, clear and connection diagnostics |

The Windows client provides its own native WPF settings and task controls; its actual Windows GUI execution remains a separate release test, not a result inferred from Mac compilation.

## 🧭 Architecture

- A loopback-only MCP server exposes approved tools.
- The outbound tunnel connects supported remote clients to the local endpoint.
- Each workspace has a canonicalizing resolver and mutation controls.
- A shared command-admission budget counts asynchronous and synchronous jobs.
- Commands run with the **user's OS privileges**; cwd collision checks do **not** confine absolute paths.
- Sessions expose IDs, bounded output and target-specific cancellation.

**This version does not yet provide cryptographically verified per-conversation identity or durable subprocess reattachment across an application crash.** Both are explicit roadmap items.

## 🔧 MCP tool examples

Discover user-approved workspaces:

```json
{"name":"list_workspaces","arguments":{}}
```

Operate in a separately approved workspace:

```json
{
  "name":"workspace_call",
  "arguments":{
    "workspace_id":"approved-workspace-id",
    "tool_name":"read_file",
    "arguments":{"relative_path":"README.md"}
  }
}
```

Ordinary `read_file`, `grep`, `git_status` and other direct calls continue to operate in the default workspace. Clients cannot create a new scope through MCP.

## 🔒 Permissions and security

- Shell execution is **disabled unless opted in** and inherits the user's OS access.
- Folder containment covers normal tool operations, not arbitrary shell commands.
- API keys belong in macOS Keychain or Windows Credential Manager, not npm or the repo.
- Installing does not touch the old `local.filemcp` service, credentials, proxy or tunnel.
- Per-client authenticated identities and durable task recovery remain **future work**.
- Locally compiled Mac apps are not automatically Apple Developer ID signed or notarized.

Read [SECURITY.md](SECURITY.md) and [Architecture](docs/ARCHITECTURE.md) before enabling commands for less-trusted clients.

## 🧪 Platform verification

| Check | macOS | Windows |
|---|---|---|
| Native source build | **Passed** | **.NET 8 Windows-targeting build passed** |
| Multiple workspaces | **Scoped tests passed** | Implemented; Windows runtime test pending |
| Command capacity/cancel | **8/12/16 and MCP regressions passed** | Implemented; Windows runtime test pending |
| Native GUI | **150 layout-state checks passed** | WPF compiled; real UI acceptance pending |
| npm tarball → installed app | **Clean-HOME local Swift build passed** | Clean Windows install pending |

Cross-compiling Windows projects on Mac does **not** prove Windows execution. The current [roadmap](docs/ROADMAP.md) lists the remaining release gates.

## 🛠 Build and test

```bash
# macOS source tree
bash build_macos_app.sh
bash tests/test_swift_ui.sh
bash tests/test_swift_workspace_scope.sh
bash tests/test_swift_agent_tools.sh
bash tests/test_swift_runtime.sh

# Installer/package
npm test
npm pack --dry-run --json
```

Windows: build `windows/FileMCP.Windows.sln` with .NET 8, then execute the native Windows runtime and GUI tests **on Windows**.

Automated future npm releases use [GitHub Actions OIDC trusted publishing](docs/PUBLISHING.md), without long-lived publish tokens.

## 🔗 Related projects

From the same maintainer, these are complementary—not automatically integrated—developer tools:

- **[iterate-skill](https://github.com/jingzhao-l/iterate-skill)** — iterative AI code review and fixes in coding assistants.
- **[iterate-harness](https://github.com/jingzhao-l/iterate-harness)** — the headless iterate review and verification engine.
- **[iterate-plugin](https://github.com/jingzhao-l/iterate-plugin)** — iterate dashboards and controls in the dsh client.
- **[GlassPane](https://github.com/jingzhao-l/GlassPane)** — separate macOS runtime UI verification.

ChatToComputerMCP gives compatible AI conversations an approved local execution path; it is **not** a component of the iterate review loop and does not claim a shared runtime or configuration.

## ❓ FAQ

**Does it replace ChatGPT or Claude?** No. It offers local tools to a compatible MCP client.

**Can multiple AI chats run commands at once?** Yes, in disjoint working directories within the shared limit. This is not complete isolation from the host OS.

**Can I use it after restarting my Mac?** Optionally, a macOS LaunchAgent starts it at user login. It is not a pre-login system daemon.

**Is the Windows version fully verified?** Windows projects compile with a Windows-targeting SDK; real-device tests are still pending.

**Does installation change my existing FileMCP?** No. The new app is independent, and conflicting auto-connect is blocked.

## 📚 Documentation

| Guide | Focus |
|---|---|
| [Install from npm](docs/INSTALL_NPM.md) | System requirements, CLI, background service |
| [Architecture](docs/ARCHITECTURE.md) | Client, tunnel, host runtime and workspace scopes |
| [Multi-workspace security](docs/MULTI_WORKSPACE_SECURITY.md) | Permission and path guarantees |
| [GUI capabilities](docs/GUI_CAPABILITIES.md) | Native controls |
| [Publishing](docs/PUBLISHING.md) | npm OIDC, version tags and release procedure |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Build, connection and permissions |
| [Roadmap](docs/ROADMAP.md) | Verified vs outstanding |
| [Migration](docs/RELEASE_MIGRATION.md) | Legacy compatibility and safe rollback |
| [Attribution](docs/UPSTREAM_ATTRIBUTION.md) | FileMCP source lineage |

## 🤝 Contributing and license

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), keep changes reviewable and run platform-appropriate tests. Submit sensitive security issues privately as instructed in [SECURITY.md](SECURITY.md).

ChatToComputerMCP is derived from [anhnv02/file-mcp](https://github.com/anhnv02/file-mcp) and retains applicable copyright, source notices and the [Apache License 2.0](LICENSE).

**Independent community project.** Not affiliated with or endorsed by OpenAI, Anthropic or the original FileMCP maintainers.
