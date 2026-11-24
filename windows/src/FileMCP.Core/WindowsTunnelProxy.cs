using Microsoft.Win32;
namespace FileMCP.Core;

internal static class WindowsTunnelProxy
{
    internal static string? Discover()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (settings?.GetValue("ProxyEnable") is not int enabled || enabled != 1) return null;
            return ParseManual(settings.GetValue("ProxyServer") as string);
        }
        catch { return null; }
    }

    internal static string? ParseManual(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;
        var entries = setting.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? candidate = null;
        if (entries.Length == 1 && !entries[0].Contains('=')) candidate = entries[0];
        else
        {
            foreach (var protocol in new[] { "https", "http" })
            {
                candidate = entries.FirstOrDefault(item => item.StartsWith(protocol + "=", StringComparison.OrdinalIgnoreCase));
                if (candidate is not null) { candidate = candidate[(protocol.Length + 1)..].Trim(); break; }
            }
        }
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "http://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    internal static void ApplyFallback(Dictionary<string, string> environment, Func<string?> discover)
    {
        if (new[] { "CONTROL_PLANE_HTTP_PROXY", "TUNNEL_CLIENT_HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
            .Any(key => environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))) return;
        if (discover() is { } proxy) environment["CONTROL_PLANE_HTTP_PROXY"] = proxy;
    }
}
