# Connect ChatToComputerMCP to ChatGPT

This walkthrough connects **ChatGPT cloud conversations** to approved files and tools on your Mac or Windows PC through **OpenAI Secure MCP Tunnel**, without opening a public inbound port or manually starting a second tunnel-client.

**Official references:** [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) · [Connect a custom MCP Plugin](https://developers.openai.com/plugins/deploy/connect-chatgpt).

> A ChatGPT subscription and an OpenAI Platform API key are **not the same thing**. Using a private Secure MCP Tunnel requires a Platform runtime key and appropriate tunnel permissions. Your ChatGPT account/workspace must also allow custom MCP Plugins; account and admin restrictions may apply.

## 1. Install and open the app

```bash
npx --yes chat-to-computer-mcp@latest install
npx --yes chat-to-computer-mcp@latest open
```

The npm installer compiles a native application locally. You need Node.js 20+, plus Xcode Command Line Tools/Swift on macOS or .NET 8 SDK and Windows PowerShell on Windows.

In **Workspaces**, select a default local directory and optionally add separately approved folders. Leave Shell disabled until you explicitly need it. Save workspace permissions.

## 2. Create the OpenAI tunnel

1. Open [OpenAI Platform → Tunnel settings](https://platform.openai.com/settings/organization/tunnels) and select the intended Platform organization.
2. Create a Secure MCP Tunnel, or reuse a suitable tunnel. Copy its **Tunnel ID**, for example **format only**: `tunnel_0123456789abcdef0123456789abcdef`. The actual ID must be issued by Platform.
3. **Associate that tunnel with the intended ChatGPT workspace** as well as the appropriate Platform organization. A tunnel associated only with a different organization may not be selectable in ChatGPT.
4. Create a *restricted Platform runtime API key* for the tunnel. **Creating/managing** a tunnel requires **Tunnels Read + Manage**; **running/using** it requires **Tunnels Read + Use**. These are organization-level privileges; ask the organization owner/admin if unavailable.
5. Keep the runtime key secret. Do not paste it into ChatGPT, a GitHub issue, a screenshot, command arguments or this README.

See [OpenAI's official tunnel permissions and workspace association](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).

## 3. Configure ChatToComputerMCP

Open **Connection** in the native app. Enter:

| App field | Value |
|---|---|
| **Provider** | **OpenAI Secure MCP Tunnel** |
| **Tunnel ID** | The exact ID created or selected in Platform |
| **Runtime API key** | The restricted OpenAI **Platform** key from step 2 |
| **Profile** (advanced) | Leave the app's default unless deliberately isolating multiple installs |
| **MCP port** (advanced) | Default `8008`, used by the local HTTP server at `127.0.0.1:8008/mcp` |
| **Health listener** (advanced) | Default `127.0.0.1:0`, loopback-only with an automatically chosen port |

Choose **Save connection**, then **Connect**. Wait for **Connected** and inspect **Activity** for errors.

The app already uses the bundled `tunnel-client init`, `doctor` and `run` lifecycle; **you do not need to download or start another tunnel-client**. The Platform API key is held in macOS Keychain or Windows Credential Manager, not plain-text settings.

**Do not enter `http://127.0.0.1:8008/mcp` in ChatGPT**. That is a *local* listener. ChatGPT connects by selecting the **Tunnel ID**, not by reaching your computer's localhost.

## 4. Add a custom MCP Plugin in ChatGPT

1. Open [ChatGPT Plugins](https://chatgpt.com/plugins) in the **same ChatGPT workspace** associated with your tunnel.
2. Select **+ → Add custom MCP server** (labels can differ across product versions).
3. Give it a recognizable name, e.g. **ChatToComputerMCP — My Computer**.
4. Under **Connection**, choose **Tunnel** and select the available tunnel or paste its **Tunnel ID**.
5. Complete any authentication options, review the risk warning and select **I understand and want to continue** → **Create as a plugin**.
6. Review the discovered tools. Your chosen ChatGPT account/workspace must permit the creation and use of custom MCP connections.

If ChatGPT cannot list the tunnel, first verify the Platform organization, **target ChatGPT workspace association**, and **Tunnels Read + Use** role. For the current UI see [OpenAI's ChatGPT Plugin guide](https://developers.openai.com/plugins/deploy/connect-chatgpt).

## 5. Confirm the real connection

In a **new ChatGPT chat**, type `@` and select your ChatToComputerMCP Plugin. Start with a read-only request:

> Use ChatToComputerMCP to show my default workspace and list its top-level files. Do not edit anything or run commands.

For multiple approved workspaces, ask it to call `list_workspaces`, then `workspace_call` to read `README.md` in an explicitly selected scope.

Confirm that ChatGPT actually calls the tools and that the app's **Activity** page records the call. **Connected alone does not mean end-to-end verification passed.** Keep the app running for future calls.

## Optional alternative: Cloudflare Tunnel (macOS)

The Mac app also supports Cloudflare if you prefer a public HTTPS hostname rather than OpenAI's private Tunnel:

1. Install `cloudflared` (e.g. `brew install cloudflared`); it is not bundled.
2. Configure a Cloudflare tunnel and public hostname to route to the local MCP server (usually `http://127.0.0.1:8008`). Route the **whole hostname**, including `/mcp`, `/oauth/*` and `/.well-known/*`.
3. In the native **Connection** page select a Cloudflare provider. Enter your named tunnel or dashboard token and the public hostname.
4. Connect, then copy the app's HTTPS MCP URL: `https://your-hostname.example/mcp`.
5. In ChatGPT Add custom MCP server, choose **URL** instead of Tunnel, enter the full HTTPS URL and use **OAuth with dynamic client registration**.
6. When your ChatToComputerMCP authorization page appears, enter its login code copied from the native app, review permissions and authorize only your own requested connection.

The Cloudflare route does **not** use an OpenAI Tunnel ID or OpenAI Platform runtime key. Avoid **URL-secret compatibility mode** unless necessary: anyone with that full secret URL may call the enabled tools.

**Per-client scopes:** the local Cloudflare OAuth approval page now asks the
user which approved workspaces the verified client may access; the default
workspace is selected by default. macOS Connection can inspect and revoke
approved client tokens. The shared Secure MCP Tunnel header does not provide
equivalent per-conversation identity isolation.


See [Cloudflare Tunnel documentation](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/).

## Troubleshooting checklist

| Symptom | Likely issue / what to check |
|---|---|
| Cannot create tunnel | Platform role lacks **Tunnels Read + Manage** |
| Tunnel missing from ChatGPT | Missing **target ChatGPT workspace** association or **Tunnels Read + Use** |
| Cannot add a custom MCP Plugin | ChatGPT account/workspace/admin restrictions |
| App tunnel connection fails | Wrong Tunnel ID or runtime Platform API key; check **Activity** |
| App Connected but tools fail | Confirm matching Plugin Tunnel ID, local permissions and an actual ChatGPT tool call |
| Port is in use | Select another local MCP port; avoid running old FileMCP/new ChatToComputerMCP on the same profile/port |
| Commands conflict | Use separate working directories or Git worktrees; capacity is 4–16 (default 8) |
| Need automatic start | Enable the **separate optional** macOS LaunchAgent; automatic tunnel connection is off by default |

**Network:** Secure MCP Tunnel needs **outbound HTTPS** to `api.openai.com:443` (or `mtls.api.openai.com:443` with mTLS) and local MCP reachability, **not inbound port forwarding**.

**Limits:** Current documented defaults include 600 requests/minute, 10 MiB serialized request/response, 120 seconds per request and a 1,000-request pending queue; these are **tunnel limits**, separate from ChatToComputerMCP's 4–16 local command slots. See [OpenAI's current limits](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).

**Security:** Several connected chats can still share a tunnel identity. Cryptographically verified per-chat permissions and full OS shell sandboxing are **not** shipped. Enable Shell only for trusted AI workflows.

More help: [General troubleshooting](TROUBLESHOOTING.md) · [Permissions and path scope](MULTI_WORKSPACE_SECURITY.md) · [OpenAI connection guide](https://developers.openai.com/plugins/deploy/connect-chatgpt).
