using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task TestWindowsNamedCloudflareAsync(string testRoot)
    {
        var root=Path.Combine(testRoot,"named-cloudflare-runtime");
        Directory.CreateDirectory(root);
        var workspace=Path.Combine(root,"workspace");
        Directory.CreateDirectory(workspace);
        var tunnelName="ctcmcp-test-"+Guid.NewGuid().ToString("N")[..10];
        var tunnelID=Guid.NewGuid().ToString();
        var cloudflareHome=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cloudflared");
        var credentials=Path.Combine(cloudflareHome,tunnelID+".json");
        Directory.CreateDirectory(cloudflareHome);
        Assert(!File.Exists(credentials),
            "Do not overwrite an existing Cloudflare tunnel credential");
        File.WriteAllText(credentials,"{\"AccountTag\":\"test\",\"TunnelID\":\""+tunnelID+"\"}");
        var capture=Path.Combine(root,"capture.txt");
        var saved=new Dictionary<string,string?>();
        var env=new Dictionary<string,string> {
            ["MCP_CLOUDFLARED"]=Environment.ProcessPath!,
            ["MCP_TEST_FAKE_CLOUDFLARED"]="1",
            ["MCP_TEST_CF_CAPTURE"]=capture,
            ["MCP_TEST_CF_ID"]=tunnelID,
            ["MCP_TEST_CF_NAME"]=tunnelName
        };
        foreach(var variable in env)
        {
            saved[variable.Key]=Environment.GetEnvironmentVariable(variable.Key);
            Environment.SetEnvironmentVariable(variable.Key,variable.Value);
        }
        await using var runtime=new LocalMcpRuntime(Path.Combine(root,"profiles"));
        try
        {
            var certificate = Path.Combine(root, "isolated-cert.pem");
            var previousCert = Environment.GetEnvironmentVariable("TUNNEL_ORIGIN_CERT");
            var previousMode = Environment.GetEnvironmentVariable("MCP_TEST_CF_LOGIN_MODE");
            try
            {
                Environment.SetEnvironmentVariable("TUNNEL_ORIGIN_CERT", certificate);
                File.WriteAllText(certificate, "old-test-certificate");
                Environment.SetEnvironmentVariable("MCP_TEST_CF_LOGIN_MODE", "fail");
                try { await LocalMcpRuntime.LoginCloudflareAsync(true, CancellationToken.None); throw new Exception("Expected failed login"); }
                catch (FileMcpException) {}
                Assert(File.ReadAllText(certificate) == "old-test-certificate", "failed login restores previous certificate");
                Environment.SetEnvironmentVariable("MCP_TEST_CF_LOGIN_MODE", "cancel");
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                try { await LocalMcpRuntime.LoginCloudflareAsync(true, cancel.Token); throw new Exception("Expected cancelled login"); }
                catch (OperationCanceledException) {}
                Assert(File.ReadAllText(certificate) == "old-test-certificate", "cancelled login restores previous certificate");
                Environment.SetEnvironmentVariable("MCP_TEST_CF_LOGIN_MODE", "success");
                var backup = await LocalMcpRuntime.LoginCloudflareAsync(true, CancellationToken.None);
                Assert(backup is not null && File.ReadAllText(backup) == "old-test-certificate" &&
                    File.ReadAllText(certificate) == "new-test-certificate", "successful switch retains previous certificate");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TUNNEL_ORIGIN_CERT", previousCert);
                Environment.SetEnvironmentVariable("MCP_TEST_CF_LOGIN_MODE", previousMode);
            }
            var list=LocalMcpRuntime.ListCloudflareNamedTunnels();
            Assert(list.Any(t=>t.Id==tunnelID && t.Name==tunnelName),
                "Locally-managed Cloudflare tunnel discovery works");
            LocalMcpRuntime.CreateCloudflareNamedTunnel(tunnelName);
            LocalMcpRuntime.RouteCloudflareHostname(tunnelName,"mcp.example.test");
            var configuration=new LocalMcpConfiguration(
                "", "", "cf-named-test", (ushort)FreePort(), workspace,
                "127.0.0.1:0", "Test", "test@example.invalid", false,
                Provider:"cloudflare",CloudflareHostname:"mcp.example.test",
                CloudflarePairingCode:new string('n',64),
                CloudflareMode:"named",CloudflareTunnelName:tunnelName);
            await runtime.StartAsync(configuration);
            Assert(runtime.State.Status==LocalMcpRuntimeStatus.Running,
                "Named Cloudflare provider runtime initiated");
            var deadline=DateTimeOffset.UtcNow.AddSeconds(12);
            while(!File.Exists(capture) && DateTimeOffset.UtcNow<deadline)
                await Task.Delay(50);
            Assert(File.Exists(capture),"Named Cloudflare child started");
            var content=File.ReadAllText(capture);
            Assert(content.Contains("valid=True",StringComparison.OrdinalIgnoreCase),
                "Private ingress configuration routes full HTTPS hostname to local MCP");
            var configFile=content.Split('\n')
                .First(text=>text.StartsWith("named-config=",StringComparison.Ordinal))
                ["named-config=".Length..];
            await runtime.StopAsync();
            Assert(!File.Exists(configFile),
                "Ephemeral named Cloudflare ingress file removed after disconnect");
            LocalMcpRuntime.DeleteCloudflareNamedTunnel(tunnelName);
            Console.WriteLine("windows-named-cloudflare-cli-and-runtime: ok");
        }
        finally
        {
            await runtime.DisposeAsync();
            try {File.Delete(credentials);} catch {}
            foreach(var variable in env)
                Environment.SetEnvironmentVariable(variable.Key,saved[variable.Key]);
        }
    }
}
