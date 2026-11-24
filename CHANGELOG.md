# Changelog

## Unreleased

## [0.5.0] — 2026-11-24

- Make approved workspaces first-class MCP routing targets, with local live
  add/revoke, per-workspace policies and verified OAuth grant binding.
- Add bounded shared FIFO scheduling, local command history, structured activity
  audit, and live cancellation/output for short and long commands.
- Add opt-in independent long-task workers with original-session recovery after
  gateway restart, durable receipt reconciliation and no blind replay.
- Add opt-in restricted Linux container shell for locally approved immutable
  Docker images; missing engines/images fail closed rather than run host shell.
- Add connection identity and owner-scoped task discovery; a shared OpenAI
  tunnel remains a shared principal, not an authenticated conversation.
- Preserve local jobs when a transport exits, and align Windows cloud-poll
  health, wake/recovery, proxy discovery, fragmented log redaction and
  directory-level command collision checks with macOS.
- Reorganize both native desktops into seven sections, with adaptive navigation,
  remembered geometry, diagnostic log controls and idle update activation.
- Add Windows Cloudflare login cancellation, certificate rollback and profile
  mutex ownership fixes. Keep OS-specific implementations native.
- This independent release excludes the separate experimental CUA branch.
  Local Docker and live provider/Windows device acceptance are not implied by
  source compilation; see the platform parity and independent-task docs.

## [0.4.4] — 2026-11-23

- Fix macOS GUI being permanently stuck on **Connecting to OpenAI** even
  while the real tunnel is connected. Official tunnel-client v0.0.12 omits
  cloud polling metrics unless health is run with
  `--require-control-plane-poll`; the GUI watchdog now requests that data.
- Enforce the actual bundled health CLI contract in native regression tests,
  separately validating real connected polling and preserving prior releases.

## [0.4.3] — 2026-11-23

- Fix the normal CLI updater on npm 12, which emits a one-element JSON
  array for the latest dist-tag instead of npm 11's single JSON string.
  Reject malformed or multiple-version registry responses without guessing.
- Run CLI and native macOS GUI npm source updates from separate private,
  manifest-free temporary directories so a development repository cannot
  override the official requested package with an older local CLI.
- Reuse the existing locally available macOS signing certificate when
  upgrading a signed version, while retaining the strict original
  signing-team compatibility gate and Keychain identity protection.
- Validate the macOS wake notification recovery hook with an injected
  native AppKit wake event, including immediate retry when the tunnel is
  already stopped and a recorded follow-up poll result; exercise real vendor TCP
  resets and polling recovery against an isolated loopback control plane.
  Production OpenAI traffic and active user tasks are never fault-injected.

## [0.4.2] — 2026-11-23

- Fix clean Windows npm installation by including `package.json` in the
  temporary native source-build directory before resolving the versioned
  Windows archive name. Native build and runtime tests already succeeded, but
  the isolated npm installer stage lacked this manifest in v0.4.1.
- Preserve v0.4.1 as an immutable historical tag; its npm publication was
  blocked by the Windows clean-install release gate.

## [0.4.1] — 2026-11-23

- Add macOS OpenAI tunnel supervision using the bundled client health endpoint:
  verify recent successful cloud polls, distinguish process startup from actual
  connection, recheck after sleep/wake, and recover sustained idle stalls without
  interrupting active synchronous commands or long-running sessions.
- Add bounded retry and watchdog regressions for stale, missing and fresh polls.
- Added owner-verified `command_receipt_status` on macOS and Windows: a verified OAuth client may read its own salted, durable command state after app restart; other clients and legacy unowned receipts fail closed. A receipt is never claimed as a reattached subprocess.
- Fix OpenAI Secure MCP Tunnel control-plane proxy handling on macOS:
  forward explicit proxy settings, detect manually configured system HTTP/HTTPS
  proxy when no explicit override exists, and bypass the local MCP loopback.
- Retain proxy overrides when the GUI or npm installer regenerates the
  ChatToComputerMCP LaunchAgent, without copying unrelated secrets.
- Prevent the npm installer test in a temporary HOME from stopping the user's
  real macOS LaunchAgent; add native proxy, login-agent and migration regressions.
- Pass OpenAI tunnel proxy variables through the Windows native runtime.
- Derive Windows release archive names from the package version rather than
  leaving CI and native smoke tests pinned to a prior release.
- Fix macOS source-built app bundle sealing: sign the completed bundle
  (ad-hoc by default, explicit local identity optionally) and validate
  `codesign --verify --deep --strict` in source build and CI; see #15.

## [0.4.0] — 2026-11-23

