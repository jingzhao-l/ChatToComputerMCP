using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task CheckOAuthHttpAsync(
        string workspace, ApprovedWorkspace[] scopes,
        WindowsOAuthServer oauth, string first, string second)
    {
        var port = FreePort();
        await using var http = new LocalMcpServer((ushort)port, workspace,
            "Test", "test@example.invalid", true, new string('a', 64), _ => {},
            scopes, maxConcurrentCommands: 4, oauth: oauth, publicHost: "example.test");
        await http.StartAsync();

        var discovery = await SendHttpAsync(port, "GET",
            "/.well-known/oauth-authorization-server",
            new Dictionary<string, string>(), "", host: "example.test");
        Assert(discovery.StartsWith("HTTP/1.1 200",StringComparison.Ordinal),
            "Public authorization server metadata is reachable");
        var denied = await SendHttpAsync(port, "POST", "/mcp",
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            "{}",host:"example.test");
        Assert(denied.StartsWith("HTTP/1.1 401",StringComparison.Ordinal),
            "Cloudflare OAuth mode refuses unauthenticated tool access");

        async Task<JsonObject> Call(string access,string tool,JsonObject? args=null)
        {
            var body = new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 51, ["method"] = "tools/call",
                ["params"] = new JsonObject {
                    ["name"] = tool, ["arguments"] = args ?? new JsonObject()
                }
            };
            var headers = new Dictionary<string,string> {
                ["Content-Type"] = "application/json",
                ["Authorization"] = "Bearer " + access
            };
            var result = await SendHttpAsync(port,"POST","/mcp",headers,
                body.ToJsonString(),host:"example.test");
            Assert(result.StartsWith("HTTP/1.1 200",StringComparison.Ordinal),
                "Verified client reaches MCP");
            return JsonNode.Parse(HttpBody(result))!["result"]!.AsObject();
        }
        var firstScopes = await Call(first,"list_workspaces");
        Assert(firstScopes["structuredContent"]!["workspaces"]!.AsArray().Count==1,
            "Default-only client cannot discover another workspace");
        var otherScopes = await Call(second,"list_workspaces");
        Assert(otherScopes["structuredContent"]!["workspaces"]!.AsArray().Count==2,
            "Approved OAuth client sees its extra workspace");
        var scopeRequest = new JsonObject {
            ["workspace_id"] = "second",
            ["tool_name"] = "read_file",
            ["arguments"] = new JsonObject { ["relative_path"] = "second.txt" }
        };
        var forbidden = await Call(first,"workspace_call",
            (JsonObject)scopeRequest.DeepClone());
        Assert(forbidden["isError"]!.GetValue<bool>(),
            "Unapproved workspace call denied by verified client identity");
        var allowed = await Call(second,"workspace_call",
            (JsonObject)scopeRequest.DeepClone());
        Assert(!allowed["isError"]!.GetValue<bool>() &&
            allowed["structuredContent"]!.ToJsonString().Contains("secondary-content"),
            "Approved extra workspace works over real HTTP");
        Console.WriteLine("windows-oauth-http-scope-isolation: ok");
    }
}
