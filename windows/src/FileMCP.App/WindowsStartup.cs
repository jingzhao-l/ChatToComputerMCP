using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FileMCP.App;

/// <summary>
/// Per-user, opt-in Windows counterpart of the macOS LaunchAgent.
/// A HKCU Run entry starts a detached, non-privileged supervisor after LOGIN.
/// The supervisor launches the normal app hidden to tray and restarts it
/// after a crash; AlwaysOn also restarts normal exits. No Windows service,
/// admin privilege, system-wide policy or legacy FileMCP registration.
/// </summary>
internal static class WindowsStartup
{
    private const string RegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryName = "ChatToComputerMCP";
    private const string AppName = "ChatToComputerMCP";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal sealed record Policy(int Schema, bool Enabled, bool AutoConnect, bool AlwaysOn);

    internal static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName, "startup.json");

    internal static Policy Read(string? file = null)
    {
        file ??= SettingsPath;
        if (!File.Exists(file) && !Directory.Exists(file))
            return new Policy(1, false, false, false);
        RefuseLinks(file);
        if (new FileInfo(file).Length > 16_384)
            throw new IOException("Windows startup policy is unexpectedly large");
        var policy = JsonSerializer.Deserialize<Policy>(
            File.ReadAllText(file), JsonOptions);
        if (policy is null || policy.Schema != 1 ||
            (!policy.Enabled && (policy.AutoConnect || policy.AlwaysOn)))
            throw new IOException("Invalid Windows startup policy");
        return policy;
    }

    internal static void Save(Policy policy, string? file = null)
    {
        if (policy.Schema != 1 ||
            (!policy.Enabled && (policy.AutoConnect || policy.AlwaysOn)))
            throw new ArgumentException("Invalid startup policy");
        file ??= SettingsPath;
        var folder = Path.GetDirectoryName(file) ??
            throw new IOException("Startup settings directory unavailable");
        Directory.CreateDirectory(folder);
        RefuseLinks(file);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(policy, JsonOptions));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static void RefuseLinks(string file)
    {
        try
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing symlinked startup settings");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static string RegisteredCommand
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "";
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(RegistryName) as string ?? "";
        }
    }

    internal static bool IsRegistered =>
        !string.IsNullOrWhiteSpace(RegisteredCommand);

    internal static void Configure(Policy policy, string executable)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        if (policy.Schema != 1)
            throw new IOException("Unexpected startup policy version");
        if (!policy.Enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            key?.DeleteValue(RegistryName, throwOnMissingValue: false);
            Save(new Policy(1, false, false, false));
            return;
        }
        // Reject development and temporary builds; only allow an installed
        // release executable in this user's own app data folder.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName, "releases");
        var full = Path.GetFullPath(executable);
        if (!full.StartsWith(Path.GetFullPath(appData) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(full) != AppName + ".exe" || !File.Exists(full))
            throw new IOException("Install ChatToComputerMCP into the stable user release folder before enabling login start.");
        var installedVersion = new DirectoryInfo(Path.GetDirectoryName(full)!).Name;
        if (!DesktopUpdates.ValidVersion(installedVersion))
            throw new IOException("Startup executable is not versioned");
        if (policy.AutoConnect)
        {
            // Never enable auto-connect if the old app is registered for
            // login start and may use the same OpenAI tunnel ID.
            using var existing = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (existing?.GetValue("FileMCP") is not null)
                throw new IOException("Existing FileMCP login agent is configured; disable it before enabling automatic tunnel connection.");
        }
        using var run = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new IOException("Could not create the user startup registration");
        var prior = run.GetValue(RegistryName) as string;
        run.SetValue(RegistryName, "\"" + full + "\" --supervise",
            RegistryValueKind.String);
        try { Save(policy); }
        catch
        {
            if (prior is not null) run.SetValue(RegistryName, prior, RegistryValueKind.String);
            else run.DeleteValue(RegistryName, throwOnMissingValue: false);
            throw;
        }
    }

    /// <summary>
    /// Used after a safe local package upgrade: only an already registered,
    /// locally owned startup entry can be refreshed. Does not boot or stop
    /// any live process and never changes the user's startup preferences.
    /// </summary>
    internal static void RefreshInstalledExecutable(string executable)
    {
        if (!OperatingSystem.IsWindows() || !IsRegistered) return;
        var policy = Read();
        if (policy.Enabled) Configure(policy, executable);
    }

    internal static void SelfTestIsolatedPolicy()
    {
        // Does not read or modify HKCU startup keys, live application data,
        // OS credentials or another running ChatToComputerMCP instance.
        var directory = Path.Combine(Path.GetTempPath(), "ctcmcp-startup-selftest-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "startup.json");
            if (Read(file).Enabled) throw new IOException("Startup was unexpectedly enabled");
            var approved = new Policy(1, true, true, false);
            Save(approved, file);
            if (Read(file) != approved) throw new IOException("Startup policy roundtrip failed");
            Save(new Policy(1, false, false, false), file);
            if (Read(file).Enabled) throw new IOException("Startup disable failed");
            try
            {
                Save(new Policy(1, false, true, true), file);
                throw new IOException("Invalid startup policy accepted");
            }
            catch (ArgumentException) { }
            File.WriteAllText(file, "{broken");
            try
            {
                _ = Read(file);
                throw new IOException("Corrupt startup policy was silently reset");
            }
            catch (JsonException) { }
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    internal static async Task RunSupervisorAsync(string executable)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var mutex = new System.Threading.Mutex(false,
            @"Local\ChatToComputerMCP-LoginSupervisor");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        try
        {
            var failedQuickly = 0;
            while (true)
            {
                Policy policy;
                try { policy = Read(); }
                catch { return; } // corrupted policy cannot enable autostart
                if (!policy.Enabled || !IsRegistered) return;
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "--login-start",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(executable)!
                    }
                };
                var started = DateTimeOffset.UtcNow;
                try
                {
                    if (!process.Start()) return;
                    await process.WaitForExitAsync();
                    var exit = process.ExitCode;
                    if (!policy.AlwaysOn && exit == 0) return;
                }
                catch { return; }
                finally { process.Dispose(); }
                // Never spin infinitely if the app cannot reach startup.
                failedQuickly = DateTimeOffset.UtcNow - started <
                    TimeSpan.FromSeconds(20) ? failedQuickly + 1 : 0;
                if (failedQuickly >= 5) return;
                await Task.Delay(TimeSpan.FromSeconds(4));
            }
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}
