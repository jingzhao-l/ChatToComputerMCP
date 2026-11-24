using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FileMCP.Core;

/// <summary>Local Docker Desktop Linux container mode; never falls back to host execution.</summary>
internal sealed record ShellExecutionPlan(string Executable, IReadOnlyList<string> Arguments,
    string Cwd, IReadOnlyDictionary<string,string> Environment, Action? Cleanup)
{
    internal static ShellExecutionPlan Build(string root, string cwd, string command, string shell,
        int timeout, string? image)
    {
        if (image is null)
            return new(shell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], cwd,
                System.Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase), null);
        if (!Regex.IsMatch(image, @"\Asha256:[a-f0-9]{64}\z") || root.Contains(',') || root.Contains('\n') || root.Contains('\r') || root.Contains('"') ||
            string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
            throw new FileMcpException("Isolated shell requires a pinned local image ID and a non-root workspace without comma/newline");
        var resolver = new SafePathResolver(root);
        var relative = resolver.RelativePath(cwd);
        if (!string.Equals(resolver.Resolve(relative), cwd, StringComparison.OrdinalIgnoreCase))
            throw new FileMcpException("Isolated shell working directory is outside the workspace");
        var docker = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe");
        if (!File.Exists(docker)) throw new FileMcpException("Isolated shell requires local Docker Desktop; host execution is not permitted");
        var config = Path.Combine(Path.GetTempPath(), "ctcmcp-docker-" + Guid.NewGuid().ToString("N"));
        CommandReceiptLedger.EnsurePrivateFolder(config);
        var environment = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            ["SystemRoot"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            ["PATH"] = Path.GetDirectoryName(docker)!,
            ["DOCKER_CONFIG"] = config
        };
        string[] prefix = ["--config", config, "--host", "npipe:////./pipe/dockerDesktopLinuxEngine"];
        var name = "ctcmcp-" + Guid.NewGuid().ToString("D");
        var args = prefix.Concat(new[] { "run", "--rm", "--pull=never", "--name", name,
            "--network", "none", "--read-only", "--cap-drop", "ALL", "--no-healthcheck", "--log-driver", "none", "--restart", "no",
            "--security-opt", "no-new-privileges", "--user", "65532:65532",
            "--pids-limit", "256", "--memory", "2g", "--cpus", "2",
            "--mount", "type=bind,source=" + root + ",target=/workspace,bind-recursive=disabled",
            "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m",
            "--env", "HOME=/tmp", "--env", "HTTP_PROXY=", "--env", "HTTPS_PROXY=",
            "--env", "ALL_PROXY=", "--env", "http_proxy=", "--env", "https_proxy=", "--env", "all_proxy=",
            "--workdir", string.IsNullOrEmpty(relative) || relative == "." ? "/workspace" : "/workspace/" + relative.Replace('\\','/'),
            "--entrypoint", "/usr/bin/timeout", image, "--signal=TERM", "--kill-after=2s",
            Math.Max(1, timeout).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s", "/bin/sh", "-c", command }).ToArray();
        void Cleanup()
        {
            try { ProcessRunner.RunAsync(docker, prefix.Concat(new[] { "rm", "-f", name }).ToArray(), root,
                environment, 10).GetAwaiter().GetResult(); } catch { }
            try { Directory.Delete(config, true); } catch { }
        }
        return new(docker, args, root, environment, Cleanup);
    }
}

