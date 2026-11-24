namespace FileMCP.Core;

/// <summary>Per-stream redaction, retaining only a possible unfinished secret prefix.</summary>
internal sealed class StreamingSecretRedactor(IEnumerable<string> secrets)
{
    private readonly string[] _secrets = secrets.Where(secret => !string.IsNullOrEmpty(secret))
        .Distinct(StringComparer.Ordinal).OrderByDescending(secret => secret.Length).ToArray();
    private string _pending = "";

    internal string Append(string chunk, bool final = false)
    {
        if (_secrets.Length == 0) return chunk;
        var data = _pending + chunk;
        var output = new System.Text.StringBuilder(data.Length);
        var offset = 0;
        while (offset < data.Length)
        {
            var remaining = data.AsSpan(offset);
            string? match = null;
            bool partial = false;
            foreach (var secret in _secrets)
            {
                if (remaining.StartsWith(secret.AsSpan(), StringComparison.Ordinal)) { match = secret; break; }
                if (secret.AsSpan().StartsWith(remaining, StringComparison.Ordinal)) partial = true;
            }
            if (!final && partial) break;
            if (match is not null)
            {
                output.Append("[REDACTED]"); offset += match.Length;
            }
            else if (final && partial)
            {
                output.Append("[REDACTED]"); offset = data.Length;
            }
            else output.Append(data[offset++]);
        }
        _pending = data[offset..];
        return output.ToString();
    }
}

internal sealed class CloudflareConnectionMonitor
{
    private readonly object _gate = new();
    private string _tail = "";
    private bool _registered;
    internal bool Consume(string text)
    {
        lock (_gate)
        {
            if (_registered) return false;
            var combined = _tail + text;
            _registered = combined.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase);
            _tail = combined.Length > 64 ? combined[^64..] : combined;
            return _registered;
        }
    }
}
