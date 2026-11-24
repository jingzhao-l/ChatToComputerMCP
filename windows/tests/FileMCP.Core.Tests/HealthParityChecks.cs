using System.Diagnostics;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private sealed class TestHealthClock : TimeProvider
    {
        private long _offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offset));
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _offset, amount.Ticks);
    }

    private static void CheckTunnelHealthPolicy()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1000);
        Assert(TunnelCloudHealthPolicy.Parse("{}") == (false, null), "no metric cannot prove cloud health");
        Assert(TunnelCloudHealthPolicy.Parse("invalid") == (false, null), "malformed health rejected");
        Assert(TunnelCloudHealthPolicy.Parse("{\"control_plane_poll\":{\"value\":0}}") == (true, null), "zero is not successful poll");
        Assert(TunnelCloudHealthPolicy.Parse("{\"control_plane_poll\":{\"value\":1010}}") == (true, 1010d), "actual poll decoded");
        Assert(TunnelCloudHealthPolicy.Evaluate(null, start, start.AddSeconds(179)) == TunnelCloudHealth.AwaitingPoll, "startup grace");
        Assert(TunnelCloudHealthPolicy.Evaluate(null, start, start.AddSeconds(180)) == TunnelCloudHealth.Stalled, "metric without successful poll stalls");
        Assert(TunnelCloudHealthPolicy.Evaluate(1010, start, start.AddSeconds(129)) == TunnelCloudHealth.Connected, "fresh verified poll");
        Assert(TunnelCloudHealthPolicy.Evaluate(1010, start, start.AddSeconds(130)) == TunnelCloudHealth.AwaitingPoll, "stale grace");
        Assert(TunnelCloudHealthPolicy.Evaluate(1010, start, start.AddSeconds(190)) == TunnelCloudHealth.Stalled, "stale poll stalls");
        Assert(!TunnelCloudHealthPolicy.ValidTimestamp(989, start, start), "old tunnel timestamp rejected");
        Assert(!TunnelCloudHealthPolicy.ValidTimestamp(1031, start, start), "future timestamp rejected");
        Assert(WindowsTunnelProxy.ParseManual("http=localhost:8080;https=127.0.0.1:8443") == "http://127.0.0.1:8443", "HTTPS proxy selected");
        Assert(WindowsTunnelProxy.ParseManual("proxy.example:3128") == "http://proxy.example:3128", "manual proxy parsed");
        Assert(WindowsTunnelProxy.ParseManual("socks=127.0.0.1:1080") is null, "unsupported SOCKS not guessed");
        Assert(WindowsTunnelProxy.ParseManual("http://user:secret@proxy.example:8080") is null, "authenticated proxy not copied");
        var env = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["HTTPS_PROXY"] = "http://explicit:8080" };
        WindowsTunnelProxy.ApplyFallback(env, () => throw new Exception("Explicit proxy override lost"));
        env.Clear();
        WindowsTunnelProxy.ApplyFallback(env, () => "http://127.0.0.1:8443");
        Assert(env["CONTROL_PLANE_HTTP_PROXY"] == "http://127.0.0.1:8443" && env.Count == 1, "system proxy scoped to control plane");
        var redactor = new StreamingSecretRedactor(["abc-SECRET-xyz", "secondary"]);
        var redacted = redactor.Append("before abc-SE") + redactor.Append("CRET-xyz sec") +
            redactor.Append("ondary after") + redactor.Append("", final: true);
        Assert(redacted == "before [REDACTED] [REDACTED] after", "split secret redaction");
        var monitor = new CloudflareConnectionMonitor();
        Assert(!monitor.Consume("INF Registered tunnel ") && monitor.Consume("connection connIndex=0") &&
            !monitor.Consume("Registered tunnel connection"), "split cloudflared registration recognized once");
        Console.WriteLine("PASS: Windows cloud health, system proxy precedence, split secret and registration parsing");
    }

    private static async Task WaitHealthAsync(LocalMcpRuntime runtime, TunnelCloudHealth health)
    {
        var elapsed = Stopwatch.StartNew();
        while (runtime.CloudHealth != health && elapsed.Elapsed < TimeSpan.FromSeconds(12))
        {
            runtime.CheckCloudHealthNow();
            await Task.Delay(100);
        }
        Assert(runtime.CloudHealth == health, "runtime health reaches " + health);
    }
}
