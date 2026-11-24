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
        Console.WriteLine("PASS: Windows cloud health policy, missing/invalid metrics, freshness, staleness and clock bounds");
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
