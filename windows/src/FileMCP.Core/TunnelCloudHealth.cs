using System.Text.Json;
namespace FileMCP.Core;

public enum TunnelCloudHealth { AwaitingPoll, Connected, Stalled }

internal static class TunnelCloudHealthPolicy
{
    internal static (bool HasMetric, double? Timestamp) Parse(string json)
    {
        try {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("control_plane_poll", out var poll) ||
                poll.ValueKind != JsonValueKind.Object) return (false, null);
            if (!poll.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var timestamp) || !double.IsFinite(timestamp) || timestamp < 0) return (false, null);
            return (true, timestamp == 0 ? null : timestamp);
        } catch (JsonException) { return (false, null); }
    }

    internal static bool ValidTimestamp(double timestamp, DateTimeOffset started, DateTimeOffset now) =>
        double.IsFinite(timestamp) && timestamp > 0 && timestamp >= started.ToUnixTimeMilliseconds() / 1000d - 10 &&
        timestamp <= now.ToUnixTimeMilliseconds() / 1000d + 30;

    // Require evidence from the current health probe. Missing or invalid
    // observations cannot justify restarting an otherwise live transport.
    internal static TunnelCloudHealth EvaluateProbe((bool HasMetric, double? Timestamp) observation,
        DateTimeOffset started, DateTimeOffset now)
    {
        if (!observation.HasMetric ||
            (observation.Timestamp is double stamp && !ValidTimestamp(stamp, started, now)))
            return TunnelCloudHealth.AwaitingPoll;
        return Evaluate(observation.Timestamp, started, now);
    }

    internal static TunnelCloudHealth Evaluate(double? lastPoll, DateTimeOffset started, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeMilliseconds() / 1000d;
        double? valid = lastPoll is double stamp && ValidTimestamp(stamp, started, now) ? stamp : null;
        if (valid is double recent && seconds - recent < 120) return TunnelCloudHealth.Connected;
        return seconds - (valid ?? started.ToUnixTimeMilliseconds() / 1000d) >= 180 ?
            TunnelCloudHealth.Stalled : TunnelCloudHealth.AwaitingPoll;
    }
}
