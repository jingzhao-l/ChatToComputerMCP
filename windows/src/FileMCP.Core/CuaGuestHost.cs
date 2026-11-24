using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FileMCP.Core;

/// <summary>Explicit local guest-only entry point. No workspace/shell tools or public listener.</summary>
public static class CuaGuestHost
{
    public static async Task RunAsync(CancellationToken cancellationToken,string? relayDomain=null)
    {
        var approvalEpoch=CuaGuestApproval.Epoch()??throw new FileMcpException("Guest is disabled; enable it locally first");
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChatToComputerMCP");
        if(relayDomain is not null){
            if(!Regex.IsMatch(relayDomain,"^[A-Za-z0-9_-]{1,48}$"))throw new FileMcpException("Invalid relay domain");
            root=Path.Combine(root,"cua-relays",relayDomain);
            if(!Directory.Exists(root)||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new FileMcpException("Private relay directory required");
        }
        string PrivateText(string file)
        {
            var f=new FileInfo(Path.Combine(root,file));
            using var identity=WindowsIdentity.GetCurrent();
            var sid=identity.User??throw new FileMcpException("Current user unavailable");
            var acl=f.GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access);
            if(!f.Exists||f.Length is <=0 or >4096||(f.Attributes&FileAttributes.ReparsePoint)!=0||
                !sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))))throw new FileMcpException("Invalid guest config");
            var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
            var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if(rule.AccessControlType==AccessControlType.Allow&&!sid.Equals(rule.IdentityReference)&&!system.Equals(rule.IdentityReference)&&!admins.Equals(rule.IdentityReference))
                    throw new FileMcpException("Guest config must be private");
            return File.ReadAllText(f.FullName);
        }
        var config=JsonNode.Parse(PrivateText(relayDomain is null?"cua-guest.json":"relay.json"))!;
        var token=PrivateText(relayDomain is null?"cua-guest-token":"relay-token").Trim();
        var confirmed=relayDomain is null?config["isolatedDesktopConfirmed"]?.GetValue<bool>()==true:
            config["dedicatedGuestConfirmed"]?.GetValue<bool>()==true&&config["domain"]?.GetValue<string>()==relayDomain;
        if(!confirmed||config["port"] is not JsonValue p||
            !p.TryGetValue<int>(out var port)||port is <1024 or >65535||!Regex.IsMatch(token,"^[a-f0-9]{64}$"))
            throw new FileMcpException("Run local cua guest-init in the isolated guest first");
        var workspace=Path.Combine(root,"guest-empty-workspace");Directory.CreateDirectory(workspace);
        await using var server=new LocalMcpServer((ushort)port,workspace,"","",false,token,_=>{},cuaGuest:relayDomain is null,cuaPolicyPath:Path.Combine(root,"cua-policy.json"),cuaRelayOnly:relayDomain is not null);
        if(CuaGuestApproval.Epoch()!=approvalEpoch)throw new FileMcpException("Guest approval changed");
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        while(CuaGuestApproval.Epoch()==approvalEpoch)
            await Task.Delay(250,cancellationToken).ConfigureAwait(false);
    }
}
