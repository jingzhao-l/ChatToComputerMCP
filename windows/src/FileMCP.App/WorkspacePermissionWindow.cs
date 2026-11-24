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
    private readonly CheckBox _isolated = new() { Content = "Use isolated Linux Shell (Docker Desktop)" };
    private readonly System.Windows.Controls.TextBox _image = new();
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
            Text = "Global switches remain upper bounds. Host Shell uses your OS privileges. Isolated Linux Shell requires Docker Desktop and a trusted local image, disables host Git push and Codex import, and never falls back.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12)
        });
        _inherit.IsChecked = current is null;
        _write.IsChecked = current?.AllowWrite ?? true;
        _shell.IsChecked = current?.AllowShell ?? true;
        _push.IsChecked = current?.AllowGitPush ?? true;
        _codex.IsChecked = current?.AllowCodexHistory ?? true;
        _isolated.IsChecked = current?.IsolatedShellImage is not null;
        _image.Text = current?.IsolatedShellImage ?? "";
        _image.ToolTip = "sha256: followed by 64 lowercase hexadecimal characters";
        _image.Margin = new Thickness(0, 0, 0, 8);
        foreach (var option in new[] { _inherit, _write, _shell, _isolated, _push, _codex })
        {
            option.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(option);
            if (option == _isolated)
            {
                stack.Children.Add(_image);
                stack.Children.Add(new TextBlock { Text = "Trusted local image with /bin/sh and GNU timeout. No automatic download.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            }
            option.Checked += (_, _) => UpdateControls();
            option.Unchecked += (_, _) => UpdateControls();
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var apply = new Button { Content = "Apply", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) => {
            var useIsolation = _isolated.IsEnabled && _isolated.IsChecked == true;
            var image = _image.Text.Trim();
            if (useIsolation && !System.Text.RegularExpressions.Regex.IsMatch(image, @"\Asha256:[a-f0-9]{64}\z"))
            {
                System.Windows.MessageBox.Show(this, "Enter the full immutable local image ID (sha256: plus 64 lowercase hex characters).", "Invalid image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Permissions = _inherit.IsChecked == true ? null : _write.IsChecked != true ? WorkspacePermissions.ReadOnly :
                new WorkspacePermissions(true, _shell.IsChecked == true, !useIsolation && _push.IsChecked == true,
                    !useIsolation && _codex.IsChecked == true, useIsolation ? image : null);
            DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); stack.Children.Add(actions);
        Content = stack;
        UpdateControls();
    }

    private void UpdateControls()
    {
        _write.IsEnabled = _inherit.IsChecked != true;
        _shell.IsEnabled = _write.IsEnabled && _write.IsChecked == true;
        _isolated.IsEnabled = _shell.IsEnabled && _shell.IsChecked == true;
        _image.IsEnabled = _isolated.IsEnabled && _isolated.IsChecked == true;
        _push.IsEnabled = _codex.IsEnabled = _shell.IsEnabled && !_image.IsEnabled;
    }
}
