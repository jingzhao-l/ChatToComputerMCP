using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FileMCP.App;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private Mutex? _uiMutex;
    private bool _ownsUiMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            Shutdown(1);
            return;
        }
        if (e.Args.SequenceEqual(new[] {"--cua-guest-server"},StringComparer.Ordinal))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var guestCancellation=new CancellationTokenSource();
            Exit+=(_,_)=>guestCancellation.Cancel();
            SessionEnding+=(_,_)=>guestCancellation.Cancel();
            _=Task.Run(async()=>{
                int code=0;
                try{await FileMCP.Core.CuaGuestHost.RunAsync(guestCancellation.Token);}
                catch(OperationCanceledException)when(guestCancellation.IsCancellationRequested){}
                catch{code=1;}
                try{await Dispatcher.InvokeAsync(()=>Shutdown(code));}catch{}
            });
            return;
        }
        if (e.Args.Contains("--selftest-startup", StringComparer.Ordinal))
        {
            try { WindowsStartup.SelfTestIsolatedPolicy(); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--sync-startup", StringComparer.Ordinal))
        {
            try { WindowsStartup.RefreshInstalledExecutable(executable); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--manage-startup", StringComparer.Ordinal))
        {
            try
            {
                var actionIndex = Array.IndexOf(e.Args, "--manage-startup");
                var action = e.Args.ElementAtOrDefault(actionIndex + 1);
                if (action == "enable")
                {
                    WindowsStartup.Configure(new WindowsStartup.Policy(
                        1, true, e.Args.Contains("--auto-connect", StringComparer.Ordinal),
                        e.Args.Contains("--always", StringComparer.Ordinal)), executable);
                }
                else if (action == "disable")
                {
                    WindowsStartup.Configure(new WindowsStartup.Policy(
                        1, false, false, false), executable);
                }
                else throw new IOException("Expected enable or disable");
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--supervise", StringComparer.Ordinal))
        {
            _ = Task.Run(async () =>
            {
                try { await WindowsStartup.RunSupervisorAsync(executable); }
                finally
                {
                    try { _ = Dispatcher.BeginInvoke(new Action(() => Shutdown(0))); }
                    catch { /* session is already terminating */ }
                }
            });
            return;
        }

        _uiMutex = new Mutex(false, @"Local\ChatToComputerMCP-NativeApp");
        try { _ownsUiMutex = _uiMutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsUiMutex = true; }
        if (!_ownsUiMutex)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(
                    @"Local\ChatToComputerMCP-ShowExistingWindow");
                signal.Set();
            }
            catch { /* Existing app may be shutting down; never launch duplicate. */ }
            _uiMutex.Dispose();
            _uiMutex = null;
            Shutdown(0);
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\ChatToComputerMCP-ShowExistingWindow");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => _window?.ShowFromTray())),
            null, Timeout.Infinite, executeOnlyOnce: false);

        _window = new MainWindow();
        MainWindow = _window;
        SessionEnding += (_, _) => _window.ShutdownForSystemSession();
        if (e.Args.Contains("--login-start", StringComparer.Ordinal))
        {
            _window.ShowActivated = false;
            _window.ShowInTaskbar = false;
            _window.WindowState = WindowState.Minimized;
            _window.Show();
            _window.Hide();
            // No credentials are requested interactively. The app uses its
            // existing OS credential store only if the user opted in.
            try
            {
                if (WindowsStartup.Read().AutoConnect)
                    _window.ConnectSavedAtLogin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    "Windows login auto-connect settings unavailable: " + ex.GetType().Name);
            }
        }
        else
        {
            _window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        if (_uiMutex is not null)
        {
            if (_ownsUiMutex)
            {
                try { _uiMutex.ReleaseMutex(); } catch { }
            }
            _uiMutex.Dispose();
        }
        base.OnExit(e);
    }
}
