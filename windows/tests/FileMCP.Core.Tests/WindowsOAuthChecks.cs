using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private sealed record OAuthFixture(WindowsOAuthServer Server, string Issuer,
        string File, string Pairing, string[] Scopes);

    private static async Task TestWindowsOAuthAsync(string root)
    {
        var folder = Path.Combine(root, "oauth");
        Directory.CreateDirectory(folder);
        const string issuer = "https://example.test";
        var pairing = new string('q', 64);
        var file = Path.Combine(folder, "oauth-state.json");
        var scopes = new[] { "default", "second" };
        var defaultRoot = Path.Combine(folder, "default");
        var secondRoot = Path.Combine(folder, "second");
        Directory.CreateDirectory(defaultRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(defaultRoot, "default.txt"), "primary-content");
        File.WriteAllText(Path.Combine(secondRoot, "second.txt"), "secondary-content");
        var approved = new[] { new ApprovedWorkspace("second", "Secondary", secondRoot) };
        var server = new WindowsOAuthServer(issuer, pairing,
            "policy-A|files", "File and command access", approved, file);
        static Dictionary<string,string> Form(params (string Name,string Value)[] fields) =>
            fields.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        static string Encode(IReadOnlyDictionary<string,string> values) =>
            string.Join("&",values.Select(x => Uri.EscapeDataString(x.Key) +
                "=" + Uri.EscapeDataString(x.Value)));
        static JsonElement Json(WindowsOAuthServer.Response response) =>
            JsonDocument.Parse(response.Body).RootElement.Clone();
        static WindowsOAuthServer.Response Post(WindowsOAuthServer api,string route,
            IReadOnlyDictionary<string,string> values) =>
            api.Handle("POST",route,"","application/x-www-form-urlencoded",
                Encoding.UTF8.GetBytes(Encode(values)))!;
        string Register(string label)
        {
            var json = JsonSerializer.Serialize(new {
                client_name = label,
                redirect_uris = new[] { "https://chatgpt.com/connector_platform_oauth_redirect" },
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" }
            });
            var result=server.Handle("POST","/oauth/register","",
                "application/json",Encoding.UTF8.GetBytes(json))!;
            Assert(result.Status == 201, "OAuth dynamic client registration");
            return Json(result).GetProperty("client_id").GetString()!;
        }
        var clientA=Register("client Alice");
        var clientB=Register("client Bob");
        var verifier = new string('v', 64);
        var challenge = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
            .TrimEnd('=').Replace('+','-').Replace('/','_');
        var redirect = "https://chatgpt.com/connector_platform_oauth_redirect";

        string Authorize(string client,string workspaceScope)
        {
            var query=Encode(Form(("response_type","code"),
                ("client_id",client),("redirect_uri",redirect),
                ("resource",issuer+"/mcp"),("scope","mcp"),
                ("code_challenge_method","S256"),("code_challenge",challenge),
                ("state","session-state")));
            var page=server.Handle("GET","/oauth/authorize",query,null,[])!;
            Assert(page.Status==200 && page.ContentType.StartsWith("text/html"),
                "OAuth pairing page uses HTML");
            var html=Encoding.UTF8.GetString(page.Body);
            Assert(html.Contains("scope:default") && html.Contains("scope:second") &&
                html.Contains("Default workspace only"),"User must choose scope locally");
            var requestID=System.Text.RegularExpressions.Regex.Match(html,
                "name='request_id' value='([^']+)'").Groups[1].Value;
            Assert(requestID.Length==64,"Opaque pending authorization request");
            var approvedResponse=Post(server,"/oauth/authorize",Form(
                ("request_id",requestID),("pairing_code",pairing),
                ("workspace_scope",workspaceScope)));
            Assert(approvedResponse.Status==302,
                "Correct local pairing code allows selected workspace scope");
            var destination=approvedResponse.Headers!["Location"];
            var code=new Uri(destination).Query.TrimStart('?').Split('&')
                .Select(field=>field.Split('=',2))
                .Where(parts=>parts[0]=="code")
                .Select(parts=>Uri.UnescapeDataString(parts[1])).Single();
            Assert(code.Length==64,"Authorization code is opaque");
            return code;
        }
        string Token(string client,string code)
        {
            var response=Post(server,"/oauth/token",Form(
                ("grant_type","authorization_code"),("client_id",client),
                ("code",code),("redirect_uri",redirect),
                ("code_verifier",verifier),("resource",issuer+"/mcp")));
            Assert(response.Status==200,"PKCE code exchanged for token");
            return Json(response).GetProperty("access_token").GetString()!;
        }
        var tokenA=Token(clientA,Authorize(clientA,"scope:default"));
        var tokenB=Token(clientB,Authorize(clientB,"scope:second"));
        var identityA=server.VerifyBearer("Bearer "+tokenA)!;
        var identityB=server.VerifyBearer("Bearer "+tokenB)!;
        Assert(identityA.Id==clientA && identityA.Workspaces.SetEquals(["default"]),
            "Client A permitted default only");
        Assert(identityB.Id==clientB && identityB.Workspaces.SetEquals(["default","second"]),
            "Client B granted second workspace");
        Assert(server.VerifyBearer("Bearer invented-token") is null &&
            server.VerifyBearer("Basic "+tokenA) is null,
            "Untrusted tokens do not grant workspace access");
        Assert(server.ApprovedClients().Count==2, "Approved clients visible in GUI");
        await CheckOAuthHttpAsync(defaultRoot,approved,server,tokenA,tokenB);
        // A malicious scope is not an implicit approval.
        var invalidScope=server.Handle("POST","/oauth/register","",
            "application/json",Encoding.UTF8.GetBytes(
                "{\"redirect_uris\":[\"http://evil.example/steal\"]}"))!;
        Assert(invalidScope.Status==400, "Non-loopback insecure redirects rejected");
        var secondProcess=new WindowsOAuthServer(issuer,pairing,
            "policy-A|files","File and command access",approved,file);
        Assert(secondProcess.VerifyBearer("Bearer "+tokenB)?.Workspaces.SetEquals(
            ["default","second"]) == true,
            "Verified OAuth client permissions and token hashes survive restart");
        secondProcess.RevokeClient(clientB);
        Assert(secondProcess.VerifyBearer("Bearer "+tokenB) is null &&
            secondProcess.VerifyBearer("Bearer "+tokenA) is not null,
            "Selective revocation preserves sibling client");
        var changedPolicy=new WindowsOAuthServer(issuer,pairing,
            "policy-B|permission-changed","File access",approved,file);
        Assert(changedPolicy.VerifyBearer("Bearer "+tokenA) is null,
            "Permission-policy change invalidates old token families");

        File.WriteAllText(file,"{corrupt");
        try
        {
            _ = new WindowsOAuthServer(issuer,pairing,"policy-B|permission-changed",
                "File access",approved,file);
            throw new Exception("Invalid OAuth file accepted");
        }
        catch(FileMcpException ex)
        {
            Assert(ex.Message.Contains("Invalid OAuth state"),
                "Corrupt OAuth store fails closed rather than resetting");
        }
        await Task.CompletedTask;
        Console.WriteLine("windows-oauth-dcr-pkce-client-scopes-revocation-persistence: ok");
    }
}

internal static class VerifiedOAuthSetExtensions
{
    public static bool SetEquals(this IReadOnlySet<string> value, IEnumerable<string> expected)
        => new HashSet<string>(value,StringComparer.Ordinal).SetEquals(expected);
}
