# macOS / Windows functional parity audit

Audited against the independent ChatToComputerMCP source tree, 2026-11-23.
Matching Swift and C# tool names do not guarantee equivalent execution.
Native test results and unverified external steps are kept distinct.

| Capability | macOS | Windows | Evidence and limits |
| --- | --- | --- | --- |
| 31 MCP tool names | Implemented | Implemented | Source guard checks names; native integration suites check runtime behaviors |
| File/Git operations, search, repo overview, batch reading | Implemented | Implemented | Swift runtime/agent and Windows native tests |
| 4–16 concurrent slots, workspace isolation and task cancellation | Implemented | Implemented | Independent native tests; no claim of unlimited concurrency |
| Multiple approved workspaces and GUI management | Implemented | Implemented | Native GUI and integration suites |
| Opt-in Shell, Git push and Codex history writes | Implemented | Implemented | Default-off, separately enforced at runtime |
| OpenAI Secure MCP Tunnel | Implemented | Implemented | Requires user Platform credentials |
| Cloudflare remotely managed tunnel token | Implemented | Implemented in main | Windows token-file process tests, not live Cloudflare account verification |
| Cloudflare named tunnel management | Implemented | Implemented in main | Fake Cloudflare CLI process integration; real account test still needed |
| Cloudflare OAuth PKCE and per-client permissions | Implemented | Implemented in main | Real Windows HTTP test with two OAuth clients, not WAN provider E2E |
| Credentials | macOS Keychain | Windows Credential Manager | Independent new product namespace |
| Login start, auto-connect, crash recovery, always-on | launchd user LaunchAgent | HKCU user startup supervisor | Explicit opt-in; not pre-login Windows system services |
| npm local source installer and updater | Implemented | Implemented | Shared CLI and local native compile |
| GUI check/install update | Implemented | Implemented | AppKit and WPF tests; never restarts active jobs |
| Durable command receipts and replay prevention | Implemented | Implemented | Incomplete commands marked interrupted, not replayed |
| Restart-safe owner-scoped command receipt status | Implemented | Implemented | New command_receipt_status tool binds salted owner identities to authenticated clients; status only, no process reattachment |
| True background task execution reattachment after crash/reboot | NOT implemented | NOT implemented | Requires independent task supervisor, not just receipts |
| Per-ChatGPT-conversation cryptographic identity in shared OpenAI Tunnel | NOT available | NOT available | Shared tunnel credential is not a per-conversation principal |
| Windows ARM64 native execution | N/A | NOT tested | ARM64 package cross-compile alone cannot verify runtime |
| Signed/native notarized public installers | NOT completed | NOT completed | Source-only npm distribution is not signed native distribution |

## Acceptance gates

Before describing Mac/Windows as fully equivalent, finish native transport
interoperability with real ChatGPT/Claude clients, live Cloudflare account
validation, keyboard and accessibility checks, persistent background task
supervision and reattachment, native Windows ARM64 runtime/restart tests,
and signed/notarized distribution milestones where relevant.

OS-specific implementations correctly differ (LaunchAgent vs HKCU,
Keychain vs Credential Manager). The product parity target is equal user
capability and verified security properties, not identical source code.

The static tool-name guard cannot substitute for either native runtime suite.
