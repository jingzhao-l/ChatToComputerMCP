using System.Collections;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace FileMCP.Core;

/// <summary>
/// Locates cloudflared and prepares secure, non-command-line token handling.
/// Never downloads a binary automatically or logs a Cloudflare secret.
/// </summary>
internal static class WindowsCloudflareRunner
{
    internal sealed record Startup(string Executable, string[] Args,
        Dictionary<string,string> Environment, string TokenFile);

    internal static bool ValidHostname(string value) =>
        value.Length is >= 4 and <= 253 &&
        value.Split('.').Length >= 2 &&
        value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch=='-')) &&
        !value.Contains("localhost",StringComparison.OrdinalIgnoreCase);

    internal static string Locate()
    {
        var testOrCustom = Environment.GetEnvironmentVariable("MCP_CLOUDFLARED");
        if (!string.IsNullOrWhiteSpace(testOrCustom) && File.Exists(testOrCustom))
            return Path.GetFullPath(testOrCustom);
        var candidates = new List<string> {
            Path.Combine(AppContext.BaseDirectory, "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Cloudflare", "cloudflared.exe")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)
            .Select(p=>Path.Combine(p.Trim('"'),"cloudflared.exe")));
        var found=candidates.FirstOrDefault(File.Exists);
        return found ?? throw new FileMcpException(
            "cloudflared.exe was not found. Install Cloudflare Tunnel for Windows and add it to PATH.");
    }

    internal static string? OriginCertificatePath()
    {
        var custom = Environment.GetEnvironmentVariable("TUNNEL_ORIGIN_CERT");
        if (!string.IsNullOrWhiteSpace(custom)) return File.Exists(custom) ? Path.GetFullPath(custom) : null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] {
            Path.Combine(home, ".cloudflared", "cert.pem"),
            Path.Combine(home, ".cloudflare-warp", "cert.pem"),
            Path.Combine(home, "cloudflare-warp", "cert.pem"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cloudflared", "cert.pem")
        }.FirstOrDefault(File.Exists);
    }

    internal static async Task<string?> LoginAsync(bool replacingExisting, CancellationToken cancellation)
    {
        var existing = OriginCertificatePath();
        if (existing is not null && !replacingExisting) return null;
        string? backup = null;
        if (existing is not null)
        {
            CommandReceiptLedger.RefuseLinks(existing);
            backup = existing + ".bak-" + Guid.NewGuid().ToString("N");
            File.Move(existing, backup);
        }
        try
        {
            var result = await ProcessRunner.RunAsync(Locate(), ["tunnel", "login"], timeoutSeconds: 600,
                outputLimitBytes: 40_000, cancellationToken: cancellation, maxTimeoutSeconds: 600).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (result.TimedOut || result.ExitCode != 0 || OriginCertificatePath() is null ||
                (result.Stdout + result.Stderr).Contains("existing certificate", StringComparison.OrdinalIgnoreCase))
                throw new FileMcpException("Cloudflare login did not produce a new certificate");
            return backup;
        }
        catch
        {
            // Do not overwrite a new certificate created by another process.
            if (existing is not null && backup is not null && !File.Exists(existing))
                File.Move(backup, existing);
            throw;
        }
    }

    public sealed record NamedTunnel(string Id, string Name);

    internal static bool IsNamedTunnel(string value) =>
        value.Length is >= 1 and <= 100 &&
        System.Text.RegularExpressions.Regex.IsMatch(value,
            @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z");

    internal static IReadOnlyList<NamedTunnel> ListNamedTunnels()
    {
        var executable = Locate();
        var result = ProcessRunner.RunAsync(executable,
            ["tunnel", "list", "-o", "json"], timeoutSeconds: 20,
            outputLimitBytes: 350_000).GetAwaiter().GetResult();
        if (result.ExitCode != 0 || result.TimedOut)
            throw new FileMcpException("Could not list local Cloudflare tunnels; use cloudflared tunnel login first");
        using var parsed = System.Text.Json.JsonDocument.Parse(result.Stdout);
        if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new FileMcpException("Cloudflare tunnel listing did not return an array");
        var names = new List<NamedTunnel>();
        foreach (var item in parsed.RootElement.EnumerateArray())
        {
            var id=item.TryGetProperty("id",out var guid)?guid.GetString():"";
            var name=item.TryGetProperty("name",out var label)?label.GetString():"";
            if(id is not null && name is not null && Guid.TryParse(id,out _) &&
               IsNamedTunnel(name)) names.Add(new NamedTunnel(id,name));
        }
        return names;
    }

    internal static string CreateNamedTunnel(string name)
    {
        if (!IsNamedTunnel(name)) throw new FileMcpException("Invalid tunnel name");
        var executable=Locate();
        var result=ProcessRunner.RunAsync(executable,
            ["tunnel", "create", name], timeoutSeconds: 45,
            outputLimitBytes: 40_000).GetAwaiter().GetResult();
        if(result.ExitCode != 0 || result.TimedOut)
            throw new FileMcpException("Creating Cloudflare tunnel failed; check your cloudflared login and DNS account");
        return ListNamedTunnels().FirstOrDefault(t=>t.Name==name)?.Id ??
            throw new FileMcpException("Newly created tunnel was not found in Cloudflare account");
    }

    internal static void DeleteNamedTunnel(string name)
    {
        if (!IsNamedTunnel(name)) throw new FileMcpException("Invalid tunnel name");
        var executable=Locate();
        var result=ProcessRunner.RunAsync(executable,
            ["tunnel", "delete", name], timeoutSeconds: 40,
            outputLimitBytes: 40_000).GetAwaiter().GetResult();
        if(result.ExitCode!=0||result.TimedOut)
            throw new FileMcpException("Deleting Cloudflare tunnel failed; check for active connectors");
    }

    internal static void RouteNamedTunnel(string name,string hostname)
    {
        if(!IsNamedTunnel(name)||!ValidHostname(hostname))
            throw new FileMcpException("Invalid Cloudflare tunnel or hostname");
        var executable=Locate();
        var result=ProcessRunner.RunAsync(executable,
            ["tunnel", "route", "dns", name, hostname],
            timeoutSeconds:40,outputLimitBytes:40_000).GetAwaiter().GetResult();
        if(result.ExitCode!=0||result.TimedOut)
            throw new FileMcpException("Cloudflare DNS route failed; check existing DNS records and account permissions");
    }

    internal static Startup PrepareNamed(string executable,string name,
        string hostname,ushort port,string profile)
    {
        if(!IsNamedTunnel(name)||!ValidHostname(hostname)||
           !LocalMcpRuntime.IsValidProfileName(profile))
            throw new FileMcpException("Invalid local named Cloudflare tunnel configuration");
        var matching=ListNamedTunnels().FirstOrDefault(item=>item.Name==name) ??
            throw new FileMcpException("Named tunnel was not found; log in and create or select a tunnel");
        var identifier=Guid.Parse(matching.Id).ToString();
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates=new[] {
            Path.Combine(home,".cloudflared",identifier+".json"),
            Path.Combine(home,".cloudflare-warp",identifier+".json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "cloudflared",identifier+".json")
        };
        var credentialFile=candidates.FirstOrDefault(File.Exists) ??
            throw new FileMcpException("Credentials file for the selected Cloudflare tunnel was not found");
        var privateDirectory=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP","tunnel-profiles");
        EnsurePrivateDirectory(privateDirectory);
        var config=Path.Combine(privateDirectory,
            "cloudflared-"+profile+"-"+Guid.NewGuid().ToString("N")+".yml");
        var yaml="ingress:\n  - hostname: "+hostname+
            "\n    service: http://127.0.0.1:"+port+
            "\n  - service: http_status:404\n";
        try
        {
            File.WriteAllText(config,yaml,new UTF8Encoding(false));
            if(OperatingSystem.IsWindows())
            {
                var sid=WindowsIdentity.GetCurrent().User ??
                    throw new FileMcpException("Windows user identity unavailable");
                var acl=new FileSecurity();
                acl.SetAccessRuleProtection(true,false);
                acl.AddAccessRule(new FileSystemAccessRule(sid,
                    FileSystemRights.FullControl,AccessControlType.Allow));
                new FileInfo(config).SetAccessControl(acl);
            }
        }
        catch
        {
            try { File.Delete(config); } catch { }
            throw;
        }
        var environment=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var key in new[] {"PATH","USERPROFILE","APPDATA","LOCALAPPDATA",
                "SystemRoot","WINDIR","TEMP","TMP","HTTP_PROXY","HTTPS_PROXY","NO_PROXY"})
        {
            var value=Environment.GetEnvironmentVariable(key);
            if(!string.IsNullOrEmpty(value))environment[key]=value;
        }
        foreach(DictionaryEntry envVar in Environment.GetEnvironmentVariables())
        {
            if(envVar.Key is string key && envVar.Value is string value &&
               key.StartsWith("MCP_TEST_",StringComparison.OrdinalIgnoreCase))
                environment[key]=value;
        }
        return new Startup(executable,
            ["tunnel","--no-autoupdate","--config",config,
             "run","--credentials-file",credentialFile,name],environment,config);
    }

    internal static Startup Prepare(string executable,string token,string profile)
    {
        if(!File.Exists(executable)||!LocalMcpRuntime.IsValidProfileName(profile)||
           string.IsNullOrWhiteSpace(token)||token.Length>8192||
           token.Any(ch=>ch is '\r' or '\n' or '\0'))
            throw new FileMcpException("Cloudflare tunnel setup is invalid");
        // OAuth is the only public auth mode. Require modern cloudflared
        // --token-file to avoid exposing dashboard tunnel tokens on command
        // line or in the process environment (both locally inspectable).
        var location=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "tunnel-profiles");
        EnsurePrivateDirectory(location);
        var secretFile=Path.Combine(location,"cloudflared-"+profile+"-"+
            Guid.NewGuid().ToString("N")+".token");
        try
        {
            using var stream=new FileStream(secretFile,FileMode.CreateNew,
                FileAccess.Write,FileShare.None);
            var data=Encoding.UTF8.GetBytes(token);
            stream.Write(data);
            stream.Flush(true);
            if(OperatingSystem.IsWindows())
            {
                var user=WindowsIdentity.GetCurrent().User ??
                    throw new FileMcpException("No Windows user identity");
                var security=new FileSecurity();
                security.SetAccessRuleProtection(true,false);
                security.AddAccessRule(new FileSystemAccessRule(user,
                    FileSystemRights.FullControl,AccessControlType.Allow));
                new FileInfo(secretFile).SetAccessControl(security);
            }
        }
        catch
        {
            try{File.Delete(secretFile);}catch{}
            throw;
        }
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA",
            "LOCALAPPDATA", "TEMP", "TMP", "SystemRoot", "WINDIR",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "LANG", "LC_ALL", "LC_CTYPE"
        };
        var env = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if(item.Key is string key && item.Value is string value &&
                (allowed.Contains(key) || key.StartsWith("MCP_TEST_",StringComparison.OrdinalIgnoreCase)))
                env[key]=value;
        }
        return new Startup(executable,
            ["tunnel","--no-autoupdate","run","--token-file",secretFile],
            env,secretFile);
    }

    internal static void RemovePrivateToken(string file)
    {
        if(string.IsNullOrWhiteSpace(file))return;
        try{File.Delete(file);}catch{}
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        for(var node=directory;!string.IsNullOrWhiteSpace(node);node=Path.GetDirectoryName(node))
        {
            if(Directory.Exists(node)&&
               (File.GetAttributes(node)&FileAttributes.ReparsePoint)!=0)
                throw new FileMcpException("Tunnel profile path includes a reparse point");
            if(string.Equals(Path.GetPathRoot(node),node,StringComparison.OrdinalIgnoreCase))
                break;
        }
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())return;
        var owner=WindowsIdentity.GetCurrent().User ??
            throw new FileMcpException("Windows identity unavailable");
        var access=new DirectorySecurity();
        access.SetAccessRuleProtection(true,false);
        access.AddAccessRule(new FileSystemAccessRule(owner,FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,
            PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(access);
    }
}
