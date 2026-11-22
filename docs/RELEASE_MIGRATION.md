# Compatibility-safe release plan

Current production service on the user's Mac is a separate existing FileMCP
instance managed by local.filemcp launchd. It must not be moved or mutated
as part of source repository restructuring.

This initial ChatToComputer rebrand changes only visible product identifiers.
The inherited macOS Bundle Identifier com.localfilesmcp.app, binary name,
UserDefaults, Keychain service, Secure MCP Tunnel, tunnel profile and proxy
paths remain until a tested migration exists.

To migrate future releases:
1. Preserve old signed bundle and original credentials, verify source hashes.
2. Stage a new signed bundle separately; show UI before Keychain access.
3. Require authorized Keychain prompts; never export tokens or modify ACLs.
4. Check live command sessions before replacing the production symlink.
5. Use independent one-shot launchd helper with atomic update and rollback.
6. Verify healthz/readyz HTTP200, control-plane polling, client/cwd concurrency,
   and sibling cancellation isolation before claiming production completion.

No new ChatToComputer LaunchAgent is installed by this source import.
