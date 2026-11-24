using System.Text.Json.Nodes;

namespace FileMCP.Core;

/// <summary>Local-only inspection and cancellation of a synchronous MCP command.</summary>
internal sealed class SynchronousTaskControl
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _token;
    private readonly BoundedTextBuffer _stdout = new(131_072);
    private readonly BoundedTextBuffer _stderr = new(131_072);
    private string _state = "running";
    public string SessionId { get; } = "sync-" + Guid.NewGuid().ToString("D");
    public string Cwd { get; }
    public string? OwnerId { get; }
    public CancellationToken Token => _token;
    public string State { get { lock (_gate) return _state; } }
    public bool Active => State is "running" or "stopping";

    public SynchronousTaskControl(string cwd, string? ownerId, CancellationToken cancellation)
    {
        Cwd = cwd; OwnerId = ownerId;
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _token = _source.Token;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state != "running") return;
            _state = "stopping";
            _source.Cancel();
        }
    }

    public void Finish(string state)
    {
        lock (_gate)
        {
            _state = _token.IsCancellationRequested ? "cancelled" : state;
            _source.Dispose();
        }
    }

    public void Append(string value, bool error)
    {
        (error ? _stderr : _stdout).Append(value.AsSpan());
    }

    public JsonObject Snapshot()
    {
        var stdout = _stdout.ToString();
        var stderr = _stderr.ToString();
        return new JsonObject { ["state"] = State, ["output"] = stdout + (stderr.Length == 0 ? "" : "\nstderr:\n" + stderr),
            ["truncated"] = _stdout.Truncated || _stderr.Truncated };
    }
}
