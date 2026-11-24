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
        var identity = (await Call(first, "connection_identity"))["structuredContent"]!;
        Assert(identity["authentication_mode"]!.GetValue<string>() == "verified_oauth_client" &&
            !identity["conversation_authenticated"]!.GetValue<bool>() &&
            identity["verified_client_id"] is not null && identity["allowed_workspace_ids"]!.AsArray().Count == 1,
            "Verified client identity is token-derived, not conversation authentication");
        Assert((await Call(first, "connection_identity", new JsonObject { ["client_id"] = "forged" }))["isError"]!.GetValue<bool>(),
            "Client cannot assert identity");
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
        var directScope = new JsonObject { ["workspace_id"] = "second", ["relative_path"] = "second.txt" };
        Assert((await Call(first, "read_file", (JsonObject)directScope.DeepClone()))["isError"]!.GetValue<bool>(),
            "direct scope cannot bypass verified OAuth permission");
        Assert(!(await Call(second, "read_file", (JsonObject)directScope.DeepClone()))["isError"]!.GetValue<bool>(),
            "direct scope honors verified OAuth permission");
        var started = await Call(first, "start_command", new JsonObject {
            ["request_id"] = "verified-client-task",
            ["command"] = "Start-Sleep -Seconds 8",
            ["cwd"] = "",
            ["timeout_seconds"] = 16
        });
        Assert(!started["isError"]!.GetValue<bool>(),
            "OAuth-verified client owns a new command");
        var session = started["structuredContent"]!["session_id"]!.GetValue<string>();
        var own = (await Call(first, "list_my_commands"))["structuredContent"]!["commands"]!.AsArray();
        var foreign = (await Call(second, "list_my_commands"))["structuredContent"]!["commands"]!.AsArray();
        Assert(own.Count == 1 && own[0]!["session_id"]!.GetValue<string>() == session && foreign.Count == 0,
            "Owner command discovery never leaks foreign task IDs");
        var foreignRead = await Call(second, "read_command_output",
            new JsonObject { ["session_id"] = session });
        Assert(foreignRead["isError"]!.GetValue<bool>(),
            "Second OAuth client cannot read sibling session output");
        var foreignCancel = await Call(second, "cancel_command",
            new JsonObject { ["session_id"] = session });
        Assert(foreignCancel["isError"]!.GetValue<bool>(),
            "Second OAuth client cannot cancel sibling session");
        var foreignID = await Call(second, "start_command", new JsonObject {
            ["request_id"] = "verified-client-task",
            ["command"] = "Start-Sleep -Seconds 8",
            ["cwd"] = "",
            ["timeout_seconds"] = 16
        });
        Assert(foreignID["isError"]!.GetValue<bool>(),
            "Second OAuth client cannot reuse sibling request ID");
        var ownerRead = await Call(first, "read_command_output",
            new JsonObject { ["session_id"] = session });
        Assert(!ownerRead["isError"]!.GetValue<bool>(),
            "Task owner may read its own output");
        var ownerCancel = await Call(first, "cancel_command",
            new JsonObject { ["session_id"] = session });
        Assert(!ownerCancel["isError"]!.GetValue<bool>(),
            "Task owner may cancel its own work");
        Console.WriteLine("windows-oauth-http-scope-and-task-ownership: ok");
    }
}
