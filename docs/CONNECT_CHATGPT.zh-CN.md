# 将 ChatToComputerMCP 连接到 ChatGPT

这份教程让 **ChatGPT 云端对话**通过 **OpenAI Secure MCP Tunnel** 使用 Mac / Windows 电脑上经你授权的文件、Git 与可选命令工具。**无需开放公网入站端口，也无需手动安装第二个 tunnel-client。**

官方参考：[OpenAI Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) · [ChatGPT 自定义 MCP 插件连接教程](https://developers.openai.com/plugins/deploy/connect-chatgpt)。

> **ChatGPT 订阅与 OpenAI Platform API 是两套权限和服务。** 配置 Secure MCP Tunnel 需要 Platform 组织下的 Tunnel ID、专用运行时 API Key 与对应权限；ChatGPT 账户或工作区还必须允许添加自定义 MCP 插件。不要把 ChatGPT 订阅当成自动赠送的 Platform Tunnel API Key。

## 第一步：安装并打开客户端

```bash
npx --yes chat-to-computer-mcp@latest install
npx --yes chat-to-computer-mcp@latest open
```

npm 包在你的电脑上编译应用。需要 Node.js 20+；Mac 需安装 Swift/Xcode Command Line Tools，Windows 需 .NET 8 SDK 和 Windows PowerShell。

在应用的 **Workspaces（工作区）** 里选择默认目录，也可以添加其他单独批准的文件夹。建议初次连接时**不要打开 Shell**，先验证只读文件操作。保存工作区权限。

## 第二步：在 OpenAI Platform 创建 Tunnel

1. 打开 [OpenAI Platform → Tunnel 设置](https://platform.openai.com/settings/organization/tunnels)，选择正确的 **Platform 组织**。
2. 创建新的 **Secure MCP Tunnel**，或使用合适的现有 Tunnel。复制它的 **Tunnel ID**。格式示例（不是有效凭据）：`tunnel_0123456789abcdef0123456789abcdef`。
3. **把这个 Tunnel 关联到目标 ChatGPT 工作区**，并关联需要使用它的 Platform 组织。只绑定到其他组织的 Tunnel 不一定能在当前 ChatGPT 工作区中选到。
4. 在 Platform 创建**权限受限的运行时 API Key**，仅赋予使用 Tunnel 必需的权限。**创建/管理 Tunnel** 需要 **Tunnels Read + Manage**；**运行/使用 Tunnel** 需要 **Tunnels Read + Use**。这些是组织级权限；没有权限请联系组织管理员。
5. **不要**把 API Key 放入 GitHub、聊天窗口、命令行参数、日志或截图。

官方说明：[Secure MCP Tunnel 的权限、关联与网络要求](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)。

## 第三步：在 ChatToComputerMCP 配置连接

打开本机应用的 **Connection（连接）** 页面：

| 设置项 | 输入内容 |
|---|---|
| **Provider** | `OpenAI Secure MCP Tunnel` |
| **Tunnel ID** | 第二步获得的真实 Tunnel ID |
| **Runtime API key** | 刚创建的 **OpenAI Platform** 运行时密钥 |
| **Profile**（高级） | 一般保留默认配置；多个安装实例必须避免 Profile 冲突 |
| **MCP port**（高级） | 默认 `8008`，本机端点是 `127.0.0.1:8008/mcp` |
| **Health listener**（高级） | 默认 `127.0.0.1:0`，仅本地监听且自动分配端口 |

点击 **Save connection（保存连接）**，再点击 **Connect（连接）**。等待状态显示 **Connected**，遇到错误请打开 **Activity（活动/日志）** 查看原因。

应用已经自动管理内置的 `tunnel-client init`、`doctor` 和 `run`；**不需要自己再开一个 tunnel-client 进程**。密钥由 Mac Keychain 或 Windows Credential Manager 保存，不是明文设置文件。

**不要把 `http://127.0.0.1:8008/mcp` 填到 ChatGPT 的服务器地址里。** 这是只有用户本机能访问的地址；使用 OpenAI Secure MCP Tunnel 时，ChatGPT 需要选择的是 **Tunnel ID**。

## 第四步：在 ChatGPT 添加自定义 MCP 插件

1. 在与 Tunnel 关联的目标工作区中打开 [ChatGPT Plugins](https://chatgpt.com/plugins)。
2. 点击 **+ → Add custom MCP server（添加自定义 MCP 服务器）**。各账户的具体按钮文案可能不同。
3. 例如把插件命名为 **ChatToComputerMCP — My Computer**。
4. 在 **Connection** 里选择 **Tunnel**，从列表选中该 Tunnel，或者粘贴相同的 Tunnel ID。
5. 完成页面要求的身份验证设置，阅读风险提示，选择 **I understand and want to continue**，然后点击 **Create as a plugin**。
6. 检查 ChatGPT 成功发现的工具。只有权限和工具范围符合你的预期时才启用。

如果没有“添加自定义 MCP 服务器”选项，可能是**账户资格、工作区管理政策或插件权限**限制；如果看不到 Tunnel，先检查 Platform 的工作区关联和 **Tunnels Read + Use**。

此流程以 [OpenAI 官方 ChatGPT 插件连接文档](https://developers.openai.com/plugins/deploy/connect-chatgpt) 为准。

## 第五步：实际进行一次只读调用

新开一个 ChatGPT 对话，输入 `@` 并选择刚创建的 ChatToComputerMCP 插件，然后发送：

> 使用 ChatToComputerMCP 查看默认工作区，列出顶层文件。不要修改任何文件，也不要执行命令。

如果已经批准了多个工作区：

> 先调用 `list_workspaces`，再通过 `workspace_call` 读取我指定的第二个工作区中的 `README.md`。

必须确认**ChatGPT 确实调用了 MCP 工具**并拿到了返回值，同时本机 **Activity** 能看到对应调用。仅看到应用 **Connected**，不能算整个 ChatGPT 连接链路已验收成功。

之后才逐步尝试经你批准的文件编辑、Git 或 Shell 工作。使用过程中保持本机程序在线；如果改了工具或权限，应重新连接并在 ChatGPT 的插件设置中刷新工具元数据。

## 备选方式：Cloudflare Tunnel（目前仅 Mac）

如果不使用 OpenAI 私有 Tunnel，也可以通过你自己的 Cloudflare Tunnel 公网 HTTPS 域名接入：

1. 安装 `cloudflared`（例如 `brew install cloudflared`），它不包含在本项目安装包中。
2. 在 Cloudflare 配置 Tunnel，把整个域名转发至本地 MCP 服务（通常为 `http://127.0.0.1:8008`），需覆盖 `/mcp`、`/oauth/*`、`/.well-known/*` 等路径，**不能只代理 `/mcp`**。
3. 在应用 **Connection** 里选择 Cloudflare 连接方式，设置 Tunnel/令牌和公网 Hostname。
4. 连接成功后复制应用显示的 `https://your-hostname.example/mcp`。
5. 在 ChatGPT 的自定义 MCP 服务器配置中选 **URL**，粘贴完整 HTTPS 地址，优先采用 **OAuth + 动态客户端注册（DCR）**。
6. 浏览器打开**你自己的 ChatToComputerMCP 授权页面**时，从本机应用复制登录码，确认权限后授权。

Cloudflare 方式**不需要 OpenAI Tunnel ID 和对应运行时 API Key**。不推荐启用“把密钥放进 MCP URL”的兼容模式：获得完整 URL 的人可能直接调用已开放工具。

**客户端独立授权：**Cloudflare OAuth 授权页现在会让用户选择该客户端
能够访问的已批准工作区，默认仅授权默认工作区。Mac Connection 界面
可以查看或撤销客户端的 OAuth 令牌。OpenAI Secure MCP Tunnel 的共享
连接认证头仍不能独立认证每个 ChatGPT 对话。


官方参考：[Cloudflare Tunnel 文档](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/)。

## 常见故障

| 现象 | 排查重点 |
|---|---|
| 不能创建 Tunnel | Platform 组织权限不足，创建需要 **Tunnels Read + Manage** |
| ChatGPT 列不出 Tunnel | 没有关联当前 **ChatGPT 工作区**，或缺少 **Tunnels Read + Use** |
| 找不到“添加自定义 MCP 服务器” | ChatGPT 账户资格与工作区管理员权限 |
| 应用连接失败 | Tunnel ID、运行时 API Key、出口 HTTPS 网络以及 **Activity** 日志 |
| Connected 但调用失败 | ChatGPT 插件 Tunnel ID 是否相同、权限是否已批准、是否真的触发工具调用 |
| 本地端口冲突 | 不要让旧 FileMCP 和新应用共用 MCP 端口/Profile |
| 目录被其他命令占用 | 为并发会话使用不同目录或 Git worktree；默认 8，最大 16 个命令槽 |
| Mac 登录后想自动连接 | 需要主动启用**独立的可选 LaunchAgent**，自动连接默认关闭 |

**网络要求：** 本机 `tunnel-client` 需要连接到 `api.openai.com:443`（使用 mTLS 时可能为 `mtls.api.openai.com:443`）的**出站 HTTPS**；无需路由器的入站端口转发。

**并发区别：** OpenAI 文档中的 Tunnel 默认限制包括 **600 请求/分钟**、**10 MiB 单次序列化数据**、**120 秒请求期限**、**1,000 个待处理请求**；它们与应用本地可配置的 **4–16 个同时运行的命令任务**不是同一种指标。[官方限制](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)。

**安全提示：** 目前不同 ChatGPT 对话仍可能共用 Tunnel 授权，尚未提供逐对话的加密身份隔离。Shell 的 cwd 保护不等于 OS 沙盒。

进一步阅读：[项目故障排查](TROUBLESHOOTING.md) · [工作区与权限](MULTI_WORKSPACE_SECURITY.md) · [ChatGPT 官方接入说明](https://developers.openai.com/plugins/deploy/connect-chatgpt)。
