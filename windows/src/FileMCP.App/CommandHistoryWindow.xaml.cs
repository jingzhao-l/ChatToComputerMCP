using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FileMCP.Core;

namespace FileMCP.App;

public partial class CommandHistoryWindow : Window
{
    private readonly string _profile;
    private IReadOnlyList<CommandHistoryRecord> _records = [];
    private int _generation;

    public CommandHistoryWindow(string profile)
    {
        _profile = profile;
        InitializeComponent();
        Explanation.Text = $"Local profile: {profile}. Up to {CommandHistoryStore.MaxCompleted} completed records, retained " +
            $"{CommandHistoryStore.RetentionDays} days. Command text and output are saved only when explicitly enabled. " +
            "A recorded running state is not proof of a live process.";
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        Status.Text = "Reading local history…";
        try
        {
            var rows = await Task.Run(() => CommandHistoryStore.Read(CommandHistoryStore.DirectoryFor(_profile)));
            if (generation != _generation) return;
            _records = rows;
            ApplyFilter();
        }
        catch (Exception ex) { if (generation == _generation) Status.Text = "History unavailable: " + ex.Message; }
    }

    private void ApplyFilter()
    {
        if (HistoryGrid is null || Status is null) return;
        var old = (HistoryGrid.SelectedItem as CommandHistoryRecord)?.Id;
        var query = SearchBox.Text.Trim();
        var filtered = _records.Where(record => query.Length == 0 ||
            new[] { record.WorkspaceName, record.WorkspaceId, record.Cwd, record.Kind, record.State,
                record.RequestId ?? "", record.Command ?? "" }.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        HistoryGrid.ItemsSource = filtered;
        HistoryGrid.SelectedItem = filtered.FirstOrDefault(record => record.Id == old) ?? filtered.FirstOrDefault();
        Status.Text = $"{filtered.Length} of {_records.Count} records";
        ShowSelection();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelection();

    private void ShowSelection()
    {
        if (Details is null || ExportButton is null) return;
        var item = HistoryGrid.SelectedItem as CommandHistoryRecord;
        ExportButton.IsEnabled = item is not null;
        if (item is null) { Details.Text = "No matching command records."; return; }
        var elapsed = Math.Max(0, (item.UpdatedAt - item.StartedAt).TotalSeconds);
        Details.Text = $"Workspace: {item.WorkspaceName} ({item.WorkspaceId})\nDirectory: {item.Cwd}\n" +
            $"Type: {item.Kind}\nRequest: {item.RequestId ?? "Synchronous call"}\n" +
            $"Client: {item.ClientId ?? "Shared/local connection — no verified individual client"}\n" +
            $"Started: {item.StartedAt.ToLocalTime():G}\nLast updated: {item.UpdatedAt.ToLocalTime():G}\n" +
            $"Recorded duration: {elapsed:F2}s\nState: {item.State}\nExit: {item.ExitCode?.ToString() ?? "Unconfirmed"}\n" +
            (item.Truncated ? "Some captured content was truncated.\n" : "") +
            $"\nCOMMAND\n{item.Command ?? "[Content capture was not enabled]"}\n\nOUTPUT\n{item.Output ?? "[No captured output]"}";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not CommandHistoryRecord item) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export local command record — may contain private content",
            FileName = $"command-{item.Id:D}.json", Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }));
            Status.Text = "Selected record exported. Review before sharing.";
        }
        catch (Exception ex) { Status.Text = "Export failed: " + ex.Message; }
    }
}