- Raised the minimum supported Node.js version to 24. Users on Node.js 20 must upgrade before using the new CLI. This is a compatibility change relative to v0.3.0.
- Production acceptance: latest main CI passes macOS, Windows native build/clean npm installation, and security scans.
- Prevent duplicate full CI runs for pull-request branch pushes; keep `main` push, pull-request and manual verification gates.
- Made Windows source-installer archive SHA-256 verification independent of the `Get-FileHash` PowerShell command, which failed in isolated Windows PowerShell 5.1 despite successful native compilation.
- Added a native Windows x64 clean-user npm tarball installation gate (offline source installation, CLI version, real local .NET app build, installed app readback, and legacy profile isolation) to CI and release validation.
- Updated GitHub Actions `checkout`, `setup-node`, `setup-go`, `setup-dotnet`, and `upload-artifact` to their verified Node 24 runtime major versions; existing build/test jobs are unchanged.
- Standardized the supported Node.js CLI runtime to 24+, pinned the repository toolchain, and ensured release tag validation runs after Node 24 setup. The CI guard now checks project engines and setup order in addition to GitHub Actions runtimes.
- Corrected the Windows concurrent-command MCP description and added a native eight-slot capacity/collision/cancellation regression.
- Reconciled the Cloudflare Windows parity audit with the implementation already integrated into main; live-provider and ARM64 validation remain outstanding.

## [0.3.0] — 2026-11-23

- Added native, opt-in update management on macOS and Windows:
  check and install a newer official npm release from the GUI without restarting
  active MCP sessions; automatic checks/installs while the app is running are
  off by default and configurable.
- Added CLI update check/install/status/enable/disable; atomic active-release
  pointer for Mac, retained versioned native builds, and an installer success
  postcondition. Existing FileMCP is never changed by updates.
- Regression tests cover SemVer validation, cross-platform updater policy,
  corrupt/symlinked configuration, launchagent safety, and native update GUI.
- The v0.2.0 tag did not publish to npm due to an OS-specific Linux npm
  test. v0.2.1 remained an internal release candidate; these features
  are included in the next stable 0.3.0 release.

The v0.2.0 Git tag was retained for audit but its npm publish workflow
stopped at the Linux package-test gate **before publication**. Version 0.2.1
supersedes that unpublished release candidate without rewriting tag history.
The cross-platform npm LaunchAgent test now verifies unsupported Linux
behavior instead of applying macOS-only assertions.

- Added safe no-shell file operations on macOS and Windows: file_info,
  create_directory, copy_file and move_file. File copies/moves refuse overwrite
  and symlink sources, validate workspace boundaries and have regression tests.
- Expanded Mac and Windows Tasks GUIs with bounded stdout/stderr, exit codes,
  output copy and folder reveal. Mac preserves session selection on refresh.
- Mac Cloudflare OAuth now offers explicit workspace scope approval (default
  workspace only by default), token-verified client ownership of command
  sessions, scoped tool isolation and GUI client revocation.
- Secure MCP Tunnel continues to use shared local authorization and does NOT
  cryptographically identify an individual ChatGPT conversation.
- macOS crash-safe command receipt journal (from 0.1.0 development main)
  persists idempotency tombstones and warns about interrupted work; it is not
  cross-restart process reattachment.
- Aligned macOS and Windows app/server/package version identifiers with 0.2.1,
  and expanded native regression tests and security/GUI documentation.


## [0.1.0] — 2026-11-22

Initial public npm source-installer release for ChatToComputerMCP:

- Cross-client local workspace gateway, approved multi-root routing and native GUI.
- Shared 4–16 command capacity and targeted cancellation.
- Source-only npm package that compiles the native desktop app locally.
- Optional macOS LaunchAgent with login startup, crash recovery and opt-in auto-connect.
- macOS native tests and clean-HOME package installation verified; Windows
  cross-target compilation verified but real-device end-to-end testing pending.
- Independent derivative of anhnv02/file-mcp, retaining Apache-2.0 attribution.


All notable changes will be documented in this file from the first public release onward.

## Historical development notes (integrated before v0.3.0)

- Added a compact macOS workspace overview, top navigation and a persistent light/dark appearance toggle.
- Improved connection-action contrast, workspace alignment and layout regression coverage across tabs, providers and runtime states.
- Updated setup documentation and added native screenshots for all macOS screens in light and dark appearance.

- Simplified Connection with an active-session summary, full-width MCP endpoint, grouped copy actions and collapsible Cloudflare management.

- Redesigned the OAuth login page with responsive light/dark styling, accessible code entry and explicit access details; styles use a per-response CSP nonce.

### Added

