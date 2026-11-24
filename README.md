<p align="center">
  <img src="assets/branding/chat-to-computer-mcp/wordmark.svg" width="640" alt="ChatToComputerMCP">
</p>

<h1 align="center">ChatToComputerMCP</h1>

<p align="center">
  <a href="./README.md"><strong>English</strong></a> ·
  <a href="./README.zh-CN.md"><strong>简体中文</strong></a>
</p>

<p align="center"><strong>Connect ChatGPT and other cloud AI chats to your local computer.</strong><br>
Free, open-source MCP alternative to paid computer-access plugins · Multiple workspaces · 4–16 concurrent commands.</p>

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
- [Connect ChatGPT with Secure MCP Tunnel](#-connect-chatgpt-with-secure-mcp-tunnel)
- [Installation and CLI](#-installation-and-cli)
- [Safe updates](#-safe-updates)
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

**ChatGPT can plan a task in the cloud—but how does it actually work on your Mac or Windows computer?** ChatToComputerMCP closes that gap. It is a **free, open-source local-computer MCP bridge** built for cloud-based AI chat conversations, especially **ChatGPT**, and other compatible MCP clients such as **Claude**.

Instead of needing a separate paid computer-access plugin for common development workflows, connect ChatToComputerMCP and let your AI chat **inspect and edit approved local files, search code, use Git, and (when you opt in) run terminal commands**. For these workflows, it is an open-source alternative to the paid **Remote Desktop Commander** connector. It is **not** a feature-for-feature replacement for its entire desktop-control or system-management toolset, nor an official ChatGPT plugin.

**Designed for multiple conversations, not just one:** approve multiple independent workspaces and allow **4–16 simultaneous command jobs** (default 8), with per-directory conflict protection and targeted task cancellation. This is bounded command concurrency—not unlimited parallelism, complete per-client identity isolation, or a remote GUI desktop.

**ChatToComputerMCP runs locally on your own computer.** It is not an AI model, a hosted computer, or an IDE replacement.

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
| **Paid-plugin alternative** | Use ChatGPT or another compatible cloud AI chat with your own open-source local tools for common file, Git and shell workflows—without a separate paid computer-access plugin. |
| **Multiple workspaces** | One default workspace plus up to 12 user-approved, named, non-overlapping roots. |
| **Scoped MCP operations** | `list_workspaces` discovers approved scopes; each scoped tool accepts `workspace_id` directly. |
| **Concurrent commands** | One shared configurable 4–16 command budget (default 8); overlapping cwd trees are rejected. |
| **Task controls** | Command IDs, bounded output, status and targeted cancellation. |
| **Safe everyday file tools** | Metadata lookup, folder creation, file copy and move/rename without Shell access; refuse destination overwrite. |
| **Verified OAuth scopes (Mac)** | Cloudflare OAuth clients can be approved for specific roots, own their command sessions and be revoked in the GUI. Private OpenAI Tunnel remains shared. |
| **Native GUI** | Mac: Overview, Workspaces, Connection, Tasks, Activity. Windows: connection, workspace and task management. |
| **Connection providers** | OpenAI Secure MCP Tunnel on Mac/Windows; Cloudflare alternative on Mac. |
| **Local installation** | npm ships native source and pinned vendor binaries; installation **compiles locally**. |
| **Optional background mode** | Mac LaunchAgent and Windows per-user login supervisor, crash recovery, optional always-on and explicit auto-connect. |
| **Safe auto-updates** | CLI update commands and Mac/Windows GUI buttons, optional auto-check/auto-install while app is open, with versioned native builds. |

## 🚀 Quick start

### 1. Install from npm

Requires **Node.js 24+** and **macOS with Xcode Command Line Tools/Swift** or **Windows with .NET 8 SDK and Windows PowerShell**.

```bash
npx --yes chat-to-computer-mcp@latest install
```

This builds and installs locally. It does **not** start a service, enable shell access or replace an existing FileMCP installation.

### 2. Open the desktop app

```bash
npx --yes chat-to-computer-mcp@latest open
```

### 3. Approve a workspace and connect

Use the desktop **Workspaces** page to select a default directory, add approved folders and review tool permissions. Next follow **[Connect ChatGPT with Secure MCP Tunnel](#-connect-chatgpt-with-secure-mcp-tunnel)** to create a Tunnel ID and runtime key, connect the app, and add the server in ChatGPT.

### 4. Work from your AI chat

For example: *“Show my approved workspaces, open the second repository and investigate its failing tests.”* The connected client can then use the scoped tools you approved.

> Installing the app does not secretly configure ChatGPT, Claude or other third-party clients. MCP connector availability and authorization depend on the individual client.

## 🔌 Connect ChatGPT with Secure MCP Tunnel

**This is the recommended connection path for ChatGPT.** ChatToComputerMCP runs the private MCP server locally and bundles `tunnel-client`, so you do **not** need to open a public inbound port or install a second tunnel process.

1. **Create a Tunnel:** In [OpenAI Platform → Tunnel settings](https://platform.openai.com/settings/organization/tunnels), create/reuse a Secure MCP Tunnel and associate it with the **ChatGPT workspace** where you will use it. Copy the `tunnel_…` ID. Creating requires **Tunnels Read + Manage**.
2. **Create a restricted runtime API key:** Use the **OpenAI Platform**, not your ChatGPT subscription, to create a key with **Tunnels Read + Use**. Your ChatGPT workspace must independently allow custom MCP Plugins. Keep this API key private.
3. **Connect the native app:** In ChatToComputerMCP **Connection**, choose **OpenAI Secure MCP Tunnel**, enter the **Tunnel ID** and **Runtime API key**, then **Save connection → Connect**. Wait for **Connected**. The app securely stores the key in the OS credential store and manages the bundled tunnel client itself.
4. **Add it in ChatGPT:** Open [ChatGPT Plugins](https://chatgpt.com/plugins) → **+ → Add custom MCP server**, select **Connection: Tunnel** and choose/paste the **same Tunnel ID**. Review the authentication/risk warnings, create the Plugin, and verify its discovered tools.
5. **Test a tool call:** Start a new ChatGPT chat, select the Plugin with `@` and ask it to **list the files in your approved default workspace without modifying them**. Check that **Activity** in the local app records the actual call.

**Important:** Do not paste `http://127.0.0.1:8008/mcp` into ChatGPT for the Tunnel route—it is only the local server address. If your Tunnel is missing from ChatGPT, check its **ChatGPT workspace association** and your **Tunnels Read + Use** role.

**Full illustrated-by-steps guides:** [English connection guide](docs/CONNECT_CHATGPT.md) · [中文连接教程](docs/CONNECT_CHATGPT.zh-CN.md). The full guides cover permissions, Platform vs ChatGPT accounts, Cloudflare Tunnel (macOS), OAuth and troubleshooting. Official references: [OpenAI Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) and [Connect an MCP Plugin in ChatGPT](https://developers.openai.com/plugins/deploy/connect-chatgpt).

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

## 🔄 Safe updates

Both native apps now offer **Check for updates**, **Install update**, and
opt-in automatic checking/installation. Published source packages come only
from the official npm Registry, compile locally, and install into a new
versioned directory. **Running MCP sessions and tasks are not restarted.**

```bash
chat-to-computer-mcp update check
chat-to-computer-mcp update install
chat-to-computer-mcp update status
chat-to-computer-mcp update enable --auto-install
chat-to-computer-mcp update disable
```

If your CLI is still 0.1.0, start with
`npx --yes chat-to-computer-mcp@latest update install`. Updates become active
after you quit and reopen the native app. Automatic checks run while the
app is open, not from a hidden background daemon.

See [Safe update guide](docs/UPDATES.md).

### Optional Windows login supervision

Windows Settings offers opt-in **Start at login**, **Auto-connect** and
**Always-on** controls. The equivalent installed CLI commands are:

```bash
chat-to-computer-mcp startup enable
chat-to-computer-mcp startup enable --auto-connect --always
chat-to-computer-mcp startup status
chat-to-computer-mcp startup disable
```

This runs only after the user signs in and may restart an abnormal exit.
It never installs a privileged Windows service or silently enables Shell.
A native app update refreshes an existing user login registration without
restarting the current app or its AI tasks.

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

**Current boundary:** Mac Cloudflare OAuth can verify server-issued client tokens, enforce individually approved scopes and task ownership, and revoke clients through the native GUI. **OpenAI Secure MCP Tunnel continues to use shared local credentials; it does not prove the identity of an individual ChatGPT conversation.** Mac durable receipts prevent accidental command replay but do not reattach orphaned processes after restart. See [Verified client access](docs/VERIFIED_CLIENT_ACCESS.md).

## 🔧 MCP tool examples

Discover user-approved workspaces:

```json
{"name":"list_workspaces","arguments":{}}
```

Operate in a separately approved workspace:

```json
{
  "name":"read_file",
  "arguments":{
    "workspace_id":"approved-workspace-id",
    "relative_path":"README.md"
  }
}
```

Omitting `workspace_id` selects the default workspace. The legacy `workspace_call` wrapper remains supported. Clients cannot create new scopes through MCP.

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
| Native source build | **Passed** | **Windows x64 runner build passed** |
| Multiple workspaces | **Scoped tests passed** | **Windows x64 integration passed** |
| Command capacity/cancel | **8/12/16 and MCP regressions passed** | **Windows x64 integration passed** |
| Native GUI | **150 layout-state checks passed** | **Windows x64 WPF smoke passed** |
| npm tarball → installed app | **Clean-HOME local Swift build passed** | Clean Windows install pending |

The Windows x64 CI actually ran on a GitHub-hosted Windows runner. ARM64 packages were built but not run on native ARM64 hardware; device-specific upgrade and restart acceptance remains unverified at the owner's direction. The current [roadmap](docs/ROADMAP.md) lists the remaining release gates.

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

**Can it replace the paid Remote Desktop Commander connector?** For common local file management, code search, Git and opt-in command workflows, it can be a free, open-source alternative. It does not promise every Remote Desktop Commander feature, identical UI controls or the same client integrations. Some cloud MCP services may have their own charges.

**Can multiple AI chats run commands at once?** Yes, in disjoint working directories within the shared limit. This is not complete isolation from the host OS.

**Can I use it after restarting my Mac?** Optionally, a macOS LaunchAgent starts it at user login. It is not a pre-login system daemon.

**Is the Windows version fully verified?** Windows projects compile with a Windows-targeting SDK; real-device tests are still pending.

**Does installation change my existing FileMCP?** No. The new app is independent, and conflicting auto-connect is blocked.

## 📚 Documentation

| Guide | Focus |
|---|---|
| [Connect ChatGPT](docs/CONNECT_CHATGPT.md) / [中文教程](docs/CONNECT_CHATGPT.zh-CN.md) | Tunnel setup, Platform API key, ChatGPT Plugins and verification |
| [Install from npm](docs/INSTALL_NPM.md) | System requirements, CLI, background service |
| [Safe updates](docs/UPDATES.md) | CLI subcommands, GUI update controls, versioned installation and safety |
| [Architecture](docs/ARCHITECTURE.md) | Client, tunnel, host runtime and workspace scopes |
| [Multi-workspace security](docs/MULTI_WORKSPACE_SECURITY.md) | Permission and path guarantees |
| [Verified OAuth clients](docs/VERIFIED_CLIENT_ACCESS.md) | Client scopes, command ownership, token revocation and tunnel limits |
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

### Local command history

The desktop Tasks tab opens a searchable, exportable history of short and long shell commands, including workspace, directory, status, exit code and timing. Records survive gateway restarts; unfinished records are marked interrupted when that profile restarts, not automatically replayed. Command text and output are omitted by default; enable content capture only for trusted local use. Captured command text and output are each limited to 64 KiB. The history prunes completed records to 200 or 30 days on runtime startup and completion. This local history is separate from the durable retry-prevention ledger and is not an MCP tool. Shared tunnel clients are not individually identified unless verified OAuth identity is available.
