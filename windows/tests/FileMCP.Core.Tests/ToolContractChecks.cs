using System.Text.Json.Nodes;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task DumpBaseToolContractAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ctcmcp-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new JsonObject();
            foreach (var profile in new[] { "default", "full", "read_only" })
            {
                var writable = profile != "read_only";
                var full = profile == "full";
                await using var server = new LocalMcpServer(1, root, "", "", full, new string('a',64), _ => {},
                    enableGitPush: full, enableCodexHistory: full,
                    defaultWorkspacePermissions: new WorkspacePermissions(writable, full, full, full));
                profiles[profile] = new JsonArray(server.PublishedToolDefinitions()
                    .Where(item => !item!["name"]!.GetValue<string>().StartsWith("cua_",StringComparison.Ordinal))
                    .Select(item => item!.DeepClone()).ToArray());
            }
            Console.WriteLine(profiles.ToJsonString());
        }
        finally { Directory.Delete(root, true); }
    }
}
