using FileMCP.Core;
using System.Security.Cryptography;
using System.Text;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task<int> RunFakeCloudflaredAsync(string[] args)
    {
        // Cloudflare runner tests NEVER contact the Cloudflare API.
        if (args is ["tunnel", "login"])
        {
            var certificate = Environment.GetEnvironmentVariable("TUNNEL_ORIGIN_CERT");
            if (string.IsNullOrEmpty(certificate)) return 31;
            var mode = Environment.GetEnvironmentVariable("MCP_TEST_CF_LOGIN_MODE");
            if (mode == "fail") return 32;
            if (mode == "cancel") { await Task.Delay(Timeout.InfiniteTimeSpan); return 33; }
            await File.WriteAllTextAsync(certificate, "new-test-certificate");
            return 0;
        }
        var capture=Environment.GetEnvironmentVariable("MCP_TEST_CF_CAPTURE");
        if (string.IsNullOrWhiteSpace(capture)) return 12;
        if (args is ["tunnel","list","-o","json"])
        {
            var identifier=Environment.GetEnvironmentVariable("MCP_TEST_CF_ID") ??
                "11111111-1111-1111-1111-111111111111";
            var name=Environment.GetEnvironmentVariable("MCP_TEST_CF_NAME") ?? "test-named";
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new[] {
                new { id=identifier, name }
            }));
            return 0;
        }
        if (args is ["tunnel","create",_]) return 0;
        if (args is ["tunnel","delete",_]) return 0;
        if (args is ["tunnel","route","dns",_,_]) return 0;
        if (args.Length == 8 && args[0] == "tunnel" &&
            args[1] == "--no-autoupdate" && args[2] == "--config" &&
            args[4] == "run" && args[5] == "--credentials-file")
        {
            var config=await File.ReadAllTextAsync(args[3]);
            if(!config.Contains("ingress:",StringComparison.Ordinal) ||
               !config.Contains("service: http://127.0.0.1:",StringComparison.Ordinal))
                return 23;
            await File.WriteAllTextAsync(capture,
                "named-config="+args[3]+"\nvalid="+
                config.Contains("hostname: mcp.example.test",StringComparison.Ordinal)+"\n");
            Console.WriteLine("Registered tunnel connection");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (args is ["tunnel","--no-autoupdate","run","--token-file",var tokenPath])
        {
            var secret=await File.ReadAllTextAsync(tokenPath);
            var hash=Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            await File.WriteAllTextAsync(capture,
                "token-file="+tokenPath+"\nsecret-sha256="+hash+"\n");
            Console.WriteLine("Registered tunnel connection");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        else return 13;
        return 0;
    }

    private static async Task TestWindowsCloudflareRuntimeAsync(string baseRoot)
    {
        var root=Path.Combine(baseRoot,"cloudflare-runtime");
        Directory.CreateDirectory(root);
        var workspace=Path.Combine(root,"workspace");
        Directory.CreateDirectory(workspace);
        var capture=Path.Combine(root,"capture.txt");
        var uniqueProfile="cf-runtime-"+Guid.NewGuid().ToString("N");
        var token="test-only-cloudflare-token-"+Guid.NewGuid().ToString("N");
        var pairing=new string('p',64);
        var previous=Environment.GetEnvironmentVariable("MCP_CLOUDFLARED");
        var previousFlag=Environment.GetEnvironmentVariable("MCP_TEST_FAKE_CLOUDFLARED");
        var previousCapture=Environment.GetEnvironmentVariable("MCP_TEST_CF_CAPTURE");
        Environment.SetEnvironmentVariable("MCP_CLOUDFLARED",Environment.ProcessPath);
        Environment.SetEnvironmentVariable("MCP_TEST_FAKE_CLOUDFLARED","1");
        Environment.SetEnvironmentVariable("MCP_TEST_CF_CAPTURE",capture);
        await using var runtime=new LocalMcpRuntime(Path.Combine(root,"profiles"));
        var logs=new StringBuilder();
        runtime.Log+=text=>logs.Append(text);
        try
        {
            var config=new LocalMcpConfiguration(
                "", "", uniqueProfile, (ushort)FreePort(), workspace,
                "127.0.0.1:0", "Test", "test@example.invalid", true,
                Provider:"cloudflare",
                CloudflareHostname:"mcp.example.test",
                CloudflareToken:token,
                CloudflarePairingCode:pairing);
            await runtime.StartAsync(config);
            Assert(runtime.State.Status==LocalMcpRuntimeStatus.Running,
                "Cloudflare provider runtime starts with locally approved settings");
            var deadline=DateTimeOffset.UtcNow.AddSeconds(12);
            while(!File.Exists(capture) && DateTimeOffset.UtcNow<deadline)
                await Task.Delay(50);
            Assert(File.Exists(capture),"Fake Cloudflare client process actually ran");
            var info=File.ReadAllText(capture);
            Assert(info.Contains("token-file=",StringComparison.Ordinal),
                "Tunnel secret delivered in a private file, not command arguments");
            Assert(!info.Contains(token,StringComparison.Ordinal),
                "Capture cannot reveal the tunnel token");
            Assert(!logs.ToString().Contains(token,StringComparison.Ordinal),
                "Tunnel token redacted from app logs");
            var path=info.Split('\n').First(x=>x.StartsWith("token-file="))["token-file=".Length..];
            await runtime.StopAsync();
            Assert(runtime.State.Status==LocalMcpRuntimeStatus.Stopped,
                "Cloudflare provider shutdown");
            Assert(!File.Exists(path),
                "Ephemeral tunnel token file deleted after disconnect");
        }
        finally
        {
            await runtime.DisposeAsync();
            Environment.SetEnvironmentVariable("MCP_CLOUDFLARED",previous);
            Environment.SetEnvironmentVariable("MCP_TEST_FAKE_CLOUDFLARED",previousFlag);
            Environment.SetEnvironmentVariable("MCP_TEST_CF_CAPTURE",previousCapture);
        }
        Console.WriteLine("windows-cloudflare-runtime-token-file: ok");
    }
}
