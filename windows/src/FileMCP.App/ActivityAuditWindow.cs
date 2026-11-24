using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FileMCP.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace FileMCP.App;

internal sealed class ActivityAuditWindow : Window
{
    private readonly string _profile;
    private readonly TextBox _search = new() { MinWidth = 280, MinHeight = 30 };
    private readonly TextBlock _status = new();
    private readonly DataGrid _table = new() { AutoGenerateColumns = false, IsReadOnly = true };
    private readonly Button _export = new() { Content = "Export filtered…", IsEnabled = false };
    private IReadOnlyList<ActivityAuditEvent> _rows = [];
    private ActivityAuditEvent[] _visible = [];
    private int _generation;

    public ActivityAuditWindow(string profile)
    {
        _profile = profile;
        Title = "Activity audit — ChatToComputerMCP"; Width = 980; Height = 580; MinWidth = 680; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Activity audit", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = $"Local profile: {profile}. Search tools, workspaces, verified clients and outcomes. No arguments, command contents or response text are recorded. Two bounded segments are retained.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(12,5,12,5), Margin = new Thickness(8,0,8,0) };
        _export.Padding = new Thickness(12,5,12,5);
        _search.ToolTip = "Search tool, workspace, client or outcome";
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search activity audit");
        actions.Children.Add(_search); actions.Children.Add(refresh); actions.Children.Add(_export);
        header.Children.Add(actions); _status.Margin = new Thickness(0,8,0,8); header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        foreach (var (name, path, width) in new[] { ("Time","Time",165),("Tool","Tool",175),("Workspace","Workspace",110),("Verified client","Client",180),("Outcome","Result",80),("ms","DurationMs",65),("Bytes","ResponseBytes",70) })
            _table.Columns.Add(new DataGridTextColumn { Header = name, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width) });
        dock.Children.Add(_table); Content = dock;
        _search.TextChanged += (_, _) => Filter();
        refresh.Click += async (_, _) => await ReloadAsync();
        _export.Click += (_, _) => Export();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        _status.Text = "Reading local audit…";
        try {
            var records = await Task.Run(() => ActivityAuditStore.Read(ActivityAuditStore.DirectoryFor(_profile)));
            if (generation == _generation) { _rows = records; Filter(); }
        } catch (Exception error) { if (generation == _generation) _status.Text = "Audit unavailable: " + error.Message; }
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        _visible = _rows.Where(item => q.Length == 0 ||
            new[] { item.Tool, item.Workspace ?? "", item.Client ?? "shared", item.Result }
                .Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();
        _table.ItemsSource = _visible; _export.IsEnabled = _visible.Length > 0;
        _status.Text = $"{_visible.Length} of {_rows.Count} events";
    }

    private void Export()
    {
        if (_visible.Length == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export filtered local audit", FileName = "activity-audit.json", Filter = "JSON files (*.json)|*.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_visible, new JsonSerializerOptions { WriteIndented = true }));
            _status.Text = $"Exported {_visible.Length} events.";
        } catch (Exception error) { _status.Text = "Export failed: " + error.Message; }
    }
}
