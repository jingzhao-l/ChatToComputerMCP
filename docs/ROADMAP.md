# ChatToComputerMCP roadmap

Current source is publicly maintained at
[GitHub](https://github.com/jingzhao-l/ChatToComputerMCP); stable source
packages are published to [npm](https://www.npmjs.com/package/chat-to-computer-mcp).
The [releases](https://github.com/jingzhao-l/ChatToComputerMCP/releases)
and npm registry report distribution status; a Git tag is not publication.
The [changelog](../CHANGELOG.md) preserves historical milestones.

## R0 — Source and distribution independence
- [x] Preserve original tracked code, ancestry and attribution locally; publish independent source snapshot and upstream contribution.
- [x] Public repository and npm OIDC Trusted Publishing; immutable tags; native quality gates and registry read-back before GitHub release.
- [x] Versioned native source builds, optional user-login supervision, idle update activation and rollback-safe retained releases.
- [ ] Complete explicit macOS application/credential/profile namespace migration without replacing the working legacy identity or credentials.
- [ ] Developer ID notarization and signed Windows installer distribution (separate from local source builds).

## R1 — Reliable shared access
- [x] Dual-platform multi-workspace direct routing, live idle catalog edits, canonical containment and per-workspace permissions.
- [x] Shared 4–16 command capacity, bounded FIFO queue, conflict protection, targeted cancellation, output cursors and durable receipts.
- [x] Dual-platform local command history and structured activity audit, content capture opt-in.
- [x] Verified Cloudflare OAuth client scopes, ownership, request-ID collision protection, revocation and restart-safe receipt status.
- [x] Opt-in live independent worker reattachment after gateway restart; no replay after worker loss or OS reboot.
- [x] Restricted Linux execution with explicit immutable local Docker image and no host fallback.
- [ ] Platform-verified per-conversation identity in shared OpenAI Secure MCP Tunnel (requires trusted client/platform claims).
- [ ] Real Docker engine isolation and actual client/provider interoperability acceptance.

## R2 — Cross-platform and quality governance
- [x] Seven native GUI areas, responsive navigation, preference persistence, login supervision and safe updater controls.
- [x] Cloudflare named/token tunnel, OAuth, control-plane freshness checks, resume and idle recovery on both platforms.
- [x] Windows loopback HTTP accepted-socket cap and absolute read/write deadlines matching macOS.
- [x] Apple Silicon / Intel Mac and Windows x64 / ARM64 vendored base gateway dependencies; pinned hashes and Intel native CI gate.
- [x] Shared base MCP input/output schemas, annotations and permission profiles enforced in both native CI and release gates; generated inventory.
- [ ] Complete native Intel and Windows ARM64 acceptance and cross-version physical-device, accessibility and provider matrix; track individual evidence in [platform parity](PLATFORM_PARITY.md).
- [ ] Independent always-on updater daemon (intentionally not installed; current checks run only while the desktop app is open).

## R3 — Direct background computer use (0.6–0.7 series)
- [x] Optional native driver integration, app grants, observations and durable dispatch receipts on both platforms.
- [x] Independent browser and Guest relay; local Guest opt-in, default-off; no automatic foreground or Guest fallback.
- [x] Native visual-only observation, exact geometry checks, window handoff, bounded control actions, driver preflight, post-action observation and synchronized worker cleanup.
- [ ] Expand native application compatibility and measure actual user-desktop non-interference; physical VM, browser and real cloud client acceptance.
- See the separately maintained [CUA guide](CUA.md) for detailed implementation and constraints.

Implementation checks are not blanket certifications. Native hardware,
external providers and OS security boundaries must be verified independently.

### Maintenance checkpoint — 2026-11-28 (post-v0.7.1)

- v0.7.1 protected four-architecture release: [workflow 46115469252](https://github.com/jingzhao-l/ChatToComputerMCP/actions/runs/46115469252).
  Local Mac cutover succeeded and an actual cloud MCP call was verified.
  Historical intermittent disconnect [issue #17](https://github.com/jingzhao-l/ChatToComputerMCP/issues/17) stays open pending longer real transport evidence.
- Next patch: CLI rollback/reinstallation regression; retained newer app must
  pass identity/version/signature/team validation before next-launch selection.
  Broken current pointers must fail closed. Isolated signed fixture and npm
  suite passed locally; no production service change. GUI activation after
  rollback and native Windows/Intel acceptance are distinct follow-up checks.
- Resume from the retained-release-reactivation PR: wait for native Verify,
  then review exact head, merge only on success and use a previously unused
  stable tag with protected OIDC publication. No automatic live takeover.
