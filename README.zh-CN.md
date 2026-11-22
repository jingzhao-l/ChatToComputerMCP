<p align="center">
  <img src="assets/branding/chat-to-computer-mcp/wordmark.svg" width="640" alt="ChatToComputerMCP">
</p>

<h1 align="center">ChatToComputerMCP</h1>

<p align="center"><a href="./README.md"><strong>English</strong></a> · <a href="./README.zh-CN.md"><strong>简体中文</strong></a></p>

<p align="center"><strong>让 ChatGPT 等云端聊天 AI 直接连接并使用你的本地电脑。</strong><br>
免费开源的本机 MCP 工具 · 付费电脑访问插件的替代选择 · 多工作区 · 4–16 路命令并发</p>

<p align="center">
  <a href="https://www.npmjs.com/package/chat-to-computer-mcp"><img alt="npm 版本" src="https://img.shields.io/npm/v/chat-to-computer-mcp?logo=npm&color=cb3837"></a>
  <a href="https://www.npmjs.com/package/chat-to-computer-mcp"><img alt="累计下载" src="https://img.shields.io/npm/dt/chat-to-computer-mcp?label=Downloads&color=2ea44f"></a>
  <img alt="系统" src="https://img.shields.io/badge/Platforms-macOS_%7C_Windows-5969ff">
  <a href="./LICENSE"><img alt="Apache-2.0" src="https://img.shields.io/badge/License-Apache--2.0-yellow"></a>
</p>

> ⭐ 如果项目对你有帮助，欢迎为仓库点一个 Star，让更多开发者看到它。

---

## 📑 目录

