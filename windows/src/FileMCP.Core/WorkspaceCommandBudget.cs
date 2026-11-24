namespace FileMCP.Core;

/// <summary>
/// One process-wide budget shared by all user-approved workspace roots.
/// Cwd collision checking is cooperative; it is not a shell sandbox.
/// </summary>
internal sealed class WorkspaceCommandBudget
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<CommandSession, string> _long = new();
    private readonly Dictionary<Guid, string> _sync = new();
    private readonly List<(CommandSession Session, Action Launch)> _pending = [];
    private bool _pumpScheduled;

    public WorkspaceCommandBudget(int capacity)
    {
        _capacity = Math.Clamp(capacity, 4, 16);
    }

    private List<string> ActivePaths()
    {
        foreach (var stale in _long.Where(entry => !entry.Key.Active).Select(entry => entry.Key).ToArray())
            _long.Remove(stale);
        return _long.Values.Concat(_sync.Values).ToList();
    }

    private static bool Overlaps(string lhs, string rhs) =>
        string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase) ||
        lhs.StartsWith(Path.TrimEndingDirectorySeparator(rhs) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
        rhs.StartsWith(Path.TrimEndingDirectorySeparator(lhs) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private void Check(string cwd)
    {
        var paths = ActivePaths();
        _pending.RemoveAll(item => !item.Session.Active);
        if (_pending.Count != 0) throw new FileMcpException("Global command queue has waiting jobs; use queue_if_busy or retry after it drains");
        if (paths.Count >= _capacity) throw new FileMcpException(
            "Concurrent command limit reached (" + _capacity + ")");
        if (paths.Any(existing => Overlaps(existing, cwd))) throw new FileMcpException(
            "Another active command occupies this working directory or a parent/child directory; use an independent worktree");
    }

    public void Admit(CommandSession session, string cwd)
    {
        lock (_gate)
        {
            Check(cwd);
            _long.Add(session, cwd);
        }
    }

    public void Enqueue(CommandSession session, Action launch)
    {
        lock (_gate)
        {
            _pending.RemoveAll(item => !item.Session.Active);
            if (_pending.Count >= 64) throw new FileMcpException("Global command queue is full (64)");
            _pending.Add((session, launch));
            SchedulePumpUnderLock();
        }
    }

    private void SchedulePumpUnderLock()
    {
        if (_pumpScheduled || _pending.Count == 0) return;
        _pumpScheduled = true;
        _ = Task.Run(async () => { await Task.Delay(50).ConfigureAwait(false); Pump(); });
    }

    private void Pump()
    {
        List<Action> launches = [];
        lock (_gate)
        {
            _pumpScheduled = false;
            _pending.RemoveAll(item => !item.Session.Active);
            var paths = ActivePaths();
            while (_pending.Count > 0 && paths.Count < _capacity)
            {
                var item = _pending[0];
                if (paths.Any(path => Overlaps(path, item.Session.Cwd))) break;
                _pending.RemoveAt(0);
                _long.Add(item.Session, item.Session.Cwd);
                paths.Add(item.Session.Cwd);
                launches.Add(item.Launch);
            }
            SchedulePumpUnderLock();
        }
        foreach (var launch in launches) launch();
    }

    public void Forget(CommandSession session)
    {
        lock (_gate)
        {
            _long.Remove(session);
            _pending.RemoveAll(item => ReferenceEquals(item.Session, session));
        }
    }

    public Guid AcquireSynchronous(string cwd)
    {
        lock (_gate)
        {
            Check(cwd);
            var lease = Guid.NewGuid();
            _sync.Add(lease, cwd);
            return lease;
        }
    }

    public void Release(Guid id)
    {
        lock (_gate) _sync.Remove(id);
    }

    public bool HasActiveCommands()
    {
        lock (_gate) return ActivePaths().Count > 0 || _pending.Any(item => item.Session.Active);
    }

    public bool HasActivityUnderRoot(string root)
    {
        lock (_gate) return ActivePaths().Concat(_pending.Where(item => item.Session.Active).Select(item => item.Session.Cwd)).Any(path =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    public System.Text.Json.Nodes.JsonObject Status()
    {
        lock (_gate)
        {
            var paths=ActivePaths();
            return new System.Text.Json.Nodes.JsonObject
            {
                ["max_concurrent_commands"]=_capacity,
                ["active_long_running"]=_long.Count,
                ["active_synchronous"]=_sync.Count,
                ["active_total"]=paths.Count,
                ["queued_total"]=_pending.Count(item => item.Session.Active),
                ["available_slots"]=Math.Max(0,_capacity-paths.Count),
                ["accepting_new_commands"]=true,
            };
        }
    }
}
