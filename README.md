![ChatToComputerMCP](assets/branding/chat-to-computer-mcp/wordmark.svg)

# ChatToComputerMCP

**Bring cloud AI conversations to your local development workspaces.**

ChatToComputerMCP is a local-first, cross-client MCP workspace gateway designed so
multiple ChatGPT, Claude, and other compatible AI conversations can collaborate
with a computer without taking over one another's work. The initial codebase is
derived from [anhnv02/file-mcp](https://github.com/anhnv02/file-mcp),
licensed under **Apache-2.0**. Original licenses and notices remain intact.

> Development preview: current macOS and Windows sources retain FileMCP
> compatibility identifiers, resource names, and connection routes. Changing
> them without a credential migration could detach existing users' Keychain
> or Windows Credential Manager records.

## What works today

- Native macOS (Swift/AppKit) and Windows (C#/.NET) applications with scoped
  filesystem, search and Git tools; shell commands require explicit opt-in.
- Outbound OpenAI Secure MCP Tunnel; Cloudflare alternative on macOS.
- macOS multi-command sessions in disjoint cwd trees, configurable
  FILEMCP_MAX_CONCURRENT_COMMANDS (default 8, clamp 4–16), independent job
  tracking, cancellation, output cursors and conflict rejection. The read-only
  command_capacity_status tool reports occupied and available command slots
  without exposing other clients' command text, directories or session IDs.
- Managed macOS launchd auto-connect and startup after visible window,
  allowing a legitimate Keychain authorization flow.

The cwd guard is cooperative, **not** an OS-level sandbox. Use independent
worktrees and stronger OS isolation for untrusted tasks. Windows currently
has separate command-lifecycle semantics and is not yet feature-equivalent.

## Build and test

macOS: run ./build_macos_app.sh, tests/test_swift_ui.sh,
tests/test_swift_agent_tools.sh, and tests/test_swift_runtime.sh.

Windows: build windows/FileMCP.Windows.sln with .NET 8 and run
tests/test_windows_runtime.ps1.

The macOS app bundle output is dist/ChatToComputerMCP.app. The internal
Keychain bundle identifier and tunnel profile stay backward-compatible
with the original installation pending a safe credential migration. Do
not replace a live production service while AI sessions remain active.

## Documentation

- [Product vision and safety](docs/PROJECT_SCOPE.md)
- [Multi-workspace routing and security](docs/MULTI_WORKSPACE_SECURITY.md)
- [Native GUI capabilities](docs/GUI_CAPABILITIES.md)
- [Engineering roadmap](docs/ROADMAP.md)
- [Credential-preserving migration](docs/RELEASE_MIGRATION.md)
- [Source provenance](docs/UPSTREAM_ATTRIBUTION.md)
- [Original FileMCP README](docs/FILEMCP_UPSTREAM_README.md)
- [Apache-2.0 license](LICENSE) and [security model](SECURITY.md)

ChatToComputerMCP is independent, not an official OpenAI product and not endorsed
by the original FileMCP maintainers. Reusable improvements will be offered
upstream in focused contributions.


## Branding and legacy compatibility

Current app resources and Windows/macOS visible product strings use
ChatToComputerMCP. Legacy FileMCP images and screenshots remain only under
assets/legacy-filemcp for attribution. Older internal native namespaces,
credential target keys, user preference domains, tunnel profile locks, and
OpenAI local-auth header names are deliberately retained as binary/protocol
compatibility layers until a signed migration can preserve existing users'
credentials without an outage.