- [项目介绍](#-项目介绍)
- [功能一览](#-功能一览)
- [快速开始](#-快速开始)
- [配置 ChatGPT Secure MCP Tunnel](#-配置-chatgpt-secure-mcp-tunnel)
- [安装与命令](#-安装与命令)
- [多工作区和界面](#-多工作区和界面)
- [技术架构](#-技术架构)
- [MCP 使用示例](#-mcp-使用示例)
- [权限与安全](#-权限与安全)
- [平台验证](#-平台验证)
- [源码构建与测试](#-源码构建与测试)
- [相关项目](#-相关项目)
- [常见问题](#-常见问题)
- [文档索引](#-文档索引)
- [贡献与许可](#-贡献与许可)

---

## 💡 项目介绍

**ChatGPT 在云端能分析和规划任务，但怎样才能真正操作你自己的 Mac 或 Windows 电脑？** 这就是 ChatToComputerMCP 要解决的问题：它是面向 **ChatGPT 云端聊天对话**，同时兼容 Claude 等支持 MCP 的 AI 客户端的**免费开源本机电脑连接工具**。

通过 ChatToComputerMCP，AI 可以在你批准的目录内**读取、搜索和编辑本地文件，操作 Git 仓库，并在明确授权后执行终端命令**。针对这类常见开发和文件操作，它可以作为 ChatGPT 付费插件 **@Remote Desktop Commander** 的开源替代选择，**不需要为了这些本机工具能力再购买一个同类付费插件**。但它**不是** Remote Desktop Commander 所有远程桌面、系统管理功能的逐项复刻，也不是 ChatGPT 官方插件。

**专为多 AI 会话并发而设计：**你可以批准多个独立工作区，让不同聊天会话同时执行 **4–16 个命令任务**（默认 8），并通过目录冲突保护和定向取消避免相互干扰。这里指有上限的命令并发，并非无限并行、已实现逐客户端加密身份隔离，或完整远程桌面 GUI 控制。

**程序运行在用户自己的电脑上**，不是新大模型、托管云电脑或 IDE 替代品。

```text
ChatGPT / Claude / 其他兼容 MCP 的客户端
                  │ 经授权的连接/隧道
                  ▼
           ChatToComputerMCP
            macOS / Windows GUI
                  │
            用户批准的工作区
          ┌───────┼───────┐
          ▼       ▼       ▼
        仓库 A   仓库 B   项目 C
             本地文件与命令执行
```

**授权来自本机用户，而非 AI 自行申请成功。** 远端 MCP 工具不能擅自增加新的工作区。

## ✨ 功能一览

| 能力 | 说明 |
|---|---|
| **付费插件替代选择** | 让 ChatGPT 等兼容的云端 AI 对话使用开源本地文件、Git 与可选 Shell 工具，不必额外购买同类电脑访问插件。 |
| **多个工作区** | 一个默认工作区，另可添加最多 12 个命名、互不重叠的工作区。 |
| **按工作区调用工具** | `list_workspaces` 查看已批准的工作区；`workspace_call` 显式选择执行范围。 |
| **多 AI 并发命令** | 总容量可设为 **4–16**，默认 **8**；拒绝重叠目录的冲突命令。 |
| **任务管理** | 查询命令会话、分页读取输出、取消指定任务。 |
| **安全文件操作** | 不开启 Shell 即可查看文件属性、创建目录、复制和移动/重命名文件；目标文件存在时不覆盖。 |
| **Mac OAuth 客户端权限** | Cloudflare OAuth 可以校验真实令牌、按客户端限制工作区和任务操作，并在 GUI 中撤销授权。私有 OpenAI Tunnel 仍使用共享连接身份。 |
| **原生桌面 GUI** | Mac 提供概览、工作区、连接、任务、日志五个页面；Windows 有独立 WPF 界面。 |
| **连接方案** | Mac/Windows 支持 OpenAI Secure MCP Tunnel；Mac 另有 Cloudflare 连接方式。 |
| **本机编译安装** | npm 包提供源码和固定版本工具，在用户自己的设备上编译应用。 |
| **可选保活** | Mac 可开启 LaunchAgent 登录自启、异常恢复及自主选择的持续保活。 |

## 🚀 快速开始

### 1. 安装并在本机编译

需要 **Node.js 20+**，此外 Mac 需要 Xcode Command Line Tools / Swift；Windows 需要 .NET 8 SDK 和 Windows PowerShell。

```bash
npx --yes chat-to-computer-mcp@latest install
```

这条命令会在本机编译并安装原生程序，**不会自动启动后台服务、开启 Shell 权限或覆盖已有 FileMCP 安装**。

### 2. 打开桌面应用

```bash
npx --yes chat-to-computer-mcp@latest open
```

### 3. 选取工作区并连接

在 **Workspaces（工作区）** 里选取默认目录、添加其他授权文件夹并检查权限，然后按下面的[ChatGPT Tunnel 连接流程](#-配置-chatgpt-secure-mcp-tunnel)创建 Tunnel ID、运行时 API Key，在应用里连接，并把它添加到 ChatGPT。

### 4. 直接在 AI 对话中工作

例如：*“列出已批准的工作区，到第二个仓库里定位并修复失败的测试。”*

> 能否在 ChatGPT、Claude 等具体客户端使用，取决于该客户端是否开放兼容的 MCP 接入方式。安装本项目不会私下替你配置第三方账户。

## 🔌 配置 ChatGPT Secure MCP Tunnel

**这是 ChatGPT 推荐的私有连接方案。** ChatToComputerMCP 自动管理本机 MCP 服务和内置 `tunnel-client`，不需要对公网开放入站端口，也不用自己再安装或启动一个 tunnel-client。

1. **在 OpenAI Platform 创建 Tunnel：**打开 [Platform Tunnel 设置](https://platform.openai.com/settings/organization/tunnels)，创建或选择已有 Secure MCP Tunnel，将它关联到**准备使用插件的 ChatGPT 工作区**。复制 `tunnel_…` 开头的真实 ID。创建需要 **Tunnels Read + Manage**。
2. **准备独立的运行时 API Key：**在 **OpenAI Platform** 创建权限受限的密钥，赋予 **Tunnels Read + Use**。**ChatGPT 订阅不等于 Platform API Key**；ChatGPT 工作区也必须允许添加自定义 MCP 插件。不要在聊天、截图或仓库中透露密钥。
3. **配置本机应用：**进入 ChatToComputerMCP 的 **Connection（连接）** 页面，选择 **OpenAI Secure MCP Tunnel**，填写 **Tunnel ID** 和 **Runtime API key**，再点击 **Save connection → Connect**。确认显示 **Connected**。应用会使用系统凭据管理器保存密钥并自动管理 Tunnel 客户端。
4. **在 ChatGPT 添加插件：**打开 [ChatGPT Plugins](https://chatgpt.com/plugins) → **+ → Add custom MCP server**，在 **Connection** 选择 **Tunnel**，选中或填入**相同的 Tunnel ID**。确认身份验证和风险提示，创建后检查发现的 MCP 工具。
5. **测试真实工具调用：**新建 ChatGPT 对话，输入 `@` 选择插件，要求它**只读列出默认工作区的文件**。检查 ChatGPT 是否获得工具结果、本机 **Activity** 是否留下对应记录。

**注意：**使用 Secure MCP Tunnel 时，不要把 `http://127.0.0.1:8008/mcp` 当作 ChatGPT 插件的公网地址，它只能在你自己的电脑上访问。ChatGPT 找不到 Tunnel 时，优先检查**目标 ChatGPT 工作区关联**和 **Tunnels Read + Use** 权限。

**详细操作教程：**[简体中文版](docs/CONNECT_CHATGPT.zh-CN.md) · [English](docs/CONNECT_CHATGPT.md)，包含完整的权限、常见故障与 Mac Cloudflare + OAuth 备选方案。官方参考：[Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) · [ChatGPT 自定义插件接入](https://developers.openai.com/plugins/deploy/connect-chatgpt)。

## 📦 安装与命令

```bash
npm install -g chat-to-computer-mcp
chat-to-computer-mcp install
chat-to-computer-mcp open
chat-to-computer-mcp status
```

| 命令 | 用途 |
|---|---|
| `install` | 本机编译并安装到个人版本目录。 |
| `install --rebuild` | 关闭应用后重建，保留上一版本备份。 |
| `open` | 打开桌面应用。 |
| `status` | 查询安装状态及 Mac 的 LaunchAgent 状态。 |
| `launchagent enable` | Mac 登录自启与异常退出恢复。 |
| `launchagent enable --always` | 即使正常退出，也在系统允许时重新拉起。 |
| `launchagent enable --auto-connect` | 明确开启隧道自动连接，且拒绝和原 FileMCP 服务冲突。 |
| `launchagent disable` | 只停用新项目的保活设置，不操作旧版 FileMCP。 |

### 可选的 Mac 常驻保活

```bash
chat-to-computer-mcp launchagent enable
chat-to-computer-mcp launchagent status
chat-to-computer-mcp launchagent disable
```

这是**用户登录后生效的 LaunchAgent**，不是用户未登录时也能工作的系统守护服务。`--always` 和 `--auto-connect` 均需主动选择；自动连接默认关闭。

## 🗂 多工作区和界面

1. 选定**默认工作区**，现有直接 MCP 工具依旧只作用于这个目录。
2. 通过 **Add folders…** 增加工作区，赋予持久化名称和 ID。
3. 对 Shell、Git push 等特权行为分别审批。
4. 配置跨所有工作区共享的并发命令上限（4–16）。
5. 保存后重新连接，授权变动才会对运行时生效。
6. 在 **Tasks（任务）** 页面查看任务，明确选择后才能取消相应长任务。

移除一个工作区的批准**不会删除它的文件**。父子目录重叠、通过符号链接形成的重复根目录会被拒绝。

### Mac GUI 五个页面

| 页面 | 主要功能 |
|---|---|
| **Overview** | 当前连接状态、工作区与权限摘要 |
| **Workspaces** | 工作区增删改、定位目录、权限、并发容量、可选自启 |
| **Connection** | 连接服务商、密钥、端点、连接/断开与 OAuth |
| **Tasks** | 命令任务、运行状态、刷新与定向取消 |
| **Activity** | 脱敏日志、复制、清理与故障排查 |

Windows 有自己的 WPF 控件。其编译检查已经通过，但**Windows 实机 GUI 的完整测试仍需进行**。

## 🧭 技术架构

本机网关提供 loopback-only MCP 服务；出站隧道接收兼容客户端的调用。每个工作区拥有独立的路径解析与文件操作约束，跨工作区命令共享总容量门；同步和异步命令都计入额度。

**明确的安全边界：**Mac Cloudflare OAuth 现在使用服务器签发的 Token 验证客户端，分别限制工作区及任务读取、取消权限，GUI 可撤销授权。**OpenAI Secure MCP Tunnel 仍使用共享连接凭据，不能据此识别每个独立的 ChatGPT 对话。**Shell 的 cwd 限制不是 OS 沙盒；Mac 持久化任务记录只能防止重启后误重复执行，不能重新附着原进程。参阅[OAuth 客户端权限边界](docs/VERIFIED_CLIENT_ACCESS.md)。

## 🔧 MCP 使用示例

查询用户批准的工作区：

```json
{"name":"list_workspaces","arguments":{}}
```

指定另一个已批准工作区读取文件：

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

直接调用 `read_file`、`grep`、`git_status` 等工具仍默认使用原工作区。远端客户端不能借 `workspace_call` 为自己新建授权目录。

## 🔒 权限与安全

- Shell 默认关闭；打开后，命令具有当前操作系统用户的权限。
- 普通文件和 Git 工具有目录范围控制，但 Shell 的绝对路径访问不受 cwd 守卫限制。
- 凭据由 Mac Keychain / Windows Credential Manager 管理，不写入 npm 包。
- 安装过程不会重置旧版 FileMCP 的钥匙串、代理、Profile 和 LaunchAgent。
- 逐 AI 客户端真实身份隔离与持久任务恢复尚在路线图。
- 本地编译不代表已经完成 Apple Developer ID 签名和公证。

参阅 [安全政策](SECURITY.md) 与 [架构文档](docs/ARCHITECTURE.md)。

## 🧪 平台验证

| 项目 | macOS | Windows |
|---|---|---|
| 源码构建 | **实机通过** | **GitHub Windows x64 runner 构建通过** |
| 多工作区与边界 | **专项测试通过** | **Windows x64 集成测试通过** |
| 8/12/16 并发与取消 | **Mac 测试通过** | **Windows x64 集成测试通过** |
| GUI | **150 组布局状态检查通过** | **Windows x64 WPF 冒烟测试通过** |
| npm tarball 本地安装 | **隔离 HOME 完整编译安装通过** | Windows 全新机器安装待测试 |

Windows x64 的 CI 测试确实已在 GitHub 托管的 Windows runner 上运行并通过；ARM64 安装包虽然已成功构建，但仍未进行 ARM64 原生运行及升级/重启全场景测试，用户同意暂不作为当前发行门槛。

## 🛠 源码构建与测试

```bash
bash build_macos_app.sh
bash tests/test_swift_ui.sh
bash tests/test_swift_workspace_scope.sh
bash tests/test_swift_agent_tools.sh
bash tests/test_swift_runtime.sh
npm test
npm pack --dry-run --json
```

Windows 构建入口是 `windows/FileMCP.Windows.sln`；Windows 的运行与 GUI 回归必须在 Windows 系统上完成。

自动发布方案使用 [GitHub Actions + npm OIDC Trusted Publishing](docs/PUBLISHING.md)，不把长期发布令牌放入仓库。

## 🔗 相关项目

同一维护者的其他开发者工具——**互相关联，但不是自动集成的一套运行时**：

- **[iterate-skill](https://github.com/jingzhao-l/iterate-skill)** — AI 编程助手里的多轮代码审查与修复。
- **[iterate-harness](https://github.com/jingzhao-l/iterate-harness)** — 独立、无头的迭代审查和验证引擎。
- **[iterate-plugin](https://github.com/jingzhao-l/iterate-plugin)** — dsh 客户端中的 Iterate 质量控制界面。
- **[GlassPane](https://github.com/jingzhao-l/GlassPane)** — 独立的 macOS 运行时 UI 验证工具。

ChatToComputerMCP 解决的是**云端 AI 对话如何经授权操作本地电脑**的问题，并不自动安装上述项目，也不宣称共享 Iterate 的配置或审查引擎。

## ❓ 常见问题

**它会替代 ChatGPT、Claude 吗？** 不会。它是本地工具网关。

**能代替付费的 @Remote Desktop Commander 吗？** 对于常见的本地文件管理、代码搜索、Git 和授权后执行终端命令等工作流，可以作为免费的开源替代选择；但不保证与该插件的所有功能、GUI 控制或客户端接入方式完全一致，外部云端 MCP 服务也可能有其自身费用。

**多个 AI 会话能同时执行命令吗？** 可以，在独立工作目录、共享容量和权限约束下运行；不是完全的系统级隔离。

**重启 Mac 还会运行吗？** 可以可选使用 LaunchAgent 在用户重新登录后启动；它不是开机前系统守护服务。

**Windows 已全部通过了吗？** 源码的 Windows 目标编译通过，实机测试仍待完成。

**会接管原来的 FileMCP 吗？** 不会。新安装目录和 LaunchAgent 均独立，且拒绝有冲突的自动连接。

## 📚 文档索引

| 文档 | 内容 |
|---|---|
| [ChatGPT Tunnel 中文教程](docs/CONNECT_CHATGPT.zh-CN.md) / [English](docs/CONNECT_CHATGPT.md) | Tunnel 创建、API Key、插件接入与验收 |
| [npm 安装](docs/INSTALL_NPM.md) | 环境、CLI、保活 |
| [系统架构](docs/ARCHITECTURE.md) | 客户端、连接和命令执行 |
| [工作区安全](docs/MULTI_WORKSPACE_SECURITY.md) | 授权、路由与边界 |
| [OAuth 客户端身份与权限](docs/VERIFIED_CLIENT_ACCESS.md) | 令牌验证、工作区授权、任务隔离和撤销 |
| [GUI 功能](docs/GUI_CAPABILITIES.md) | 原生界面 |
| [自动发布](docs/PUBLISHING.md) | OIDC 与版本管理 |
| [故障排查](docs/TROUBLESHOOTING.md) | 构建、连接、权限 |
| [路线图](docs/ROADMAP.md) | 已验证与未完成 |
| [兼容迁移](docs/RELEASE_MIGRATION.md) | 历史凭据与回滚 |
| [来源声明](docs/UPSTREAM_ATTRIBUTION.md) | 上游 FileMCP 与 Apache 许可 |

## 🤝 贡献与许可

欢迎提交功能、修复和文档改进，请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。发现敏感漏洞请按照 [SECURITY.md](SECURITY.md) 私密报告，不要将密钥或漏洞利用细节发到公开 Issue。

ChatToComputerMCP 基于 [anhnv02/file-mcp](https://github.com/anhnv02/file-mcp) 发展，保留原始版权、归属声明与 [Apache-2.0 许可证](LICENSE)。

本项目是**独立社区项目**，不代表 OpenAI、Anthropic 或 FileMCP 原维护团队的官方产品。