- Optional Cloudflare Tunnel provider (macOS): use a dashboard token or locally authenticated named tunnel with a public hostname. OAuth is the default; the legacy secret-URL mode is opt-in.
- Open-source project license and contribution/security documentation.
- GitHub Actions macOS and Windows CI verification.
- Reproducible source-archive packaging from tracked Git content only.
- Explicit provenance and checksum documentation for the vendored OpenAI `tunnel-client` binary.
- New FileMCP macOS app icon optimized for the Dock.
- Native Windows application with WPF UI, system-tray lifecycle, Windows Credential Manager storage, and x64/ARM64 release packaging.
- Windows parity integration coverage for filesystem containment, NTFS junction/reparse points, Git safe mode, Job Object process cleanup, MCP legacy/modern protocols, and tunnel runtime lifecycle.
- Cross-platform `save_conversation_to_codex` support for importing supplied conversations into Codex local history on macOS and Windows, with post-write app-server verification.
- Cross-platform `batch_read` support for combining up to 16 read-only file, search, and Git inspection operations into one MCP round trip.
- Cross-platform ranked `search_code` with multi-query single-pass scanning, transparent relevance signals, and coverage metadata.
- Cross-platform ripgrep-backed `grep` and `glob` tools modeled on coding-agent search: regex/literal patterns, glob and file-type filters, content/count/file output modes, context lines, newest-first file ordering, and `head_limit`/`offset` pagination.
- Bundled official ripgrep 15.2.0 binaries (macOS arm64, Windows x64/ARM64) with documented provenance, checksums, and license files.
- Per-tool-call timing and response-size log lines (tool name only; no arguments or content).
- Cross-platform `repo_overview` for factual repository structure, manifest, extension-count, exclusion, and scan-coverage context.
- Cross-platform `workspace_context` for scoped coding context, including Git status, applicable `AGENTS.md`, bounded manifest contents, and top-level workspace entries.
- Cross-platform conflict-checked `edit_file` and batched `apply_patch` tools with dry-run previews, SHA-256 guards, ordered multi-hunk edits, pre-write validation, and best-effort rollback of partial multi-file writes.
- Cross-platform session command tools (`start_command`, `read_command_output`, and `cancel_command`) with bounded cursor-based streaming, idempotent request IDs, timeout/cancellation states, retained output, and descendant-process cleanup.

### Changed

- Redesigned the FileMCP identity with a flat folded-document symbol with a custom folded M monogram, crimson (#DB0F30) palette, layered gradient artwork and light/dark/monochrome vector logos, transparent PNGs and matching macOS/Windows application icons. Added a reproducible brand-kit exporter.
- Cloudflare on macOS now defaults to OAuth with PKCE, a private app login code, hashed Keychain session storage, rotating refresh tokens and revocation on replay or permission/workspace changes. Secret-URL authentication requires explicit compatibility opt-in.
- macOS HTTP transport now bounds active connections, request-read time and response-write time, and cancels sockets on shutdown.
- Added separate, default-off macOS permissions for Git push and writing to Codex history, plus full-history/source secret scanning in CI.
- Improved the macOS Connection, Workspace and Activity tabs with a persistent connection action, workspace/access summary, cancellable startup, inline connection errors, log-copy feedback and an empty activity state. Configuration is locked while sharing; Quit remains in the standard app menu.
- Cloudflare startup now waits for edge registration, recognizes registration messages split across reads, checks local tunnel credentials, and uses the same origin certificate for management and connection. Tunnel log redaction also handles secrets split across stdout/stderr reads.
- Replaced the hand-written recursive scanners behind `search_code` and `repo_overview` with ripgrep, and replaced `search_content`/`search_filenames` with `grep`/`glob`. Searches now respect `.gitignore`/`.ignore` by default (opt out with `include_ignored`), always reject explicit `.git` targets, and avoid prematurely exhausting recursive-scan byte budgets on binary or build-output trees while reporting bounded/truncated coverage explicitly.

- Updated the bundled OpenAI `tunnel-client` to v0.0.12 with upstream NOTICE and third-party license evidence, including official Windows AMD64/ARM64 binaries.
- Rebranded the macOS app and MCP server identity from Local Files MCP to FileMCP.
- Updated the app bundle/executable names, default profile, default shared folder, documentation, and release artifact naming for FileMCP.
- Removed the persistent menu-bar status item; FileMCP now relies on the Dock and the standard macOS application menu.
- Made advanced settings resize the window between compact and expanded states.
- Polished the native macOS UI with a wider layout, clearer connection-state actions, English labels/errors/runtime messages, collapsible advanced settings, and standard App/Edit/Window menus.
- Preserved the existing bundle identifier and Keychain namespace so local settings and saved runtime credentials continue to migrate safely across the rebrand.
- Hardened local HTTP parsing with required local authentication, strict `Host`/`Origin` and header syntax checks, bounded framing, and rejection of trailing body bytes.
- Added a fresh per-runtime 256-bit token between `tunnel-client` and the loopback MCP server; the token is referenced through an environment indirection, not persisted in generated tunnel profiles, and redacted from logs/errors.
- Restricted the `tunnel-client` health/admin listener to loopback, isolated FileMCP tunnel profiles under Application Support, and allowlisted the child tunnel environment to prevent ambient config overrides.
- Hardened Git safe mode across Git/config/common/object/alternate metadata, repository config includes, embedded repositories, external init templates, HTTP credential/TLS file settings, credential helpers, SSH config execution paths, and MCP-internal TOCTOU races.
- Hardened process execution by validating launch inputs before spawn and cleaning descendant process trees on parent exit, timeout, stop, and shutdown (POSIX process groups on macOS; Job Objects on Windows).
- HTTP malformed-request fuzz iterations can be reduced through `MCP_HTTP_FUZZ_ITERATIONS` for targeted development/CI runs while preserving the full default count.
- Scoped recursive filename search can reduce unnecessary local traversal when callers already know the relevant subdirectory, without changing default broad-search coverage.
- Content search now reports explicit truncation reasons so callers can distinguish result, preview, visited-entry, and byte-budget limits.
