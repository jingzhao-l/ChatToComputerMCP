# macOS / Windows functional parity audit

Audited against the independent ChatToComputerMCP source tree, 2026-11-24.
Matching Swift and C# tool names do not guarantee equivalent execution.
Native test results and unverified external steps are kept distinct.

| Capability | macOS | Windows | Evidence and limits |
| --- | --- | --- | --- |
| 33 scoped MCP tool names plus routing tools | Implemented | Implemented | Shared base input/output schemas, annotations and permission profiles; native integration suites check behaviors |
| File/Git operations, search, repo overview, batch reading | Implemented | Implemented | Swift runtime/agent and Windows native tests |
| 4–16 concurrent slots, workspace isolation and task cancellation | Implemented | Implemented | Independent native tests; no claim of unlimited concurrency |
| Multiple approved workspaces and GUI management | Implemented | Implemented | Native GUI and integration suites |
| Opt-in Shell, Git push and Codex history writes | Implemented | Implemented | Default-off, separately enforced at runtime |
| OpenAI Secure MCP Tunnel | Implemented | Implemented | Requires user Platform credentials |
| Cloudflare remotely managed tunnel token | Implemented | Implemented in main | Windows token-file process tests, not live Cloudflare account verification |
| Cloudflare named tunnel management | Implemented | Implemented in main | Fake Cloudflare CLI process integration; real account test still needed |
| Cloudflare OAuth PKCE and per-client permissions | Implemented | Implemented in main | Real Windows HTTP test with two OAuth clients, OAuth discovery metadata/challenge and response security headers; not WAN provider E2E |
| Credentials | macOS Keychain | Windows Credential Manager | Windows product namespace is independent; Mac still uses legacy identity for credential continuity |
| Login start, auto-connect, crash recovery, always-on | launchd user LaunchAgent | HKCU user startup supervisor | Explicit opt-in; not pre-login Windows system services |
| npm local source installer and updater | Implemented | Implemented | Shared CLI and local native compile |
| GUI check/install update | Implemented | Implemented | AppKit and WPF tests; never restarts active jobs |
| Durable command receipts and replay prevention | Implemented | Implemented | Incomplete commands marked interrupted, not replayed |
| Restart-safe owner-scoped command receipt status | Implemented | Implemented | New command_receipt_status tool binds salted owner identities to authenticated clients; receipt alone is status only; opted-in live workers support reattachment |
| Independent task execution and reattachment after gateway crash | Implemented; SIGKILL test passed | Implemented; Windows x64 native regression executed | Opt-in; OS reboot or worker death is interrupted, not replayed |
| Restricted Linux container shell | Implemented | Implemented | Docker Desktop and approved image required; actual engine validation pending |
| Seven-area native GUI | Implemented | Implemented | 210 Mac layout cases, native x64 WPF smoke; manual device acceptance pending |
| Per-ChatGPT-conversation cryptographic identity in shared OpenAI Tunnel | NOT available | NOT available | Shared tunnel credential is not a per-conversation principal |
| Bounded HTTP connections and absolute read/write deadlines | 32 / 15s / 15s | 32 / 15s / 15s | Windows C# real loopback and blocked OS write regression on Mac and Windows x64/ARM64; native CI gate |
| CPU-specific base tool packaging | Apple Silicon / Intel | x64 / ARM64 | Official pinned vendor binaries; Intel Rosetta version checks and native Intel hosted build/runtime gate passed at 9743769; physical-device acceptance separate |
| Windows ARM64 native execution | N/A | Native hosted CI passed at 9743769 | Native base runtime, app and installer checks; physical-device and CUA acceptance remain separate |
| Signed/native notarized public installers | NOT completed | NOT completed | Source-only npm distribution is not signed native distribution |

## Windows parity implementation — 2026-11-24

The Windows runtime now probes the bundled tunnel client's
`control_plane_poll` metric using the per-run private health locator and
does not equate a spawned process with a connected cloud service. The
managed login auto-connect mode checks on resume and recovers a stale
transport after a grace period and cooldown while preserving the local
gateway. Automatic idle restart does not interrupt active or queued jobs.
Explicit proxy overrides take precedence over a Windows manual system
proxy. Registration and secret redaction handle split output chunks.

Windows scoped mutation checks now distinguish sibling working
directories from overlapping targets, including mixed-path patches,
copy/move endpoints and directory deletion. The named profile mutex is
acquired and released on its owning thread across async runtime transitions.

Windows desktop controls now include startup cancellation, copied scope
instructions, light/dark preference, saved window placement, compact
navigation, log follow/copy/clear, configuration locking and cancellable
Cloudflare account switching with certificate backup/rollback.

Evidence: Windows-targeting Release solution builds with warnings as
errors; the portable C# policy regression executes on the development Mac
and checks poll freshness/invalid metrics, proxy precedence, fragmented
secret redaction and Cloudflare registration parsing. The native Windows
suite additionally contains actual process/reconnect, scoped mutation,
profile mutex and Cloudflare login rollback fixtures. The v0.5.1 native Windows gate passed (run 45337700732); the v0.6.0 Windows CUA regression initially failed. The v0.6.1 Verify run 45360668564 passed both native jobs. Hosted CI evidence is not full interactive physical-device acceptance. This section describes pre-CUA Windows parity work. See the following section for the integrated CUA backend.

## Native CUA (0.6.0+)

Both platforms implement 15 CUA MCP tools in addition to the existing 33
coding tools and routing/identity tools. Native driver input schemas differ
where platform capabilities differ. Both gateways support window handoff, visual-only observations, optional post-action observation, driver preflight and foreground-event observation. CUA protocol tests and pinned real-driver read-only probes are required by the four-architecture release gates; these are not interactive application acceptance. Native background control is preferred;
no automatic foreground or Guest fallback. Guest is disabled by default and
requires local interactive enablement. The real Chromium and native
loopback guest relay tests are not full desktop or VM acceptance.
See [CUA.md](CUA.md) for the exact platform limitations and opt-in requirements.

## Acceptance gates

Before describing Mac/Windows as fully equivalent, finish native transport
interoperability with real ChatGPT/Claude clients, live Cloudflare account
validation, keyboard and accessibility checks, actual Docker execution,
native Windows worker recovery and ARM64 runtime/restart tests,
and signed/notarized distribution milestones where relevant.

OS-specific implementations correctly differ (LaunchAgent vs HKCU,
Keychain vs Credential Manager). The product parity target is equal user
capability and verified security properties, not identical source code.

The static tool-name guard and reviewed shared base contract cannot substitute
for either native runtime suite. The generated [base inventory](BASE_TOOL_CONTRACT.md)
reports 27 default, 36 fully enabled and 16 read-only base tools for one
default root (including three routing/identity tools). These counts are not
the separately maintained 15 CUA tool names. Check both platform fixtures
with the same contract before changing an interface.

### Evidence links

- [v0.5.1 native release quality gates](https://github.com/jingzhao-l/ChatToComputerMCP/actions/runs/45337700732)
- [v0.6.0 initial failed Windows gate](https://github.com/jingzhao-l/ChatToComputerMCP/actions/runs/45356657950)
- [v0.6.1 native verification](https://github.com/jingzhao-l/ChatToComputerMCP/actions/runs/45360668564)

New HTTP and Intel gates must be read at their own commit. The previous
successful run does not verify changes made after that source snapshot.
