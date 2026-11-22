import Foundation
import CryptoKit

func runOAuthPersistenceChecks() throws {
    let issuer = "https://mcp.example.com"
    let pairing = String(repeating: "p", count: 64)
    var durable: Data?
    let approved = [WorkspaceDefinition(id: "second", name: "Second repository", path: "/tmp/user-approved-second")]
    let oauth = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing,
        policy: "workspace-A|files", permissionDescription: "File access",
        approvedWorkspaces: approved, persist: { durable = $0 })
    func response(_ server: LocalMCPOAuthServer, _ method: String, _ path: String, _ values: [String: String] = [:], json: Bool = false) -> LocalOAuthResponse {
        var components = URLComponents()
        components.queryItems = values.map { URLQueryItem(name: $0.key, value: $0.value) }
        let encoded = components.percentEncodedQuery ?? ""
        let body = json ? try! JSONSerialization.data(withJSONObject: ["redirect_uris": ["https://chatgpt.com/connector_platform_oauth_redirect"]]) : Data(encoded.utf8)
        return server.handle(method: method, path: path, query: encoded, contentType: json ? "application/json" : "application/x-www-form-urlencoded", body: body)!
    }
    func object(_ reply: LocalOAuthResponse) -> [String: Any] { try! JSONSerialization.jsonObject(with: reply.body) as! [String: Any] }
    let client = object(response(oauth, "POST", "/oauth/register", json: true))["client_id"] as! String
    let verifier = String(repeating: "v", count: 64)
    let challenge = Data(SHA256.hash(data: Data(verifier.utf8))).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    let page = response(oauth, "GET", "/oauth/authorize", ["response_type": "code", "client_id": client, "redirect_uri": "https://chatgpt.com/connector_platform_oauth_redirect", "resource": issuer + "/mcp", "scope": "mcp", "code_challenge_method": "S256", "code_challenge": challenge])
    let html = String(decoding: page.body, as: UTF8.self)
    let expression = try NSRegularExpression(pattern: "name=\"request_id\" value=\"([^\"]+)\"")
    let match = expression.firstMatch(in: html, range: NSRange(html.startIndex..., in: html))!
    let nonce = String(html[Range(match.range(at: 1), in: html)!])
    let rejected = response(oauth, "POST", "/oauth/authorize", ["request_id": nonce, "pairing_code": "incorrect"])
    precondition(rejected.status == 403 && rejected.contentType.hasPrefix("text/html"))
    let rejectedHTML = String(decoding: rejected.body, as: UTF8.self)
    precondition(rejectedHTML.contains("did not match") && rejectedHTML.contains(nonce) && !rejectedHTML.contains(pairing))
    let authorized = response(oauth, "POST", "/oauth/authorize", ["request_id": nonce, "pairing_code": " \n" + pairing + "\n "])

    precondition(authorized.status == 302)
    let replayedForm = response(oauth, "POST", "/oauth/authorize", ["request_id": nonce, "pairing_code": pairing])
    precondition(replayedForm.status == 403 && replayedForm.contentType.hasPrefix("text/html"))
    let replayedHTML = String(decoding: replayedForm.body, as: UTF8.self)
    precondition(replayedHTML.contains("start the connection again") && !replayedHTML.contains("<form"))

    let code = URLComponents(string: authorized.headers["Location"]!)!.queryItems!.first { $0.name == "code" }!.value!
    let exchanged = response(oauth, "POST", "/oauth/token", ["grant_type": "authorization_code", "client_id": client, "redirect_uri": "https://chatgpt.com/connector_platform_oauth_redirect", "code": code, "code_verifier": verifier, "resource": issuer + "/mcp"])
    precondition(exchanged.status == 200)
    let tokens = object(exchanged)
    let access = tokens["access_token"] as! String
    let refresh = tokens["refresh_token"] as! String
    precondition(oauth.accepts("Bearer " + access))
    let firstIdentity = oauth.verifiedClient(forAuthorization: "Bearer " + access)!
    precondition(firstIdentity.id == client &&
                 firstIdentity.approvedWorkspaceIDs == ["default"])
    precondition(oauth.verifiedClient(forAuthorization: "Bearer forged-credential") == nil)

    // A second, genuinely approved OAuth client can be granted ONE extra
    // workspace by the local pairing page. Its self-reported client name is
    // never considered an authorization claim.
    let otherClient = object(response(oauth, "POST", "/oauth/register", json: true))["client_id"] as! String
    let secondPage = response(oauth, "GET", "/oauth/authorize", [
        "response_type": "code", "client_id": otherClient,
        "redirect_uri": "https://chatgpt.com/connector_platform_oauth_redirect",
        "resource": issuer + "/mcp", "scope": "mcp",
        "code_challenge_method": "S256", "code_challenge": challenge
    ])
    let secondHTML = String(decoding: secondPage.body, as: UTF8.self)
    precondition(secondHTML.contains("scope:second") &&
                 secondHTML.contains("Default workspace only"))
    let otherMatch = expression.firstMatch(in: secondHTML,
        range: NSRange(secondHTML.startIndex..., in: secondHTML))!
    let secondRequestID = String(secondHTML[Range(otherMatch.range(at: 1), in: secondHTML)!])
    let scopeForgery = response(oauth, "POST", "/oauth/authorize", [
        "request_id": secondRequestID, "pairing_code": pairing,
        "workspace_scope": "scope:unapproved"
    ])
    precondition(scopeForgery.status == 403)
    let secondApproval = response(oauth, "POST", "/oauth/authorize", [
        "request_id": secondRequestID, "pairing_code": pairing,
        "workspace_scope": "scope:second"
    ])
    precondition(secondApproval.status == 302)
    let codeB = URLComponents(string: secondApproval.headers["Location"]!)!.queryItems!
        .first { $0.name == "code" }!.value!
    let exchangedB = response(oauth, "POST", "/oauth/token", [
        "grant_type": "authorization_code", "client_id": otherClient,
        "redirect_uri": "https://chatgpt.com/connector_platform_oauth_redirect",
        "code": codeB, "code_verifier": verifier, "resource": issuer + "/mcp"
    ])
    precondition(exchangedB.status == 200)
    let accessB = object(exchangedB)["access_token"] as! String
    let secondIdentity = oauth.verifiedClient(forAuthorization: "Bearer " + accessB)!
    precondition(secondIdentity.id == otherClient &&
                 secondIdentity.approvedWorkspaceIDs == ["default", "second"])
    precondition(!firstIdentity.approvedWorkspaceIDs.contains("second"))
    let saved = durable!
    let savedText = String(decoding: saved, as: UTF8.self)
    precondition(!savedText.contains(access) && !savedText.contains(refresh) && !savedText.contains(pairing))
    let restored = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing,
        policy: "workspace-A|files", permissionDescription: "File access",
        approvedWorkspaces: approved, persistedState: saved)
    precondition(restored.accepts("Bearer " + access), "a process restart should preserve an approved session")
    precondition(restored.verifiedClient(forAuthorization: "Bearer " + accessB)?
        .approvedWorkspaceIDs == ["default", "second"],
        "verified per-client workspace scopes survive restart")
    precondition(restored.desktopApprovedClients().count == 2)
    try restored.revokeDesktopClient(otherClient)
    precondition(restored.verifiedClient(forAuthorization: "Bearer " + accessB) == nil,
                 "locally revoked client token cannot authorize future requests")
    precondition(restored.verifiedClient(forAuthorization: "Bearer " + access) != nil,
                 "revoking client B must preserve client A")
    precondition(restored.desktopApprovedClients().count == 1)
    do {
        try restored.revokeDesktopClient(otherClient)
        preconditionFailure("Repeated revocation incorrectly succeeded")
    } catch { }

    // Pre-upgrade stores lack the newly added workspace choice property.
    // Such clients default to the least-privileged default workspace only.
    var oldFormat = try JSONSerialization.jsonObject(with: saved) as! [String: Any]
    var oldClients = oldFormat["clients"] as! [String: [String: Any]]
    for id in oldClients.keys { oldClients[id]?.removeValue(forKey: "allowedWorkspaceIDs") }
    oldFormat["clients"] = oldClients
    let legacy = try LocalMCPOAuthServer(
        issuer: issuer, pairingSecret: pairing, policy: "workspace-A|files",
        permissionDescription: "File access", approvedWorkspaces: approved,
        persistedState: JSONSerialization.data(withJSONObject: oldFormat))
    precondition(legacy.verifiedClient(forAuthorization: "Bearer " + accessB)?
        .approvedWorkspaceIDs == ["default"], "old store must not accidentally grant extra workspaces")
    let changedPermissions = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing, policy: "workspace-A|files+shell", permissionDescription: "Shell access", persistedState: saved)
    precondition(!changedPermissions.accepts("Bearer " + access), "new permissions require fresh consent")
    let changedWorkspace = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing, policy: "workspace-B|files", permissionDescription: "File access", persistedState: saved)
    precondition(!changedWorkspace.accepts("Bearer " + access), "a new workspace requires fresh consent")
    let changedPairing = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: String(repeating: "q", count: 64), policy: "workspace-A|files", permissionDescription: "File access", persistedState: saved)
    precondition(!changedPairing.accepts("Bearer " + access), "resetting the login code revokes earlier sessions")
    let relink = response(changedPairing, "GET", "/oauth/authorize", ["response_type": "code", "client_id": client, "redirect_uri": "https://chatgpt.com/connector_platform_oauth_redirect", "resource": issuer + "/mcp", "scope": "mcp", "code_challenge_method": "S256", "code_challenge": challenge])
    precondition(relink.status == 200, "resetting credentials should preserve client registration for re-linking")
    let unwritable = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing, policy: "workspace-A|files", permissionDescription: "File access", persistedState: saved, persist: { _ in throw NSError(domain: "OAuth fixture", code: 1) })
    let failedSave = response(unwritable, "POST", "/oauth/token", ["grant_type": "refresh_token", "client_id": client, "refresh_token": refresh, "resource": issuer + "/mcp"])
    precondition(failedSave.status == 500 && object(failedSave)["access_token"] == nil, "failed session persistence must not release new tokens")
    precondition(unwritable.accepts("Bearer " + access), "failed token rotation must roll back state")
    do {
        _ = try LocalMCPOAuthServer(issuer: "https://other.example", pairingSecret: pairing, policy: "workspace-A|files", permissionDescription: "File access", persistedState: saved)
        preconditionFailure("a session store must be bound to its issuer")
    } catch { }
    var expired = try JSONSerialization.jsonObject(with: saved) as! [String: Any]
    var grants = expired["access"] as! [String: [String: Any]]
    for key in grants.keys { grants[key]!["expires"] = Date().addingTimeInterval(-60).timeIntervalSinceReferenceDate }
    expired["access"] = grants
    let expiredServer = try LocalMCPOAuthServer(issuer: issuer, pairingSecret: pairing, policy: "workspace-A|files", permissionDescription: "File access", persistedState: JSONSerialization.data(withJSONObject: expired))
    precondition(!expiredServer.accepts("Bearer " + access), "expired access tokens must be rejected")
    let duplicate = oauth.handle(method: "POST", path: "/oauth/token", query: "", contentType: "application/x-www-form-urlencoded", body: Data("client_id=a&client_id=b".utf8))!
    precondition(duplicate.status == 400)
    for invalid in ["client_id=a&client%5Fid=b", "client_id=%ZZ"] {
        let reply = oauth.handle(method: "POST", path: "/oauth/token", query: "", contentType: "application/x-www-form-urlencoded", body: Data(invalid.utf8))!
        precondition(reply.status == 400)
    }
    print("oauth-persistence-hashed-storage-expiry-and-policy-reconsent: ok")
}
