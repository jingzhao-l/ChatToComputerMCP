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
- [x] Windows multi-workspace and command task implementation (cross-target compilation verified; Windows real-device validation pending)
- [x] Mac concurrent command slots configurable 4..16 (default 8)
- [x] Cross-cwd parallel requests and overlap admission checks
- [x] Independent cancellation and bounded command output
- [x] Read-only live capacity/active-job diagnostics (no cross-client paths or commands)
- [ ] Per-client authenticated session ownership and disconnect recovery
- [ ] Fair scheduling, durable task recovery and cancellation receipts

## R2: Cross-platform and release readiness
- [x] Windows 4–16 shared command admission implementation (Windows device regressions pending)
- [ ] ChatGPT, Claude and other MCP client interoperability matrix
- [ ] Hardened process-level isolation for untrusted workspaces
- [ ] Mac Developer ID/notarized release and Windows signed installer
- [ ] Polished native identity, status and approval UX


## Packaging and optional macOS service (2026-11-22)

- [x] Source-only npm package manifest and CLI; local compilation on macOS
- [x] npm tarball extraction and clean-HOME macOS build smoke test
- [x] Optional macOS LaunchAgent commands for explicit opt-in/disable
- [x] Native macOS GUI controls for next-login startup, reconnect, always-on
- [x] Mac AppKit UI regression and .NET Windows-targeting compilation
- [ ] Windows real-device full integration/GUI check on Windows runner
- [ ] npm Registry public publication (npm account authentication required)
- [ ] Cryptographically signed/notarized public macOS distribution
- [ ] Authenticated per-client scopes beyond shared tunnel credentials
- [ ] Durable running-process supervisor and actual cross-restart task reattachment
