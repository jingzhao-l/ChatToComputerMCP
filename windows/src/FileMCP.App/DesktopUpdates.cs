using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileMCP.App;

/// <summary>
/// Native GUI updater. It only checks the public npm registry and compiles
/// into a new user-owned version directory. It NEVER restarts the running app,
/// modifies FileMCP, or enables a background service.
/// </summary>
internal static class DesktopUpdates
{
    internal sealed record Preferences(int Schema, bool CheckAutomatically, bool InstallAutomatically);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private const string PackageName = "chat-to-computer-mcp";
    private const string Registry = "https://registry.npmjs.org/";

    internal static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatToComputerMCP", "update-settings.json");

    internal static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" :
                $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    internal static bool ValidVersion(string? value) =>
        value is not null && Regex.IsMatch(value,
            @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool IsNewer(string candidate, string baseline)
    {
        if (!ValidVersion(candidate) || !ValidVersion(baseline)) return false;
        var a = candidate.Split('.').Select(ulong.Parse).ToArray();
        var b = baseline.Split('.').Select(ulong.Parse).ToArray();
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    internal static Preferences ReadPreferences(string? file = null)
    {
        file ??= SettingsPath;
        if (!File.Exists(file) && !Directory.Exists(file)) return new Preferences(1, false, false);
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing symlinked updater preferences");
        if (new FileInfo(file).Length > 16_384)
            throw new IOException("Updater preferences are unexpectedly large");
        var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file), JsonOptions);
        if (value is null || value.Schema != 1 ||
            (value.InstallAutomatically && !value.CheckAutomatically))
            throw new IOException("Invalid updater preferences");
        return value;
    }

    internal static void SavePreferences(Preferences value, string? file = null)
    {
        file ??= SettingsPath;
        if (value.Schema != 1 || (value.InstallAutomatically && !value.CheckAutomatically))
            throw new ArgumentException("Invalid updater policy");
        var folder = Path.GetDirectoryName(file) ??
            throw new IOException("Updater settings location is invalid");
        Directory.CreateDirectory(folder);
        if (File.Exists(file) &&
            (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing symlinked updater preferences");
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best-effort temp cleanup */ }
        }
    }

    internal static async Task<string> CheckLatestAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync(Registry + PackageName + "/latest");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        if (payload.Length > 512_000)
            throw new IOException("Unexpectedly large npm registry metadata");
        using var metadata = JsonDocument.Parse(payload);
        var record = metadata.RootElement;
        if (record.GetProperty("name").GetString() != PackageName)
            throw new IOException("Unexpected npm package metadata");
        var version = record.GetProperty("version").GetString();
        if (!ValidVersion(version))
            throw new IOException("npm latest does not reference a stable SemVer release");
        return version!;
    }

    internal static bool Installed(string version)
    {
        if (!ValidVersion(version)) return false;
        var program = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "releases", version, "ChatToComputerMCP.exe");
        return File.Exists(program);
    }

    internal static string ActivationExecutable(string version, string? releases = null)
    {
        if (!ValidVersion(version)) throw new ArgumentException("Invalid release version");
        var root = releases ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "releases");
        var directory = Path.Combine(root, version);
        var executable = Path.Combine(directory, "ChatToComputerMCP.exe");
        if (!Directory.Exists(directory) || !File.Exists(executable) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Installed release is missing or linked");
        var info = FileVersionInfo.GetVersionInfo(executable);
        var installedVersion = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        if (!string.Equals(installedVersion, version, StringComparison.Ordinal))
            throw new IOException("Installed executable version does not match");
        return executable;
    }

    // Use a fixed, noninteractive helper with a base64-encoded data-only path.
    // It never terminates the old process, and refuses to launch if it stays alive.
    internal static void ScheduleActivation(string version)
    {
        var executable = ActivationExecutable(version);
        var pathData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(executable));
        var script = "$ErrorActionPreference='Stop'; " +
            "$target=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + pathData + "')); " +
            "$old=Get-Process -Id " + Environment.ProcessId + " -ErrorAction SilentlyContinue; " +
            "if ($old -and !$old.WaitForExit(60000)) { exit 1 }; " +
            "Start-Process -FilePath $target -WorkingDirectory ([IO.Path]::GetDirectoryName($target))";
        var command = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo { FileName = powershell, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("Could not schedule update activation");
    }

    internal static async Task InstallAsync(string version)
    {
        if (!ValidVersion(version)) throw new ArgumentException("Invalid release version");
        var arguments = "npm exec --yes --registry=" + Registry +
            " --package=" + PackageName + "@" + version +
            " -- chat-to-computer-mcp install";
        var isolated = Path.Combine(Path.GetTempPath(), "ctcmcp-gui-official-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = isolated,
                Arguments = "/d /s /c \"" + arguments + "\"",
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            }
        };
        if (!child.Start())
            throw new IOException("Could not launch npm; install Node.js and npm first");
        await child.WaitForExitAsync();
        if (child.ExitCode != 0 || !Installed(version))
            throw new IOException("Native build failed. Run 'chat-to-computer-mcp update install' in a terminal to see npm logs.");
        }
        finally { try { Directory.Delete(isolated, true); } catch { } }
    }
}
