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
                allowed.Contains(key))env[key]=value;
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
