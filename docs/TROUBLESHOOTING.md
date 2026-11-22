# Troubleshooting

Use `chat-to-computer-mcp status` first. Never paste access tokens, Keychain exports, API keys, private configuration, tunnel profiles, raw logs with secrets, or a full working-directory archive into a public issue.

## Installation

**`npx` says the package cannot be found**

Verify the version and registry with `npm view chat-to-computer-mcp version`. For a pre-publication release candidate, install the checksum-verified GitHub tarball instead. A repository tag alone does not publish to npm.

**The Mac app is not installed**

Run `chat-to-computer-mcp status`. The release-specific bundle should be at `~/.local/share/chat-to-computer-mcp/releases/<version>/ChatToComputerMCP.app`. Install compiles locally and requires Swift/Xcode Command Line Tools.

**Windows build fails on missing SDK or PowerShell**

Use .NET **8 SDK** and Windows PowerShell on the intended Windows platform. Cross-compiling a Windows-targeted WPF project on macOS checks source/dependencies, but not Windows process behavior.

**An old app is still running**

The new package deliberately does not replace an active `local.filemcp` LaunchAgent. Keep the old app operational until a user-authorized, signed and rollback-protected migration is ready.

## Connection

For a step-by-step setup of the OpenAI Platform Tunnel ID, restricted runtime
API key, ChatGPT workspace association and custom Plugin creation, start with
[Connect ChatGPT](CONNECT_CHATGPT.md) or
[简体中文：配置 ChatGPT Tunnel](CONNECT_CHATGPT.zh-CN.md).


**MCP endpoint unreachable**

Verify the selected tunnel provider, profile and credentials, then check the local runtime's connection status and Activity logs. Health listeners should be loopback-only. Remote MCP tools may require connection setup inside the client application.

**A new signed Mac app is waiting for Keychain access**

macOS may prompt again after a code-signing identity change. Review and approve legitimate OS prompts on the Mac itself. Never edit Keychain ACLs to bypass authorization or disclose credentials in chat.

**Auto-connect refuses to turn on**

Check whether the original `local.filemcp` LaunchAgent is loaded. The new installation refuses potentially competing profiles by default. Configure a genuinely distinct profile and perform a deliberate migration before enabling automatic connections.

## Workspaces and commands

**`workspace_call` says a workspace is unknown**

The user must approve that exact folder in Workspaces, save configuration and reconnect. The default workspace remains accessible via direct tools.

**A command is rejected because a directory is occupied**

Two active commands cannot safely share the same cwd tree. Use distinct Git worktrees or await/cancel the owning command. The total command capacity is configurable (4–16, default 8).

**A command reaches outside its cwd**

A shell is not sandboxed. Disable Shell permission for clients you do not trust; use OS-level isolation for untrusted automation.

**A command session is “unknown or expired”**

The runtime retains bounded history; a restart or session retention limit may expire older IDs. Do not blindly repeat a non-idempotent command because a receipt is missing—check the actual files and processes first.

## Background service on Mac

```bash
chat-to-computer-mcp launchagent status
chat-to-computer-mcp launchagent disable
```

Default `enable` starts after future user login and restarts abnormal exits. `--always` also reopens after ordinary Quit. To stop repeated launch attempts, disable the independent new service. The original FileMCP agent is not touched.

## Release and automated publishing

**Manual npm publish returns EOTP**

Being logged in is not enough for a publish requiring 2FA. Run the regular
`npm publish` command interactively and complete npm's browser challenge.
Do not send an OTP through chat, disable 2FA or use a bypass token.

**The CLI reports success but npm only shows `0.0.0-stage`**

A first-time direct publish may still be processing in npm's malware scanning
queue. Check `npm stage list chat-to-computer-mcp`. If it is empty, do not
interpret the placeholder as a manual stage awaiting approval. Check that
version `0.1.0` becomes installable and `latest` points at it; don't upload
the same version again while processing.

**OIDC publish says “Unable to authenticate”**

Verify the npm package's Trusted Publisher owner, repository and exact `publish-npm.yml` filename; select **Allow npm publish**. GitHub-hosted runners are required, not self-hosted. See [Publishing](PUBLISHING.md).

**GitHub Verify jobs fail without starting**

A known GitHub Actions account/billing condition can reject all hosted runners before any test starts. Check the job annotations and GitHub Billing & plans; a “failure” without a runner and steps is not a source test failure.

**npm version already exists**

npm versions are immutable. Do not force overwrite or unpublish to reuse a version. Update `package.json`, test and publish a new version/tag instead.
