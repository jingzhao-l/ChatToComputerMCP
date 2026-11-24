using System.Windows;
using System.Windows.Controls;
using FileMCP.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace FileMCP.App;

internal sealed class WorkspacePermissionWindow : Window
{
    private readonly CheckBox _inherit = new() { Content = "Inherit global settings" };
    private readonly CheckBox _write = new() { Content = "Allow file and local Git changes" };
    private readonly CheckBox _shell = new() { Content = "Allow shell (if globally enabled)" };
    private readonly CheckBox _push = new() { Content = "Allow Git push (if globally enabled)" };
    private readonly CheckBox _codex = new() { Content = "Allow Codex import (if globally enabled)" };
    public WorkspacePermissions? Permissions { get; private set; }

    public WorkspacePermissionWindow(string name, WorkspacePermissions? current)
    {
        Title = "Workspace permissions — " + name;
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "Workspace permissions", FontSize = 22, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock {
            Text = "Global privileged switches remain upper bounds. Shell uses your OS user privileges, not a filesystem sandbox. Read-only disables all writes and privileged tools.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12)
        });
        _inherit.IsChecked = current is null;
        _write.IsChecked = current?.AllowWrite ?? true;
        _shell.IsChecked = current?.AllowShell ?? true;
        _push.IsChecked = current?.AllowGitPush ?? true;
        _codex.IsChecked = current?.AllowCodexHistory ?? true;
        foreach (var option in new[] { _inherit, _write, _shell, _push, _codex })
        {
            option.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(option);
            option.Checked += (_, _) => UpdateControls();
            option.Unchecked += (_, _) => UpdateControls();
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var apply = new Button { Content = "Apply", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) => {
            Permissions = _inherit.IsChecked == true ? null : _write.IsChecked != true ? WorkspacePermissions.ReadOnly :
                new WorkspacePermissions(true, _shell.IsChecked == true, _push.IsChecked == true, _codex.IsChecked == true);
            DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); stack.Children.Add(actions);
        Content = stack;
        UpdateControls();
    }

    private void UpdateControls()
    {
        _write.IsEnabled = _inherit.IsChecked != true;
        _shell.IsEnabled = _push.IsEnabled = _codex.IsEnabled = _write.IsEnabled && _write.IsChecked == true;
    }
}
