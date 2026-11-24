# Verified OAuth client access (macOS and Windows, Cloudflare OAuth)

This document describes which remote clients have independently verified
identities and which connections are intentionally shared.

## Verified client authorization

Cloudflare OAuth clients authenticate with **server-issued bearer tokens**
bound to a locally approved OAuth client ID. A client-supplied display name is
shown for human recognition only; it is **not** the security identity.

The local authorization page defaults to **Default workspace only**. A user
may deliberately select **one additional approved workspace** or **all approved
workspaces**. The default workspace remains available for every approved
client, and remote tool calls cannot grant themselves additional roots.

The MCP server enforces the following for **verified OAuth requests**:

- Each client can list only the scopes it was approved to use.
- A `workspace_call` into an unapproved additional root is denied.
- Long-running command session IDs belong to the OAuth client that started
  them; other OAuth clients cannot read or cancel that session.
- A second client cannot obtain the first client's session through a matching
  `request_id`.
- Expired, invalid and revoked OAuth tokens are rejected.
- Approved client workspace grants persist with the hashed OAuth token store.
  Old stores without per-client grants migrate to default-only access.
- macOS Connection includes **Authorized OAuth clients**: inspect self-reported
  names, current scopes and revoke access/refresh tokens and stop that client's active commands.

## Critical exception: OpenAI Secure MCP Tunnel

The current private OpenAI Secure MCP Tunnel uses a shared, local authentication
header. It does not supply a cryptographically verified **individual ChatGPT
conversation identity** to this local server. Different chats using one tunnel
may therefore share the same approved workspace and command session controls.

Do not use self-reported MCP clientInfo, tool arguments, headers with arbitrary
names or request IDs as authentication. Real per-conversation isolation in this
mode requires a verified transport claim or a separately approved OAuth flow.

[OpenAI Secure MCP Tunnel documentation](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)

## Other limits

- Both native GUIs manage Cloudflare OAuth clients. The OpenAI shared tunnel
  remains a single connection principal, not an authenticated chat identity.
- The default host shell runs with the current user's privileges. Optional
  isolated Linux execution uses a separately approved Docker image with
  restricted mounts and no network; the cwd overlap guard is not a sandbox.
- connection_identity reports the verified principal and grant scope;
  list_my_commands lists sessions of that principal only.
- Independent worker mode can recover a live worker across gateway restarts,
  subject to the original owner, durable receipt and current workspace grant.
  An OS reboot or worker loss is not live recovery.

## Testing

The Mac suite checks OAuth registration, PKCE, token issuance and persistence,
default-only backward compatibility, scope forgery rejection, independent
workspace routing, cross-client output/cancel denial, request-ID collision
refusal, and revocation. A deployment still needs real authenticated client
end-to-end verification before claiming production isolation.
