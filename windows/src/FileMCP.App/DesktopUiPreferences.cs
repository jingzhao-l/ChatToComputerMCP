using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace FileMCP.App;

internal sealed class DesktopUiPreferences
{
    public bool Dark { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatToComputerMCP", "desktop-ui.json");

    internal static DesktopUiPreferences Load()
    {
        try { return JsonSerializer.Deserialize<DesktopUiPreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    internal void Save()
    {
        var temp = FilePath + "." + Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, true);
        } catch { }
        finally { try { File.Delete(temp); } catch { } }
    }

    internal static void ApplyPalette(bool dark)
    {
        var resources = System.Windows.Application.Current.Resources;
        var palette = new (ResourceKey Key, string Color)[] {
            (SystemColors.WindowBrushKey, "#202125"), (SystemColors.WindowTextBrushKey, "#F1F2F3"),
            (SystemColors.ControlBrushKey, "#34363B"), (SystemColors.ControlTextBrushKey, "#F1F2F3"),
            (SystemColors.GrayTextBrushKey, "#B5B9C2"), (SystemColors.ActiveBorderBrushKey, "#686C77"),
            (SystemColors.HighlightBrushKey, "#315985"), (SystemColors.HighlightTextBrushKey, "#FFFFFF"),
            (SystemColors.InactiveSelectionHighlightBrushKey, "#41454F"),
            (SystemColors.InactiveSelectionHighlightTextBrushKey, "#F1F2F3")
        };
        foreach (var (key, value) in palette)
        {
            if (!dark || SystemParameters.HighContrast) resources.Remove(key);
            else {
                var brush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
                brush.Freeze(); resources[key] = brush;
            }
        }
    }
}
