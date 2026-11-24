# ChatToComputerMCP roadmap

## R0: Source independence
- [x] Copy original tracked code and preserve local Git history
- [x] Create private GitHub repository
- [x] Prepare focused upstream macOS concurrency PR
- [ ] Publish the independent source snapshot and verify remote bytes
- [ ] Complete an explicitly tested future binary and credentials rename

## R1: Reliable shared access
- [x] macOS multi-workspace approval and explicit scoped MCP routing
- [x] Canonical containment, shared command limit and OAuth policy binding
- [x] Native Tasks page with targeted cancellation
- [x] ChatToComputerMCP brand name, icons and independent app output
- [x] Windows multi-workspace and command task implementation; GitHub-hosted Windows x64 integration and GUI smoke verified
- [x] Mac concurrent command slots configurable 4..16 (default 8)
- [x] Cross-cwd parallel requests and overlap admission checks
- [x] Independent cancellation and bounded command output
- [x] Read-only live capacity/active-job diagnostics (no cross-client paths or commands)
- [x] macOS Cloudflare OAuth: verified token-bound client scopes, task ownership, request-ID collision protection and GUI revocation
- [ ] Authenticated per-conversation identity in the shared OpenAI Secure MCP Tunnel (no verified chat principal supplied)
- [x] Persist salted OAuth-client ownership of command receipts and support owner-verified status after full app restart\n- [x] Opt-in independent worker recovery and cancellation after gateway restart; Mac crash test passed, Windows native test added
- [x] FIFO scheduling, durable receipt reconciliation, opt-in live worker recovery and cancellation
- [x] Mac command receipt journal: durable ID tombstones, interrupted state and protected 0600/0700 audit metadata
- [x] Windows command receipt journal: durable hashed request IDs, private per-user ACL, interrupted-state and replay protection; x64 execution verification pending CI
- [x] Windows default settings, runtime credential target and tunnel-profile storage isolated from the historical FileMCP directories
- [x] Independent worker reattachment after gateway crash (not OS reboot or worker death)

## R2: Cross-platform and release readiness
- [x] Windows 4–16 shared command admission implementation; x64 GitHub Windows integration tests passed
- [ ] ChatGPT, Claude and other MCP client interoperability matrix
- [x] Optional restricted Linux container execution; actual Docker engine validation remains an acceptance gate
- [ ] Mac Developer ID/notarized release and Windows signed installer
- [x] Native task-output and exit-code viewers with folder reveal on macOS and Windows
- [x] macOS Cloudflare OAuth client inspection, explicit scope approval and revocation UI
- [ ] Per-client verified scope parity across transport modes and Windows; extended accessibility and manual interaction tests


## Packaging and optional macOS service (2026-11-22)

- [x] Source-only npm package manifest and CLI; local compilation on macOS
- [x] npm tarball extraction and clean-HOME macOS build smoke test
- [x] Optional macOS LaunchAgent commands for explicit opt-in/disable
- [x] Native macOS GUI controls for next-login startup, reconnect, always-on
- [x] Windows per-user login supervisor, optional auto-connect and always-on, GUI/CLI controls (real Windows runtime gate pending CI)
- [x] Mac AppKit UI regression and .NET Windows-targeting compilation
- [x] GitHub-hosted Windows x64 integration, native WPF GUI smoke and x64/ARM64 packaging at commit 79a1abb (run 45028574644)
- [ ] Windows ARM64 native execution and upgrade/restart matrix: unverified, owner waived as current release gate due to unavailable device
- [ ] npm Registry public publication (npm account authentication required)
- [ ] Cryptographically signed/notarized public macOS distribution
- [ ] Authenticated per-client scopes beyond shared tunnel credentials
- [x] Independent worker and cross-gateway-restart reattachment (explicit opt-in)


## Native update channel (0.3.0)

- [x] CLI update check/install/status/enable/disable with shared preferences
- [x] Official npm stable SemVer validation and installer success verification
- [x] Versioned builds with atomic macOS active-release pointer
- [x] macOS and Windows GUI update buttons, optional automatic checks/installation while open
- [x] No forced app, tunnel or AI task restart during update
- [ ] Independent always-on updater daemon (intentionally not installed)
- [ ] Signed public installer (independent from the implemented worker recovery)


## Cross-platform parity evidence

See [Platform parity audit](PLATFORM_PARITY.md). Shared MCP tool names do not prove semantic equivalence or persistent task supervision. Live account verification, Windows ARM64 hardware and signed distribution remain separate acceptance gates.

## R3: Direct background computer use (0.6.0)

- [x] Native bounded Cua Driver integration, explicit app grants, observation/verification and durable action receipts on both platforms
- [x] Optional independent browser and Guest relay, Guest disabled by default
- [x] Reject foreground/Guest fallback, integrate CUA into idle update admission
- [ ] Expand native application compatibility and measure user-desktop non-interference on real Mac/Windows apps
- [ ] Real Windows ARM64 and physical VM lifecycle acceptance; verified per-conversation identity remains dependent on trusted clients
