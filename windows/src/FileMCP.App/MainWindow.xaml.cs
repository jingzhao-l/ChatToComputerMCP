using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FileMCP.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FileMCP.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly LocalMcpRuntime _runtime = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private FileMcpSettings _settings;
    private readonly List<ApprovedWorkspace> _workspaces = [];
    private sealed record TaskRow(DesktopCommandInfo Task)
    {
        public string Display => Task.WorkspaceName + " · " + Task.RequestId + " · " + Task.State;
    }
    private bool _quitting;
    private string _logBuffer = "";
    private readonly DispatcherTimer _updateTimer = new();
    private bool _updateBusy;
    private string? _availableUpdateVersion;
    private const int MaxLogCharacters = 500_000;

    public MainWindow()
    {
        InitializeComponent();
        _settings = LoadSettingsSafely();
        ApplySettings(_settings);
        UpdateApiKeyStatus();
        LoadUpdateSettings();
        _updateTimer.Interval = TimeSpan.FromHours(12);
        _updateTimer.Tick += (_, _) => _ = CheckUpdatesAsync(automatic: true);
        Loaded += (_, _) =>
        {
            ConfigureUpdateTimer();
            if (AutoCheckUpdatesBox.IsChecked == true)
                _ = CheckUpdatesAsync(automatic: true);
        };

        _runtime.Log += text => Dispatcher.BeginInvoke(new Action(() => AppendLog(text)));
        _runtime.StateChanged += state => Dispatcher.BeginInvoke(new Action(() => UpdateRuntimeState(state)));
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ChatToComputerMCP", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("About ChatToComputerMCP", null, (_, _) => Dispatcher.Invoke(ShowAbout));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit ChatToComputerMCP", null, (_, _) => Dispatcher.Invoke(async () => await QuitAsync()));
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "ChatToComputerMCP",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadApplicationIcon(),
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        UpdateRuntimeState(_runtime.State);
    }

    private FileMcpSettings LoadSettingsSafely()
    {
        try { return _settingsStore.Load(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "ChatToComputerMCP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new FileMcpSettings();
        }
    }

    private void ApplySettings(FileMcpSettings settings)
    {
        TunnelIdBox.Text = settings.TunnelId;
        ProfileBox.Text = settings.Profile;
        PortBox.Text = settings.Port.ToString();
        DirectoryBox.Text = settings.AllowedDirectory;
        HealthAddressBox.Text = settings.HealthAddress;
        GitNameBox.Text = settings.GitUserName;
        GitEmailBox.Text = settings.GitUserEmail;
        EnableCommandsCheckBox.IsChecked = settings.EnableCommands;
        _workspaces.Clear();
        _workspaces.AddRange(settings.AdditionalWorkspaces ?? []);
        RefreshWorkspaceList();
        var capacity = settings.MaxConcurrentCommands == 0 ? 8 : Math.Clamp(settings.MaxConcurrentCommands, 4, 16);
        foreach (var item in CommandCapacityBox.Items.OfType<ComboBoxItem>())
            if (int.TryParse(item.Content?.ToString(), out var count) && count == capacity)
                CommandCapacityBox.SelectedItem = item;
    }

    private int SelectedCommandCapacity() =>
        int.TryParse((CommandCapacityBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var count)
            ? Math.Clamp(count, 4, 16) : 8;

    private void RefreshWorkspaceList()
    {
        WorkspaceListBox.ItemsSource = null;
        WorkspaceListBox.ItemsSource = _workspaces.ToArray();
        WorkspaceNameBox.Text = "";
    }

    private void WorkspaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        WorkspaceNameBox.Text = (WorkspaceListBox.SelectedItem as ApprovedWorkspace)?.Name ?? "";
    }

    private void AddWorkspaces_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog {
            Title = "Choose additional approved workspaces",
            Multiselect = true,
            InitialDirectory = Directory.Exists(DirectoryBox.Text) ? DirectoryBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (picker.ShowDialog(this) != true) return;
        var before = _workspaces.ToList();
        foreach (var folder in picker.FolderNames)
        {
            _workspaces.Add(new ApprovedWorkspace(Guid.NewGuid().ToString("N"),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), folder));
        }
        if (!ValidateApprovedFolders())
        {
            _workspaces.Clear();
            _workspaces.AddRange(before);
            return;
        }
        RefreshWorkspaceList();
        AppendLog("Additional workspace approval added. Save and reconnect to apply.\n");
    }

    private void RemoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not ApprovedWorkspace workspace) return;
        if (System.Windows.MessageBox.Show(this,
            "Remove approval of " + workspace.Name + "? This never deletes files.",
            "Revoke workspace access", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _workspaces.Remove(workspace);
        RefreshWorkspaceList();
        AppendLog("Workspace approval removed. Save and reconnect to apply.\n");
    }

    private void RenameWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not ApprovedWorkspace workspace) return;
        var name = WorkspaceNameBox.Text.Trim();
        if (name.Length is < 1 or > 64) { ShowError("Workspace name must be 1–64 characters."); return; }
        var index = _workspaces.IndexOf(workspace);
        _workspaces[index] = workspace with { Name = name };
        RefreshWorkspaceList();
    }

    private void RevealWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceListBox.SelectedItem is not ApprovedWorkspace workspace) return;
        if (!Directory.Exists(workspace.Path)) { ShowError("The selected workspace folder is missing."); return; }
        Process.Start(new ProcessStartInfo {
            FileName = "explorer.exe", Arguments = "/select,\"" + workspace.Path + "\"",
            UseShellExecute = true
        });
    }

    private bool ValidateApprovedFolders()
    {
        if (_workspaces.Count > 12) { ShowError("At most 12 additional workspaces can be approved."); return false; }
        var roots = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal) { "default" };
        foreach (var workspace in new[] {
            new ApprovedWorkspace("default", "Default", DirectoryBox.Text.Trim())
        }.Concat(_workspaces))
        {
            if (string.IsNullOrWhiteSpace(workspace.Name) || workspace.Name.Length > 64 ||
                string.IsNullOrWhiteSpace(workspace.Path) ||
                !Directory.Exists(workspace.Path) ||
                !ids.Add(workspace.Id) && workspace.Id != "default")
            {
                ShowError("Every approved workspace must have a unique ID, name and existing folder.");
                return false;
            }
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path));
            if (roots.Any(root =>
                string.Equals(root, folder, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                ShowError("Approved workspace roots must not overlap.");
                return false;
            }
            roots.Add(folder);
        }
        // The server independently checks canonical paths including junctions.
        return true;
    }

    private void RefreshTasks_Click(object sender, RoutedEventArgs e) => RefreshTasks();

    private void RefreshTasks()
    {
        try
        {
            var tasks = _runtime.DesktopCommandSnapshots();
            var selectedID = (TaskListBox.SelectedItem as TaskRow)?.Task.SessionId;
            var entries = tasks.Select(task => new TaskRow(task)).ToArray();
            TaskListBox.ItemsSource = entries;
            TaskListBox.SelectedItem = entries.FirstOrDefault(entry =>
                entry.Task.SessionId == selectedID);
            TasksSummaryText.Text = tasks.Count(task => task.Active) + " active long-running commands. " +
                "Synchronous commands count toward total capacity but are not individually cancellable here.";
            TaskDetailText.Text = tasks.Count == 0 ? "No recent command sessions." : "Select a session to inspect its workspace.";
            CancelTaskButton.IsEnabled = false;
            RevealTaskButton.IsEnabled = TaskListBox.SelectedItem is TaskRow;
        }
        catch (Exception ex) { TasksSummaryText.Text = ex.Message; }
    }

    private async void TaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskListBox.SelectedItem is not TaskRow row) {
            CancelTaskButton.IsEnabled = false;
            RevealTaskButton.IsEnabled = false;
            CopyTaskOutputButton.IsEnabled = false;
            TaskOutputBox.Text = "";
            return;
        }
        TaskDetailText.Text = row.Task.WorkspaceName + " · " + row.Task.Cwd + " · " + row.Task.State;
        CancelTaskButton.IsEnabled = row.Task.Active &&
            _runtime.State.Status == LocalMcpRuntimeStatus.Running;
        RevealTaskButton.IsEnabled = Directory.Exists(row.Task.Cwd);
        CopyTaskOutputButton.IsEnabled = false;
        TaskOutputBox.Text = "Loading bounded command output…";
        try
        {
            var result = await _runtime.DesktopCommandOutputAsync(
                row.Task.WorkspaceId, row.Task.SessionId);
            // An async read may finish after the user selected another task.
            if ((TaskListBox.SelectedItem as TaskRow)?.Task.SessionId != row.Task.SessionId)
                return;
            var text = result["output"]?.GetValue<string>() ?? "";
            var truncated = result["truncated"]?.GetValue<bool>() ?? false;
            var code = result["exit_code"] is null ? "pending" :
                result["exit_code"]!.GetValue<int>().ToString();
            TaskDetailText.Text = row.Task.WorkspaceName + " · " + row.Task.Cwd +
                " · " + (result["state"]?.GetValue<string>() ?? row.Task.State) +
                " · exit " + code;
            TaskOutputBox.Text = (truncated ? "[Earlier output truncated]\n" : "") +
                (text.Length == 0 ? "(No captured output)" : text);
            CopyTaskOutputButton.IsEnabled = text.Length > 0;
        }
        catch (Exception ex)
        {
            if ((TaskListBox.SelectedItem as TaskRow)?.Task.SessionId == row.Task.SessionId)
                TaskOutputBox.Text = "Could not read output: " + ex.Message;
        }
    }

    private void RevealTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskListBox.SelectedItem is not TaskRow row ||
            !Directory.Exists(row.Task.Cwd)) return;
        Process.Start(new ProcessStartInfo {
            FileName = "explorer.exe", Arguments = "/select,\"" + row.Task.Cwd + "\"",
            UseShellExecute = true
        });
    }

    private void CopyTaskOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyTaskOutputButton.IsEnabled || string.IsNullOrEmpty(TaskOutputBox.Text))
            return;
        System.Windows.Clipboard.SetText(TaskOutputBox.Text);
        AppendLog("Task output copied. Inspect it for credentials before sharing.\n");
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskListBox.SelectedItem is not TaskRow row || !row.Task.Active) return;
        if (System.Windows.MessageBox.Show(this,
            "Stop only " + row.Task.RequestId + " in " + row.Task.WorkspaceName + "?",
            "Cancel command", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try { _runtime.CancelDesktopCommand(row.Task.WorkspaceId, row.Task.SessionId); }
        catch (Exception ex) { ShowError(ex.Message); }
        RefreshTasks();
    }

    private void LoadUpdateSettings()
    {
        try
        {
            var policy = DesktopUpdates.ReadPreferences();
            AutoCheckUpdatesBox.IsChecked = policy.CheckAutomatically;
            AutoInstallUpdatesBox.IsChecked = policy.InstallAutomatically;
            AutoInstallUpdatesBox.IsEnabled = policy.CheckAutomatically;
        }
        catch (Exception ex)
        {
            AutoCheckUpdatesBox.IsChecked = false;
            AutoInstallUpdatesBox.IsChecked = false;
            AutoInstallUpdatesBox.IsEnabled = false;
            UpdateStatusText.Text = "Could not safely load update policy: " + ex.Message;
        }
    }

    private void ConfigureUpdateTimer()
    {
        _updateTimer.Stop();
        if (AutoCheckUpdatesBox.IsChecked == true) _updateTimer.Start();
    }

    private void UpdateOptions_Click(object sender, RoutedEventArgs e)
    {
        if (AutoCheckUpdatesBox.IsChecked != true)
            AutoInstallUpdatesBox.IsChecked = false;
        AutoInstallUpdatesBox.IsEnabled = AutoCheckUpdatesBox.IsChecked == true;
        try
        {
            DesktopUpdates.SavePreferences(new DesktopUpdates.Preferences(
                1, AutoCheckUpdatesBox.IsChecked == true,
                AutoInstallUpdatesBox.IsChecked == true));
            ConfigureUpdateTimer();
            UpdateStatusText.Text = AutoCheckUpdatesBox.IsChecked == true
                ? "Automatic checks enabled while this app is running. " +
                  (AutoInstallUpdatesBox.IsChecked == true
                      ? "New versions install without restarting."
                      : "New versions require manual installation.")
                : "Automatic checks and installs disabled.";
            if (AutoCheckUpdatesBox.IsChecked == true) _ = CheckUpdatesAsync(automatic: true);
        }
        catch (Exception ex)
        {
            AutoCheckUpdatesBox.IsChecked = false;
            AutoInstallUpdatesBox.IsChecked = false;
            AutoInstallUpdatesBox.IsEnabled = false;
            _updateTimer.Stop();
            UpdateStatusText.Text = "Could not save update preferences: " + ex.Message;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdatesAsync(automatic: false);

    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        _availableUpdateVersion = null;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking official npm Registry…";
        try
        {
            var latest = await DesktopUpdates.CheckLatestAsync();
            _availableUpdateVersion = latest;
            if (!DesktopUpdates.IsNewer(latest, DesktopUpdates.CurrentVersion))
            {
                UpdateStatusText.Text = "Up to date (running v" +
                    DesktopUpdates.CurrentVersion + ", npm latest v" + latest + ").";
                return;
            }
            if (DesktopUpdates.Installed(latest))
            {
                UpdateStatusText.Text = "v" + latest +
                    " is already installed. Quit and reopen ChatToComputerMCP to use it.";
                return;
            }
            UpdateStatusText.Text = "Update available: v" + DesktopUpdates.CurrentVersion +
                " → v" + latest + ". Local .NET compilation required.";
            InstallUpdateButton.IsEnabled = true;
        }
        catch (Exception ex) { UpdateStatusText.Text = "Update check failed: " + ex.Message; }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = true;
        }
        if (automatic && AutoInstallUpdatesBox.IsChecked == true &&
            InstallUpdateButton.IsEnabled)
            await InstallUpdateAsync();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) =>
        await InstallUpdateAsync();

    private async Task InstallUpdateAsync()
    {
        if (_updateBusy || _availableUpdateVersion is null ||
            !DesktopUpdates.IsNewer(_availableUpdateVersion,
                DesktopUpdates.CurrentVersion))
            return;
        var target = _availableUpdateVersion;
        _updateBusy = true;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Compiling v" + target +
            " into a separate version directory. Existing AI tasks remain running…";
        try
        {
            await DesktopUpdates.InstallAsync(target);
            UpdateStatusText.Text = "v" + target +
                " installed. Quit and reopen the app to use it; active tasks and tunnel were not restarted.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update failed: " + ex.Message;
            InstallUpdateButton.IsEnabled = true;
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void UpdateApiKeyStatus()
    {
        var saved = _credentialStore.HasSavedApiKey;
        ApiKeyStatusText.Text = saved ? "API key is saved in Windows Credential Manager" : "No API key is saved";
        DeleteApiKeyButton.IsEnabled = saved;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var status = _runtime.State.Status;
        if (status is LocalMcpRuntimeStatus.Running or LocalMcpRuntimeStatus.Starting)
        {
            await _runtime.StopAsync();
            return;
        }
        if (status == LocalMcpRuntimeStatus.Stopping) return;
        if (!ValidateConnection(requireApiKey: true) || !ValidateSettings()) return;

        try
        {
            var typedKey = ApiKeyBox.Password.Trim();
            if (typedKey.Length > 0)
            {
                _credentialStore.SaveApiKey(typedKey);
                ApiKeyBox.Password = "";
                UpdateApiKeyStatus();
            }
            var apiKey = _credentialStore.ReadApiKey();
            SaveAllSettings();
            var configuration = new LocalMcpConfiguration(
                TunnelIdBox.Text.Trim(), apiKey, ProfileBox.Text.Trim(), checked((ushort)_settings.Port),
                DirectoryBox.Text.Trim(), HealthAddressBox.Text.Trim(), GitNameBox.Text.Trim(), GitEmailBox.Text.Trim(),
                EnableCommandsCheckBox.IsChecked == true,
                _workspaces.ToArray(), SelectedCommandCapacity());
            await _runtime.StartAsync(configuration);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateConnection(requireApiKey: true)) return;
        try
        {
            var typedKey = ApiKeyBox.Password.Trim();
            if (typedKey.Length > 0)
            {
                _credentialStore.SaveApiKey(typedKey);
                ApiKeyBox.Password = "";
            }
            _settings.TunnelId = TunnelIdBox.Text.Trim();
            _settingsStore.Save(_settings);
            UpdateApiKeyStatus();
            AppendLog("Connection settings saved.\n");
            if (_runtime.State.Status != LocalMcpRuntimeStatus.Stopped) AppendLog("Changes will take effect the next time the tunnel starts.\n");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSettings()) return;
        try
        {
            SaveSettingsFields();
            _settingsStore.Save(_settings);
            AppendLog("Settings saved.\n");
            if (_runtime.State.Status != LocalMcpRuntimeStatus.Stopped) AppendLog("Changes will take effect the next time the tunnel starts.\n");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void DeleteApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _credentialStore.DeleteApiKey();
            ApiKeyBox.Password = "";
            UpdateApiKeyStatus();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose shared directory",
            Multiselect = false,
            InitialDirectory = Directory.Exists(DirectoryBox.Text) ? DirectoryBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) == true) DirectoryBox.Text = dialog.FolderName;
    }

    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();

    private bool ValidateConnection(bool requireApiKey)
    {
        var tunnelId = TunnelIdBox.Text.Trim();
        if (tunnelId.Length == 0) { MainTabs.SelectedItem = ConnectionTab; ShowError("Enter a Tunnel ID in the Connection tab."); return false; }
        if (!LocalMcpRuntime.IsValidTunnelId(tunnelId)) { MainTabs.SelectedItem = ConnectionTab; ShowError("Tunnel ID must match tunnel_<32 lowercase letters or digits>."); return false; }
        if (requireApiKey && ApiKeyBox.Password.Trim().Length == 0 && !_credentialStore.HasSavedApiKey) { MainTabs.SelectedItem = ConnectionTab; ShowError("Enter a Runtime API key in the Connection tab."); return false; }
        return true;
    }

    private bool ValidateSettings()
    {
        if (DirectoryBox.Text.Trim().Length == 0) { MainTabs.SelectedItem = SettingsTab; ShowError("Choose a shared directory."); return false; }
        if (!ValidateApprovedFolders()) { MainTabs.SelectedItem = SettingsTab; return false; }
        if (!LocalMcpRuntime.IsValidProfileName(ProfileBox.Text.Trim())) { MainTabs.SelectedItem = SettingsTab; AdvancedExpander.IsExpanded = true; ShowError("Profile must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters)."); return false; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) { MainTabs.SelectedItem = SettingsTab; AdvancedExpander.IsExpanded = true; ShowError("MCP port must be between 1 and 65535."); return false; }
        if (LocalMcpRuntime.NormalizeHealthAddress(HealthAddressBox.Text.Trim()) is null) { MainTabs.SelectedItem = SettingsTab; AdvancedExpander.IsExpanded = true; ShowError("Health listener must use localhost, 127.0.0.1, or [::1] with a port from 0 to 65535."); return false; }
        return true;
    }

    private void SaveAllSettings()
    {
        _settings.TunnelId = TunnelIdBox.Text.Trim();
        SaveSettingsFields();
        _settingsStore.Save(_settings);
    }

    private void SaveSettingsFields()
    {
        _settings.Profile = ProfileBox.Text.Trim();
        _settings.Port = int.Parse(PortBox.Text.Trim());
        _settings.AllowedDirectory = DirectoryBox.Text.Trim();
        _settings.HealthAddress = HealthAddressBox.Text.Trim();
        _settings.GitUserName = GitNameBox.Text.Trim();
        _settings.GitUserEmail = GitEmailBox.Text.Trim();
        _settings.EnableCommands = EnableCommandsCheckBox.IsChecked == true;
        _settings.AdditionalWorkspaces = _workspaces.ToList();
        _settings.MaxConcurrentCommands = SelectedCommandCapacity();
    }

    private void UpdateRuntimeState(LocalMcpRuntimeState state)
    {
        switch (state.Status)
        {
            case LocalMcpRuntimeStatus.Stopped:
            case LocalMcpRuntimeStatus.Failed:
                ConnectButton.Content = "Connect"; ConnectButton.IsEnabled = true; break;
            case LocalMcpRuntimeStatus.Starting:
                ConnectButton.Content = "Connecting…"; ConnectButton.IsEnabled = false; break;
            case LocalMcpRuntimeStatus.Running:
                ConnectButton.Content = "Disconnect"; ConnectButton.IsEnabled = true; break;
            case LocalMcpRuntimeStatus.Stopping:
                ConnectButton.Content = "Disconnecting…"; ConnectButton.IsEnabled = false; break;
        }
        var editable = state.Status is LocalMcpRuntimeStatus.Stopped or LocalMcpRuntimeStatus.Failed;
        WorkspaceListBox.IsEnabled = editable;
        WorkspaceNameBox.IsEnabled = editable;
        CommandCapacityBox.IsEnabled = editable;
        AddWorkspaceButton.IsEnabled = editable;
        RenameWorkspaceButton.IsEnabled = editable;
        RemoveWorkspaceButton.IsEnabled = editable;
        RevealWorkspaceButton.IsEnabled = editable;
        if (state.Status == LocalMcpRuntimeStatus.Running) RefreshTasks();
        if (state.Status == LocalMcpRuntimeStatus.Failed && !string.IsNullOrEmpty(state.Error)) ShowError(state.Error);
    }

    private void AppendLog(string text)
    {
        _logBuffer += text;
        if (_logBuffer.Length > MaxLogCharacters)
            _logBuffer = "[...older log truncated...]\n" + _logBuffer[^MaxLogCharacters..];
        LogBox.Text = _logBuffer;
        LogBox.ScrollToEnd();
    }

    private void ShowError(string message) => System.Windows.MessageBox.Show(this, message, "ChatToComputerMCP", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ShowAbout()
    {
        System.Windows.MessageBox.Show(
            this,
            "ChatToComputerMCP 0.3.0\n\nNative Windows MCP bridge for controlled local file, Git, and optional command access.",
            "About ChatToComputerMCP",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    public void ShutdownForSystemSession()
    {
        if (_quitting) return;
        _quitting = true;
        try { _runtime.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        try { await _runtime.ShutdownAsync(); }
        finally
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            await _runtime.DisposeAsync();
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.W) { Hide(); e.Handled = true; }
        else if (e.Key == Key.OemComma) { MainTabs.SelectedItem = SettingsTab; ShowFromTray(); e.Handled = true; }
        else if (e.Key == Key.Q) { _ = QuitAsync(); e.Handled = true; }
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var icon = Environment.ProcessPath is { Length: > 0 } path ? System.Drawing.Icon.ExtractAssociatedIcon(path) : null;
        return icon ?? SystemIcons.Application;
    }
}
