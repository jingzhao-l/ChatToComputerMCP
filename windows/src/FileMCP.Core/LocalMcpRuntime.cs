using System.Collections;
using System.Security.Cryptography;
using System.Text;

namespace FileMCP.Core;

internal sealed class ProfileLock : IDisposable
{
    private readonly string _name;
    private readonly object _gate = new();
    private Thread? _owner;
    private ManualResetEventSlim? _release;

    public ProfileLock(string profile)
    {
        var hash = Fnv1A64(Encoding.UTF8.GetBytes(profile));
        // Keep the deployed mutex namespace. A Windows mutex is owned by a
        // thread, not a process: async runtime continuations cannot release it.
        _name = $@"Local\ChatToComputerMCP-profile-{hash:x16}";
    }

    public void Acquire()
    {
        lock (_gate)
        {
            if (_owner is not null) return;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim(false);
            var thread = new Thread(() => {
                try
                {
                    using var mutex = new Mutex(false, _name);
                    bool held;
                    try { held = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { held = true; }
                    ready.TrySetResult(held);
                    if (held)
                    {
                        try { release.Wait(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                catch (Exception error) { ready.TrySetException(error); }
            }) { IsBackground = true, Name = "CTCMCP profile lease" };
            thread.Start();
            try
            {
                if (!ready.Task.GetAwaiter().GetResult())
                    throw new FileMcpException("This tunnel profile is already in use by another ChatToComputerMCP instance.");
                _owner = thread; _release = release;
            }
            catch { release.Set(); thread.Join(); release.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_owner is null) return;
            _release!.Set(); _owner.Join();
            _release.Dispose(); _release = null; _owner = null;
        }
    }

    private static ulong Fnv1A64(IEnumerable<byte> bytes)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var value in bytes) hash = (hash ^ value) * 1099511628211UL;
        return hash;
    }
}

public sealed class LocalMcpRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string? _profileDirectoryOverride;
    private readonly string? _dataDirectoryOverride;
    private LocalMcpRuntimeState _state = LocalMcpRuntimeState.Stopped;
    private CancellationTokenSource? _startupCts;
    private int _disposed;
    private LocalMcpServer? _server;
    private LocalMcpConfiguration? _activeConfiguration;
    private ManagedProcess? _tunnelProcess;
    private string? _localAuthToken;
    private string? _activeHealthAddress;
    private Guid _transportAttempt;
    private Guid _recoveryGeneration;
    private int _transportRetryDelay = 2;
    private bool _transportStarted;
    private ProfileLock? _profileLock;
    private string? _cloudflareTokenFile;
    private readonly SystemSleepGuard _sleepGuard = new();
    private bool _requestedStop;
    private CancellationTokenSource? _healthCts;
    private string? _healthDirectory;
    private readonly SemaphoreSlim _healthWake = new(0, 1);
    private TunnelCloudHealth _cloudHealth = TunnelCloudHealth.AwaitingPoll;
    private DateTimeOffset? _stalledSince, _lastStalledRecovery;
    private volatile bool _automaticHealthRecovery;
    public bool AutomaticHealthRecovery { get => _automaticHealthRecovery; set => _automaticHealthRecovery = value; }
    internal TimeProvider HealthClock { get; set; } = TimeProvider.System;
    public TunnelCloudHealth CloudHealth { get { lock (_stateGate) return _cloudHealth; } }
    public event Action<TunnelCloudHealth>? CloudHealthChanged;

    public void CheckCloudHealthNow()
    {
        try { _healthWake.Release(); } catch (SemaphoreFullException) { }
    }

    private void SetCloudHealth(TunnelCloudHealth health)
    {
        lock (_stateGate) _cloudHealth = health;
        CloudHealthChanged?.Invoke(health);
    }

    public LocalMcpRuntime(string? profileDirectory = null, string? dataDirectory = null)
    {
        _profileDirectoryOverride = profileDirectory;
        _dataDirectoryOverride = dataDirectory;
    }

    public event Action<LocalMcpRuntimeState>? StateChanged;
    public event Action<string>? Log;

    public LocalMcpRuntimeState State { get { lock (_stateGate) return _state; } }

    public IReadOnlyList<WindowsOAuthServer.DesktopClient> DesktopOAuthClients() =>
        _server?.DesktopOAuthClients() ?? [];

    public void RevokeDesktopOAuthClient(string id)
    {
        if (_server is null) throw new FileMcpException("Not connected");
        _server.RevokeDesktopOAuthClient(id);
    }

    public IReadOnlyDictionary<string, int> DesktopReceiptSummary() =>
        _server?.DesktopReceiptSummary() ?? new Dictionary<string, int>();

    public IReadOnlyList<DesktopCommandInfo> DesktopCommandSnapshots() =>
        _server?.DesktopCommandSnapshots() ?? [];

    public Task<System.Text.Json.Nodes.JsonObject> DesktopCommandOutputAsync(
        string workspaceID, string sessionID)
    {
        if (_server is null) throw new FileMcpException("No connected local server");
        return _server.DesktopCommandOutputAsync(workspaceID, sessionID);
    }

    public void CancelDesktopCommand(string workspaceID, string sessionID)
    {
        if (_server is null) throw new FileMcpException("No connected local server");
        _server.CancelDesktopCommand(workspaceID, sessionID);
    }

    private static (string Policy, string Description) WorkspaceOAuthPolicy(LocalMcpConfiguration configuration, IReadOnlyList<ApprovedWorkspace> approved)
    {
        if (approved.Any(item => !Directory.Exists(item.Path)))
            throw new FileMcpException("Approved workspace directory is missing");
        var policy = System.Text.Json.JsonSerializer.Serialize(new {
            Default = new SafePathResolver(configuration.AllowedDirectory).Root,
            configuration.Port, configuration.EnableCommands, configuration.EnableGitPush,
            configuration.EnableCodexHistory, configuration.Profile,
            DefaultPermissions = configuration.DefaultWorkspacePermissions?.Fingerprint ?? "inherit",
            Host = configuration.CloudflareHostname.ToLowerInvariant(),
            Workspaces = approved.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new {
                item.Id, Path = new SafePathResolver(item.Path).Root,
                Permissions = item.Permissions?.Fingerprint ?? "inherit"
            })
        });
        var description = "File and Git access; shell commands " + (configuration.EnableCommands ? "enabled" : "disabled") +
            "; Git push " + (configuration.EnableGitPush ? "enabled" : "disabled") +
            "; Codex import " + (configuration.EnableCodexHistory ? "enabled" : "disabled") +
            ". Default workspace: " + (configuration.DefaultWorkspacePermissions?.Summary ?? "Inherit global permissions") +
            ". Individual workspace restrictions: " + string.Join("; ", approved.Select(item =>
                item.Name + ": " + (item.Permissions?.Summary ?? "Inherit global permissions")));
        return (policy, description);
    }

    public async Task ReplaceDesktopWorkspacesAsync(IReadOnlyList<ApprovedWorkspace> definitions, Action persist)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Status != LocalMcpRuntimeStatus.Running || _server is null || _activeConfiguration is null)
                throw new FileMcpException("Not connected");
            var configuration = _activeConfiguration;
            var (policy, description) = configuration.Provider == "cloudflare"
                ? WorkspaceOAuthPolicy(configuration, definitions) : (null, null);
            _server.ReplaceAdditionalWorkspaces(definitions, policy, description, persist);
            _activeConfiguration = configuration with { AdditionalWorkspaces = definitions.ToArray() };
        }
        finally { _serial.Release(); }
    }

    public async Task StartAsync(LocalMcpConfiguration configuration)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Status is not (LocalMcpRuntimeStatus.Stopped or LocalMcpRuntimeStatus.Failed)) return;
            if (_requestedStop) { _requestedStop = false; SetState(LocalMcpRuntimeState.Stopped); return; }
            SetState(LocalMcpRuntimeState.Starting);
            _transportStarted = false; _transportRetryDelay = 2; _recoveryGeneration = Guid.NewGuid();
            _lastStalledRecovery = null;
            _startupCts?.Dispose(); _startupCts = new CancellationTokenSource();
            var cancellationToken = _startupCts.Token;
            try
            {
                var healthAddress = ValidateConfiguration(configuration);
                var isCloudflare = configuration.Provider == "cloudflare";
                _profileLock = new ProfileLock(configuration.Profile); _profileLock.Acquire();
                var localAuthToken = MakeLocalAuthToken();
                // A corrupt receipt journal fails closed and cannot allow
                // blind replays. Never read FileMCP's old Application Data.
                var receipts = configuration.EnableCommands
                    ? new CommandReceiptLedger(_dataDirectoryOverride is null ? null :
                        Path.Combine(_dataDirectoryOverride, "receipts", "commands.json")) : null;
                WindowsOAuthServer? oauth = null;
                if (isCloudflare)
                {
                    var approved = configuration.AdditionalWorkspaces ?? [];
                    var (policy, description) = WorkspaceOAuthPolicy(configuration, approved);
                    oauth = new WindowsOAuthServer(
                        "https://"+configuration.CloudflareHostname,
                        configuration.CloudflarePairingCode, policy, description, approved);
                }
                var history = configuration.EnableCommands ? new CommandHistoryStore(
                    _dataDirectoryOverride is null ? CommandHistoryStore.DirectoryFor(configuration.Profile) :
                    Path.Combine(_dataDirectoryOverride, "history", configuration.Profile), configuration.CaptureCommandHistoryContent) : null;
                var audit = new ActivityAuditStore(_dataDirectoryOverride is null ? ActivityAuditStore.DirectoryFor(configuration.Profile) :
                    Path.Combine(_dataDirectoryOverride, "audit", configuration.Profile));
                var supervisorDirectory = _dataDirectoryOverride is null ? CommandSupervisor.DirectoryFor(configuration.Profile) : Path.Combine(_dataDirectoryOverride, "supervised-jobs", configuration.Profile);
                var supervisor = (configuration.PersistLongTasks && configuration.EnableCommands) || Directory.Exists(supervisorDirectory) ? new CommandSupervisor(supervisorDirectory, configuration.PersistLongTasks && configuration.EnableCommands) : null;
                _server = new LocalMcpServer(configuration.Port, configuration.AllowedDirectory, configuration.GitUserName,
                    configuration.GitUserEmail, configuration.EnableCommands, localAuthToken, EmitLog,
                    configuration.AdditionalWorkspaces, configuration.MaxConcurrentCommands,
                    receiptLedger: receipts, oauth: oauth,
                    publicHost: isCloudflare ? configuration.CloudflareHostname : null,
                    enableGitPush: configuration.EnableGitPush,
                    enableCodexHistory: configuration.EnableCodexHistory, commandHistory: history,
                    defaultWorkspacePermissions: configuration.DefaultWorkspacePermissions, activityAudit: audit, commandSupervisor: supervisor);
                await _server.StartAsync(cancellationToken).ConfigureAwait(false);
                _activeConfiguration = configuration;

                _localAuthToken = localAuthToken;
                _activeHealthAddress = healthAddress;
                await LaunchTransportAsync(configuration, localAuthToken, healthAddress, initialize: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_requestedStop || _startupCts?.IsCancellationRequested == true)
            {
                FinishStop();
            }
            catch (Exception ex)
            {
                CleanupRuntime();
                SetState(LocalMcpRuntimeState.Failed(ex.Message));
                EmitLog("ERROR: " + ex.Message + "\n");
            }
        }
        finally { _serial.Release(); }
    }

    private async Task LaunchTransportAsync(LocalMcpConfiguration configuration, string localAuthToken,
        string healthAddress, bool initialize, CancellationToken cancellationToken)
    {
        _transportAttempt = Guid.NewGuid();
        var attempt = _transportAttempt;
        if (configuration.Provider == "cloudflare")
        {
            var path = WindowsCloudflareRunner.Locate();
            var start = configuration.CloudflareMode == "named"
                ? WindowsCloudflareRunner.PrepareNamed(path,
                    configuration.CloudflareTunnelName,
                    configuration.CloudflareHostname,
                    configuration.Port,configuration.Profile)
                : WindowsCloudflareRunner.Prepare(path,
                    configuration.CloudflareToken,configuration.Profile);
            _cloudflareTokenFile = start.TokenFile;
            var registered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var registrationMonitor = new CloudflareConnectionMonitor();
            _tunnelProcess = ProcessRunner.StartManaged(start.Executable,start.Args,
                null,start.Environment,
                output =>
                {
                    if (registrationMonitor.Consume(output))
                        registered.TrySetResult(true);
                    EmitLog(Redact(output, [
                        configuration.CloudflareToken,configuration.CloudflarePairingCode]));
                },
                code =>
                {
                    registered.TrySetException(new FileMcpException(
                        "Cloudflare tunnel exited before registration: " + code));
                    _ = Task.Run(() => TunnelDidExitAsync(code, attempt));
                }, sensitiveValues: [configuration.CloudflareToken, configuration.CloudflarePairingCode]);
            EmitLog("Waiting for Cloudflare Tunnel to register a secure connection…\n");
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(40),
                cancellationToken).ConfigureAwait(false);
            if (configuration.CloudflareMode == "token" &&
                _cloudflareTokenFile is { } pendingToken)
            {
                // The managed cloudflared process has read its token.
                // Remove the ephemeral file as soon as registration
                // succeeds; the durable copy stays only in CredMgr.
                WindowsCloudflareRunner.RemovePrivateToken(pendingToken);
                _cloudflareTokenFile = null;
            }
            _transportStarted = true; _transportRetryDelay = 2;
            SetState(LocalMcpRuntimeState.Running);
            EmitLog("Cloudflare Tunnel registered for " +
                configuration.CloudflareHostname + ". Ensure DNS and "+
                $"ingress route all OAuth and MCP paths to http://127.0.0.1:{configuration.Port}.\n");
            return;
        }

        var tunnelClient = TunnelClientPath(); var profileDirectory = TunnelProfileDirectory();
        var environment = TunnelClientEnvironment(configuration.ApiKey, localAuthToken);
        var localAuthHeader = $"{FileMcpConstants.LocalAuthHeaderName}: env:FILEMCP_LOCAL_AUTH_TOKEN";
        environment["MCP_EXTRA_HEADERS"] = localAuthHeader; environment["MCP_DISCOVERY_EXTRA_HEADERS"] = localAuthHeader;
        string[] sensitive = [configuration.ApiKey, localAuthToken];

        if (initialize)
        {
        EmitLog("Configuring Secure MCP Tunnel…\n");
        var initResult = await ProcessRunner.RunAsync(tunnelClient,
            ["init", "--sample", "sample_mcp_remote_no_auth", "--profile", configuration.Profile, "--profile-dir", profileDirectory, "--force", "--tunnel-id", configuration.TunnelId, "--mcp-server-url", $"http://127.0.0.1:{configuration.Port}/mcp", "--health-listen-addr", healthAddress],
            environment: environment, timeoutSeconds: 30, outputLimitBytes: 250_000, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); RequireSuccess(initResult, "tunnel-client init", sensitive);

        EmitLog("Checking tunnel configuration…\n");
        var doctorResult = await ProcessRunner.RunAsync(tunnelClient,
            ["doctor", "--profile", configuration.Profile, "--profile-dir", profileDirectory, "--health-listen-addr", healthAddress, "--explain"],
            environment: environment, timeoutSeconds: 30, outputLimitBytes: 250_000, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); RequireSuccess(doctorResult, "tunnel-client doctor", sensitive);

        }
        _healthDirectory = Path.Combine(Path.GetTempPath(), "ctcmcp-health-" + Guid.NewGuid().ToString("N"));
        CommandReceiptLedger.EnsurePrivateFolder(_healthDirectory);
        var locator = Path.Combine(_healthDirectory, "listener.url");
        using (File.Create(locator)) { }
        var started = HealthClock.GetUtcNow();
        SetCloudHealth(TunnelCloudHealth.AwaitingPoll);
        _stalledSince = null;
        _tunnelProcess = ProcessRunner.StartManaged(tunnelClient,
            ["run", "--profile", configuration.Profile, "--profile-dir", profileDirectory, "--health-listen-addr", healthAddress, "--health.url-file", locator],
            null, environment,
            text => EmitLog(Redact(text, sensitive)),
            exitCode => _ = Task.Run(() => TunnelDidExitAsync(exitCode, attempt)), sensitiveValues: sensitive);
        _transportStarted = true;
        SetState(LocalMcpRuntimeState.Running);
        EmitLog($"OpenAI Secure MCP Tunnel process started; verifying cloud polling. Command execution: {(configuration.EnableCommands ? "enabled" : "disabled")}.\n");
        _healthCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var healthToken = _healthCts.Token;
        _ = Task.Run(() => MonitorCloudHealthAsync(attempt, tunnelClient, locator, started, healthToken));
    }

    private async Task MonitorCloudHealthAsync(Guid attempt, string executable, string locator,
        DateTimeOffset started, CancellationToken cancellation)
    {
        try
        {
            await _healthWake.WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            while (!cancellation.IsCancellationRequested)
            {
                (bool HasMetric, double? Timestamp) observation = (false, null);
                try
                {
                    var env = TunnelClientEnvironment("", "");
                    env.Remove("CONTROL_PLANE_API_KEY"); env.Remove("FILEMCP_LOCAL_AUTH_TOKEN");
                    var result = await ProcessRunner.RunAsync(executable,
                        ["health", "--url-file", locator, "--require-control-plane-poll", "--json"],
                        environment: env, timeoutSeconds: 5, outputLimitBytes: 16_384,
                        cancellationToken: cancellation).ConfigureAwait(false);
                    if (!result.TimedOut && !result.StdoutTruncated)
                        observation = TunnelCloudHealthPolicy.Parse(result.Stdout);
                }
                catch (OperationCanceledException) { return; }
                catch { /* Broken local probe is not proof of cloud failure. */ }
                await _serial.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (attempt != _transportAttempt || cancellation.IsCancellationRequested || _requestedStop ||
                        State.Status != LocalMcpRuntimeStatus.Running) return;
                    var now = HealthClock.GetUtcNow();
                    var health = _server?.IsReady == true
                        ? TunnelCloudHealthPolicy.EvaluateProbe(observation, started, now)
                        : TunnelCloudHealth.AwaitingPoll;
                    if (health != CloudHealth)
                    {
                        if (health == TunnelCloudHealth.Connected) EmitLog("OpenAI control-plane polling verified.\n");
                        if (health == TunnelCloudHealth.Stalled) EmitLog("WARNING: OpenAI control-plane polling stalled.\n");
                    }
                    SetCloudHealth(health);
                    if (health == TunnelCloudHealth.Connected) { _transportRetryDelay = 2; _stalledSince = null; }
                    else if (health == TunnelCloudHealth.Stalled)
                    {
                        _stalledSince ??= now;
                        if (AutomaticHealthRecovery && now - _stalledSince >= TimeSpan.FromSeconds(60) &&
                            (_lastStalledRecovery is null || now - _lastStalledRecovery >= TimeSpan.FromMinutes(5)) &&
                            _server?.HasActiveCommands() == false)
                        {
                            _lastStalledRecovery = now;
                            EmitLog("Cloud polling stalled; restarting only the transport.\n");
                            CleanupTransport();
                            ScheduleTransportRecovery();
                            return;
                        }
                    }
                    else _stalledSince = null;
                }
                finally { _serial.Release(); }
                await _healthWake.WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task StopAsync()
    {
        _requestedStop = true; _startupCts?.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Status == LocalMcpRuntimeStatus.Stopped) { _requestedStop = false; return; }
            if (State.Status != LocalMcpRuntimeStatus.Stopping) SetState(LocalMcpRuntimeState.Stopping);
            if (_tunnelProcess is not null) await _tunnelProcess.StopAsync().ConfigureAwait(false);
            FinishStop();
        }
        finally { _serial.Release(); }
    }

    public async Task ActivateUpdateWhenIdleAsync(Action schedule)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (State.Status)
            {
                case LocalMcpRuntimeStatus.Running:
                    if (_server is null) throw new FileMcpException("No running local server");
                    _server.FreezeForIdleActivation(schedule);
                    break;
                case LocalMcpRuntimeStatus.Stopped:
                case LocalMcpRuntimeStatus.Failed:
                    schedule();
                    break;
                default:
                    throw new FileMcpException("Wait until connection startup or shutdown finishes.");
            }
            _requestedStop = true;
            SetState(LocalMcpRuntimeState.Stopping);
            _tunnelProcess?.StopSynchronously();
            FinishStop();
        }
        finally { _serial.Release(); }
    }

    public async Task ShutdownAsync()
    {
        _requestedStop = true; _startupCts?.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _tunnelProcess?.StopSynchronously(); FinishStop(preserveSupervised: true); }
        finally { _serial.Release(); }
    }

    private async Task TunnelDidExitAsync(int exitCode, Guid attempt)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (attempt != _transportAttempt) return;
            CleanupTransport();
            if (_requestedStop) { FinishStop(); return; }
            if (_transportStarted && _server?.IsOperational == true && _activeConfiguration is not null)
            {
                EmitLog($"Tunnel exited (status {exitCode}); retaining the local gateway and command sessions.\n");
                ScheduleTransportRecovery();
                return;
            }
            CleanupRuntime();
            if (exitCode == 0) SetState(LocalMcpRuntimeState.Stopped);
            else SetState(LocalMcpRuntimeState.Failed($"Tunnel stopped unexpectedly (exit status {exitCode})."));
        }
        finally { _serial.Release(); }
    }

    private void ScheduleTransportRecovery()
    {
        if (_requestedStop || _server?.IsOperational != true || _startupCts is null) return;
        _recoveryGeneration = Guid.NewGuid();
        var generation = _recoveryGeneration;
        var delay = _transportRetryDelay;
        _transportRetryDelay = Math.Min(60, delay * 2);
        var cancellation = _startupCts.Token;
        SetState(LocalMcpRuntimeState.Starting);
        EmitLog($"Reconnecting transport in {delay} seconds; local commands continue.\n");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellation).ConfigureAwait(false);
                await _serial.WaitAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            try
            {
                if (generation != _recoveryGeneration || _requestedStop ||
                    _server?.IsOperational != true || _activeConfiguration is null ||
                    _localAuthToken is null || _activeHealthAddress is null) return;
                try {
                    await LaunchTransportAsync(_activeConfiguration, _localAuthToken,
                        _activeHealthAddress, initialize: false, cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { CleanupTransport(); }
                catch {
                    CleanupTransport();
                    EmitLog("Transport restart failed; local tasks retained.\n");
                    ScheduleTransportRecovery();
                }
            }
            finally { _serial.Release(); }
        });
    }

    private void FinishStop(bool preserveSupervised = false)
    {
        CleanupRuntime(preserveSupervised); _requestedStop = false; SetState(LocalMcpRuntimeState.Stopped); EmitLog("Tunnel stopped.\n");
    }

    private void CleanupTransport()
    {
        // Invalidate any delayed callback before disposing the previous child.
        _transportAttempt = Guid.NewGuid();
        _healthCts?.Cancel(); _healthCts?.Dispose(); _healthCts = null;
        _tunnelProcess?.Dispose(); _tunnelProcess = null;
        if (_healthDirectory is { } healthDirectory)
        {
            try { Directory.Delete(healthDirectory, true); } catch { }
            _healthDirectory = null;
        }
        SetCloudHealth(TunnelCloudHealth.AwaitingPoll);
        if (_cloudflareTokenFile is { } token)
        {
            WindowsCloudflareRunner.RemovePrivateToken(token);
            _cloudflareTokenFile = null;
        }
    }

    private void CleanupRuntime(bool preserveSupervised = false)
    {
        _recoveryGeneration = Guid.NewGuid(); _transportStarted = false;
        CleanupTransport();
        _server?.Stop(preserveSupervised); _server = null;
        _activeConfiguration = null; _localAuthToken = null; _activeHealthAddress = null;
        _profileLock?.Dispose(); _profileLock = null;
    }

    public static string ValidateConfiguration(LocalMcpConfiguration configuration)
    {
        foreach (var (label, value) in new[] { ("Profile", configuration.Profile), ("Shared directory", configuration.AllowedDirectory) })
            if (string.IsNullOrWhiteSpace(value)) throw new FileMcpException($"{label} cannot be empty.");
        if (configuration.Provider == "cloudflare")
        {
            if (!WindowsCloudflareRunner.ValidHostname(configuration.CloudflareHostname))
                throw new FileMcpException("Cloudflare public hostname is not a valid DNS hostname");
            if (configuration.CloudflareMode == "token")
            {
                if (string.IsNullOrWhiteSpace(configuration.CloudflareToken))
                    throw new FileMcpException("Cloudflare dashboard tunnel token is required");
            }
            else if (configuration.CloudflareMode == "named")
            {
                if (!WindowsCloudflareRunner.IsNamedTunnel(configuration.CloudflareTunnelName))
                    throw new FileMcpException("Locally managed Cloudflare tunnel name is invalid");
            }
            else throw new FileMcpException("Unknown Cloudflare tunnel mode");
            if (configuration.CloudflarePairingCode.Length is < 32 or > 128 ||
                !configuration.CloudflarePairingCode.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '-' or '_'))
                throw new FileMcpException("Cloudflare OAuth login code is missing or invalid");
        }
        else if (configuration.Provider == "openai")
        {
            if (string.IsNullOrWhiteSpace(configuration.TunnelId) ||
                string.IsNullOrWhiteSpace(configuration.ApiKey))
                throw new FileMcpException("Secure MCP Tunnel ID and runtime API key are required");
            if (!IsValidTunnelId(configuration.TunnelId))
                throw new FileMcpException("Tunnel ID must match tunnel_<32 lowercase letters or digits>.");
        }
        else throw new FileMcpException("Unknown connection provider");
        if (!IsValidProfileName(configuration.Profile)) throw new FileMcpException("Profile must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).");
        if (configuration.Port == 0) throw new FileMcpException("MCP port must be between 1 and 65535.");
        return NormalizeHealthAddress(configuration.HealthAddress) ?? throw new FileMcpException("Health listener must use localhost, 127.0.0.1, or [::1] with a port from 0 to 65535.");
    }

    public static bool IsValidCloudflareHostname(string value) =>
        WindowsCloudflareRunner.ValidHostname(value);

    public static bool IsCloudflareLoggedIn() => WindowsCloudflareRunner.OriginCertificatePath() is not null;
    public static Task<string?> LoginCloudflareAsync(bool replacingExisting, CancellationToken cancellation) =>
        WindowsCloudflareRunner.LoginAsync(replacingExisting, cancellation);

    public static string CloudflareExecutable() =>
        WindowsCloudflareRunner.Locate();

    public static bool IsValidCloudflareTunnelName(string value) =>
        WindowsCloudflareRunner.IsNamedTunnel(value);

    public static IReadOnlyList<CloudflareNamedTunnel> ListCloudflareNamedTunnels() =>
        WindowsCloudflareRunner.ListNamedTunnels()
            .Select(item => new CloudflareNamedTunnel(item.Id,item.Name)).ToArray();

    public static void CreateCloudflareNamedTunnel(string name) =>
        WindowsCloudflareRunner.CreateNamedTunnel(name);

    public static void DeleteCloudflareNamedTunnel(string name) =>
        WindowsCloudflareRunner.DeleteNamedTunnel(name);

    public static void RouteCloudflareHostname(string name,string hostname) =>
        WindowsCloudflareRunner.RouteNamedTunnel(name,hostname);

    public static bool IsValidTunnelId(string value) => value.Length == 39 && value.StartsWith("tunnel_", StringComparison.Ordinal) && value[7..].All(ch => (ch >= 'a' && ch <= 'z') || char.IsAsciiDigit(ch));
    public static bool IsValidProfileName(string value) => value.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) && value.Skip(1).All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    public static string? NormalizeHealthAddress(string value)
    {
        var trimmed = value.Trim(); if (trimmed.Length == 0) return "127.0.0.1:0";
        if (trimmed.StartsWith('[')) { var close = trimmed.IndexOf(']'); if (close < 0 || trimmed[1..close].ToLowerInvariant() != "::1") return null; var rest = trimmed[(close + 1)..]; if (!rest.StartsWith(':') || !ValidHealthPort(rest[1..])) return null; return "[::1]:" + rest[1..]; }
        var parts = trimmed.Split(':', 2); if (parts.Length != 2 || (parts[0].ToLowerInvariant() is not ("127.0.0.1" or "localhost")) || !ValidHealthPort(parts[1])) return null;
        return parts[0].ToLowerInvariant() + ":" + parts[1];
    }

    private static bool ValidHealthPort(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) && uint.TryParse(value, out var port) && port <= 65535;

    private Dictionary<string, string> TunnelClientEnvironment(string apiKey, string localAuthToken)
    {
        var inherited = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        string[] pass = ["PATH","USERPROFILE","HOMEDRIVE","HOMEPATH","APPDATA","LOCALAPPDATA","TEMP","TMP","SystemRoot","WINDIR","LANG","LC_ALL","LC_CTYPE","HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy","CONTROL_PLANE_HTTP_PROXY","TUNNEL_CLIENT_HTTP_PROXY","MCP_HTTP_PROXY","HARPOON_HTTP_PROXY","CA_BUNDLE","PROXY_CHECK_INTERVAL"];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in pass) if (inherited.TryGetValue(key, out var value) && value.Length > 0) result[key] = value;
        foreach (var pair in inherited.Where(pair => pair.Key.StartsWith("MCP_TEST_", StringComparison.OrdinalIgnoreCase))) result[pair.Key] = pair.Value;
        WindowsTunnelProxy.ApplyFallback(result, WindowsTunnelProxy.Discover);
        var noProxy = (inherited.GetValueOrDefault("NO_PROXY") ?? inherited.GetValueOrDefault("no_proxy") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var loopback in new[] { "127.0.0.1", "localhost", "::1" }) if (!noProxy.Contains(loopback, StringComparer.OrdinalIgnoreCase)) noProxy.Add(loopback);
        result["NO_PROXY"] = result["no_proxy"] = string.Join(',', noProxy); result["CONTROL_PLANE_API_KEY"] = apiKey; result["FILEMCP_LOCAL_AUTH_TOKEN"] = localAuthToken;
        return result;
    }

    private string TunnelProfileDirectory()
    {
        var directory = _profileDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatToComputerMCP", "tunnel-profiles");
        Directory.CreateDirectory(directory); return directory;
    }

    private static string TunnelClientPath()
    {
        var sibling = Path.Combine(AppContext.BaseDirectory, "tunnel-client.exe"); if (File.Exists(sibling)) return sibling;
        var overridePath = Environment.GetEnvironmentVariable("MCP_TUNNEL_CLIENT"); if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        throw new FileMcpException("tunnel-client.exe was not found beside ChatToComputerMCP.exe.");
    }

    private static string MakeLocalAuthToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private void RequireSuccess(ProcessResult result, string operation, IReadOnlyList<string> sensitive)
    {
        if (result.TimedOut) throw new FileMcpException(operation + " timed out.");
        if (result.ExitCode != 0) throw new FileMcpException(operation + " failed: " + Redact(string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr, sensitive));
        if (result.Stdout.Length > 0) EmitLog(Redact(result.Stdout, sensitive) + (result.Stdout.EndsWith('\n') ? "" : "\n"));
        if (result.Stderr.Length > 0) EmitLog(Redact(result.Stderr, sensitive) + (result.Stderr.EndsWith('\n') ? "" : "\n"));
    }

    private static string Redact(string text, IReadOnlyList<string> sensitive) { foreach (var secret in sensitive.Where(s => s.Length > 0)) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal); return text; }
    private void EmitLog(string text) => Log?.Invoke(text);
    private void SetState(LocalMcpRuntimeState state)
    {
        lock (_stateGate)
        {
            _state = state;
            if (state.Status == LocalMcpRuntimeStatus.Running ||
                (state.Status == LocalMcpRuntimeStatus.Starting && _transportStarted)) _sleepGuard.Acquire(); else _sleepGuard.Release();
        }
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        // Window teardown, test harnesses and tunnel exit callbacks can
        // request disposal concurrently. Never Cancel() a disposed CTS.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await ShutdownAsync().ConfigureAwait(false); }
        finally
        {
            _startupCts?.Dispose();
            _startupCts = null;
            _sleepGuard.Dispose();
            _serial.Dispose();
        }
    }
}
