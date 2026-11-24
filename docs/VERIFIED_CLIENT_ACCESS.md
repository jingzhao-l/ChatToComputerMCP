# Verified OAuth client access and conversation identity boundaries

This document describes which remote clients have independently verified
identities and which connections are intentionally shared.

## What verified OAuth actually provides

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
- macOS and Windows Connection include **Authorized OAuth clients**: inspect self-reported
  names, current scopes and revoke access/refresh tokens and stop that client's active commands.

## Critical exception: OpenAI Secure MCP Tunnel

The current private OpenAI Secure MCP Tunnel uses a shared, local authentication
header. It does not supply a cryptographically verified **individual ChatGPT
conversation identity** to this local server. Different chats using one tunnel
may therefore share the same approved workspace and command session controls.

Do not use self-reported MCP clientInfo, tool arguments, headers with arbitrary
names or request IDs as authentication. A separately approved OAuth flow proves a distinct OAuth client, not necessarily a distinct conversation. Secure MCP Tunnel can forward Authorization headers, but the present shared configuration does not bind every request to a particular ChatGPT conversation. A platform-verified identity claim or a trusted client that binds unique authorization to each conversation is required.

## Design for true conversation isolation

Distinguish four principals: tunnel connection, verified OAuth client, verified cloud conversation, and execution lease. Never promote a weaker principal into a stronger one. A server-generated opaque lease is a capability for a resource, not evidence of a conversation's origin. MCP transport session identifiers likewise do not prove cloud conversation identity.

A platform-supported claim must bind the authenticated account/tenant, conversation identifier, audience, expiry and request context, with verified issuer/signature and replay protection. A cooperating client can instead obtain separate, short-lived authorization for each conversation and attach it outside model-controlled tool arguments. The server must validate this binding on each operation, including reads, cancellation, receipts and revocation, and reject missing/invalid claims without falling back to shared identity. Until a trusted Chat client supplies such evidence, do not advertise verified conversation isolation. Independent connector registrations alone do not prove one registration per chat.

CUA offers a local fail-closed option, `chat-to-computer-mcp cua require-verified enable`, requiring independently verified OAuth clients; it does not invent a cloud conversation identifier. See [CUA](CUA.md). The project also needs an OS security boundary (e.g. distinct restricted OS accounts or dedicated guests) if it must isolate an untrusted shell from another client's files, credentials or desktop. App-level authorization and path checks are not that boundary.

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

The Mac and Windows suites check OAuth registration, PKCE, token issuance and persistence,
default-only backward compatibility, scope forgery rejection, independent
workspace routing, cross-client output/cancel denial, request-ID collision
refusal, and revocation. Both advertise the OAuth MCP security scheme on verified OAuth transports. A deployment still needs real authenticated client
end-to-end verification before claiming production isolation.

### Dedicated execution domains

For clients without a trustworthy per-conversation identity assertion, a
dedicated connector and credential can be assigned to a separate execution
domain. The new CUA relay-only listener enforces a single Guest profile and
rejects all host file/shell/Git/workspace and native desktop routes. See
[dedicated relay execution domains](CUA.md#dedicated-relay-execution-domains-no-host-tool-fallback).
A separate credential verifies access to that domain, never that a particular
cloud conversation originated the call. Use distinct trusted VMs or restricted
OS accounts for a strong resource boundary; do not grant a restricted caller
another route to the general-purpose host gateway.
