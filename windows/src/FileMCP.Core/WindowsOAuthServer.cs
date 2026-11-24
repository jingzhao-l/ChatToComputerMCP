using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileMCP.Core;

/// <summary>
/// User-approved, local OAuth 2.0 / PKCE server for Windows Cloudflare mode.
/// Neither tool arguments nor self-reported client names are trusted as identity.
/// Tokens and authorization codes are opaque; only hashes are persisted.
/// </summary>
public sealed class WindowsOAuthServer
{
    internal sealed record VerifiedClient(string Id, IReadOnlySet<string> Workspaces, Guid Revision, string TokenDigest);
    public sealed record DesktopClient(string Id, string Name,
        IReadOnlyList<string> Workspaces, DateTimeOffset ApprovedAt);
    internal sealed record Response(int Status, string ContentType, byte[] Body,
        IReadOnlyDictionary<string, string>? Headers = null);

    private sealed class Client
    {
        public string[] Redirects { get; set; } = [];
        public string Name { get; set; } = "MCP client";
        public DateTimeOffset Created { get; set; }
        public bool Approved { get; set; }
        public string[] Workspaces { get; set; } = ["default"];
    }
    private sealed class Grant
    {
        public string Client { get; set; } = "";
        public string Family { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
    }
    private sealed class Store
    {
        public int Schema { get; set; } = 1;
        public string Issuer { get; set; } = "";
        public string Policy { get; set; } = "";
        public Dictionary<string, Client> Clients { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Grant> Access { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Grant> Refresh { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Grant> ConsumedRefresh { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed record Authorization(string Client, string Redirect,
        string Challenge, string State, DateTimeOffset Expires);

    private static readonly JsonSerializerOptions Serializer = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> Metadata = new(StringComparer.Ordinal)
    {
        "/.well-known/oauth-protected-resource/mcp",
        "/.well-known/oauth-protected-resource",
        "/.well-known/oauth-authorization-server"
    };
    private readonly object _gate = new();
    private readonly string _pairingCode;
    private readonly string _policy;
    private string _permissionDescription;
    private readonly string _file;
    private ApprovedWorkspace[] _approved;
    private Guid _catalogRevision = Guid.NewGuid();
    private readonly Dictionary<string, Authorization> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Authorization> _codes = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _registrationTimes = [];
    private readonly List<DateTimeOffset> _loginAttempts = [];
    private Store _store;

    internal string Issuer { get; }
    internal string Resource => Issuer + "/mcp";

    internal static string DefaultFile(string hostname)
    {
        var digest = Hash(hostname.ToLowerInvariant());
        return Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "ChatToComputerMCP", "oauth", digest + ".json");
    }

    internal WindowsOAuthServer(string issuer, string pairingCode,
        string permissionPolicy, string permissionDescription,
        IReadOnlyList<ApprovedWorkspace>? approvedWorkspaces = null,
        string? file = null)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 ||
            uri.Port != 443 || issuer.EndsWith('/'))
            throw new FileMcpException("OAuth issuer must be a valid HTTPS hostname without a path");
        if (Encoding.UTF8.GetByteCount(pairingCode) is < 32 or > 128 ||
            !Regex.IsMatch(pairingCode, @"\A[A-Za-z0-9_-]+\z"))
            throw new FileMcpException("OAuth pairing code must be 32–128 URL-safe characters");
        Issuer = issuer;
        _pairingCode = pairingCode;
        _approved = (approvedWorkspaces ?? []).ToArray();
        _permissionDescription = permissionDescription;
        _policy = Hash(permissionPolicy + "|" + pairingCode);
        _file = Path.GetFullPath(file ?? DefaultFile(uri.Host));
        var folder = Path.GetDirectoryName(_file)!;
        EnsurePrivateDirectory(folder);
        RefuseLink(_file);
        if (!File.Exists(_file))
        {
            _store = new Store { Issuer = Issuer, Policy = _policy };
        }
        else
        {
            try
            {
                if (new FileInfo(_file).Length > 2_000_000)
                    throw new FileMcpException("Stored OAuth metadata exceeds size limit");
                _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Serializer)
                    ?? throw new FileMcpException("Invalid OAuth state");
                if (_store.Schema != 1 || _store.Issuer != Issuer ||
                    _store.Policy.Length != 64 ||
                    _store.Clients.Count > 64 || _store.Access.Count > 128 ||
                    _store.Refresh.Count > 128 || _store.ConsumedRefresh.Count > 512)
                    throw new FileMcpException("Invalid or mismatched OAuth state");
                if (_store.Policy != _policy)
                {
                    // Workspace, permission, hostname or pairing-code change
                    // always invalidates active token families.
                    _store.Policy = _policy;
                    _store.Access.Clear();
                    _store.Refresh.Clear();
                    _store.ConsumedRefresh.Clear();
                    Persist();
                }
            }
            catch (Exception ex)
            {
                throw new FileMcpException("Invalid OAuth state; refusing to lose authorization history: " +
                    ex.GetType().Name);
            }
        }
    }

    internal bool IsCurrent(VerifiedClient client)
    {
        lock (_gate) return client.Revision == _catalogRevision &&
            _store.Access.TryGetValue(client.TokenDigest, out var grant) &&
            grant.Expires > DateTimeOffset.UtcNow && grant.Client == client.Id &&
            _store.Clients.TryGetValue(client.Id, out var registered) && registered.Approved;
    }

    internal void ReplaceCatalog(IReadOnlyList<ApprovedWorkspace> definitions, string policy,
        string description, Action? persistSettings)
    {
        lock (_gate)
        {
            var before = CloneStore();
            _store.Policy = Hash(policy + "|" + _pairingCode);
            _store.Access.Clear(); _store.Refresh.Clear(); _store.ConsumedRefresh.Clear();
            try { Persist(); persistSettings?.Invoke(); }
            catch
            {
                _store = before;
                try { Persist(); } catch { }
                throw;
            }
            _approved = definitions.ToArray(); _permissionDescription = description;
            _requests.Clear(); _codes.Clear(); _catalogRevision = Guid.NewGuid();
        }
    }

    internal IReadOnlyList<DesktopClient> ApprovedClients()
    {
        lock (_gate)
        {
            return _store.Clients
                .Where(item => item.Value.Approved)
                .Select(item => new DesktopClient(item.Key,item.Value.Name,
                    item.Value.Workspaces,item.Value.Created))
                .OrderBy(item => item.ApprovedAt).ToArray();
        }
    }

    internal IReadOnlyList<string> RevokeClient(string id)
    {
        lock (_gate)
        {
            if (!_store.Clients.TryGetValue(id, out var record) || !record.Approved)
                throw new FileMcpException("This approved OAuth client does not exist");
            var snapshot = CloneStore();
            _store.Clients.Remove(id);
            _store.Access = _store.Access.Where(item => item.Value.Client != id)
                .ToDictionary(item => item.Key,item => item.Value,StringComparer.Ordinal);
            _store.Refresh = _store.Refresh.Where(item => item.Value.Client != id)
                .ToDictionary(item => item.Key,item => item.Value,StringComparer.Ordinal);
            _store.ConsumedRefresh = _store.ConsumedRefresh
                .Where(item => item.Value.Client != id)
                .ToDictionary(item => item.Key,item => item.Value,StringComparer.Ordinal);
            try { Persist(); } catch { _store = snapshot; throw; }
            return record.Workspaces;
        }
    }

    internal VerifiedClient? VerifyBearer(string? authorization)
    {
        if (authorization is null) return null;
        var words = authorization.Split(' ', StringSplitOptions.None);
        if (words.Length != 2 || words[0] != "Bearer") return null;
        lock (_gate)
        {
            if (!_store.Access.TryGetValue(Hash(words[1]), out var grant) ||
                grant.Expires <= DateTimeOffset.UtcNow ||
                !_store.Clients.TryGetValue(grant.Client, out var client) ||
                !client.Approved) return null;
            var allowed = new HashSet<string>(_approved.Select(x => x.Id),
                StringComparer.Ordinal) { "default" };
            allowed.IntersectWith(client.Workspaces);
            return new VerifiedClient(grant.Client, allowed, _catalogRevision, Hash(words[1]));
        }
    }

    internal bool IsPublicRoute(string method,string path) =>
        (method=="GET" && (Metadata.Contains(path)||path=="/oauth/authorize")) ||
        (method=="POST" && path is ("/oauth/register" or "/oauth/authorize" or
            "/oauth/token" or "/oauth/revoke"));

    internal Response? Handle(string method, string path, string query,
        string? contentType, byte[] body)
    {
        if (!IsPublicRoute(method,path))return null;
        lock (_gate)
        {
            Prune();
            try {return Route(method,path,query,contentType,body);}
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    "OAuth authorization request failed: " + ex.GetType().Name);
                return Error("invalid_request",400);
            }
        }
    }

    private Response Route(string method, string path, string query,
        string? contentType, byte[] body)
    {
        if (Metadata.Contains(path))
        {
            if (path.StartsWith("/.well-known/oauth-protected-resource",
                    StringComparison.Ordinal))
                return Json(new { resource=Resource,
                    authorization_servers=new[]{Issuer},
                    bearer_methods_supported=new[]{"header"},
                    scopes_supported=new[]{"mcp"} });
            return Json(new {
                issuer=Issuer,
                authorization_endpoint=Issuer+"/oauth/authorize",
                token_endpoint=Issuer+"/oauth/token",
                registration_endpoint=Issuer+"/oauth/register",
                revocation_endpoint=Issuer+"/oauth/revoke",
                revocation_endpoint_auth_methods_supported=new[]{"none"},
                response_types_supported=new[]{"code"},
                response_modes_supported=new[]{"query"},
                grant_types_supported=new[]{"authorization_code","refresh_token"},
                token_endpoint_auth_methods_supported=new[]{"none"},
                code_challenge_methods_supported=new[]{"S256"},
                authorization_response_iss_parameter_supported=true,
                scopes_supported=new[]{"mcp"}
            });
        }
        if(body.Length>16_384)return Error("invalid_request",413);
        if(path=="/oauth/register")
            return Register(contentType,body);
        if(method=="GET")
        {
            var args=ParseForm(query);
            return GetAuthorizationPage(args);
        }
        if(contentType?.Split(';')[0].Trim().ToLowerInvariant()
            !="application/x-www-form-urlencoded")return Error("invalid_request");
        var form=ParseForm(new UTF8Encoding(false,true).GetString(body));
        if(path=="/oauth/authorize")return Approve(form);
        if(path=="/oauth/revoke")return RevokeToken(form);
        if(path=="/oauth/token")return IssueToken(form);
        return Error("invalid_request",404);
    }

    private Response Register(string? contentType,byte[] data)
    {
        if(contentType?.Split(';')[0].Trim().ToLowerInvariant()!="application/json")
            return Error("invalid_client_metadata");
        var obj=JsonDocument.Parse(data).RootElement;
        if(obj.ValueKind!=JsonValueKind.Object ||
           !obj.TryGetProperty("redirect_uris",out var uris) ||
           uris.ValueKind!=JsonValueKind.Array ||
           uris.GetArrayLength() is < 1 or > 4)
            return Error("invalid_client_metadata");
        var redirects=uris.EnumerateArray().Select(x=>x.GetString()??"").ToArray();
        if(!redirects.All(ValidRedirect))return Error("invalid_client_metadata");
        if(obj.TryGetProperty("token_endpoint_auth_method",out var authMethod) &&
           authMethod.GetString()!="none")return Error("invalid_client_metadata");
        if(obj.TryGetProperty("grant_types",out var grants) &&
           grants.EnumerateArray().Any(item=> item.GetString() is not ("authorization_code" or "refresh_token")))
            return Error("invalid_client_metadata");
        if(obj.TryGetProperty("response_types",out var types) &&
           (types.GetArrayLength()!=1||types[0].GetString()!="code"))
            return Error("invalid_client_metadata");
        if(_registrationTimes.Count>=10)return Error("temporarily_unavailable",429);
        _registrationTimes.Add(DateTimeOffset.UtcNow);
        if(_store.Clients.Count>=64)
        {
            var oldest=_store.Clients.Where(item=>!item.Value.Approved)
                .OrderBy(item=>item.Value.Created).FirstOrDefault();
            if(oldest.Key is null)return Error("temporarily_unavailable",429);
            _store.Clients.Remove(oldest.Key);
        }
        var id=Random();
        var displayName="MCP client";
        if(obj.TryGetProperty("client_name",out var name)&&name.ValueKind==JsonValueKind.String)
            displayName=(name.GetString()??displayName)[..Math.Min(100,(name.GetString()??displayName).Length)];
        _store.Clients[id]=new Client{Redirects=redirects,Name=displayName,
            Created=DateTimeOffset.UtcNow};
        return Json(new {
            client_id=id,client_id_issued_at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            redirect_uris=redirects, token_endpoint_auth_method="none",
            grant_types=new[]{"authorization_code","refresh_token"},response_types=new[]{"code"}
        },201);
    }

    private Response GetAuthorizationPage(IReadOnlyDictionary<string,string> args)
    {
        if(!args.TryGetValue("response_type",out var response)||response!="code"||
            args.GetValueOrDefault("resource")!=Resource||
            args.GetValueOrDefault("code_challenge_method")!="S256"||
            !args.TryGetValue("code_challenge",out var challenge) ||
            challenge.Length!=43||!Regex.IsMatch(challenge,@"\A[A-Za-z0-9_-]{43}\z")||
            !args.TryGetValue("client_id",out var clientID)||
            !_store.Clients.TryGetValue(clientID,out var client)||
            !args.TryGetValue("redirect_uri",out var redirect)||
            !client.Redirects.Contains(redirect,StringComparer.Ordinal)||
            args.GetValueOrDefault("scope","mcp")!="mcp"||
            _requests.Count>=16)return Error("invalid_request");
        var state=args.GetValueOrDefault("state","");
        if(Encoding.UTF8.GetByteCount(state)>1024)return Error("invalid_request");
        var requestID=Random();
        _requests[requestID]=new Authorization(clientID,redirect,challenge,state,
            DateTimeOffset.UtcNow.AddMinutes(5));
        return Page(requestID,client.Name,redirect);
    }

    private Response Approve(IReadOnlyDictionary<string,string> form)
    {
        if(_loginAttempts.Count>=5)return Page(message:"Too many attempts; start again later.",status:429);
        _loginAttempts.Add(DateTimeOffset.UtcNow);
        var id=form.GetValueOrDefault("request_id","");
        if(!_requests.TryGetValue(id,out var request)||
           request.Expires<=DateTimeOffset.UtcNow||
           !_store.Clients.TryGetValue(request.Client,out var client))
            return Page(message:"Request expired; start the connection again.",status:403);
        if(!Equal(form.GetValueOrDefault("pairing_code","").Trim(),_pairingCode))
            return Page(id,client.Name,request.Redirect,
                "Login code did not match; copy the current local login code.",403);
        var scope=form.GetValueOrDefault("workspace_scope","scope:default");
        string[] approved;
        if(scope=="scope:default") approved=["default"];
        else if(scope=="scope:all")approved=["default",.._approved.Select(x=>x.Id)];
        else if(scope.StartsWith("scope:",StringComparison.Ordinal) &&
                _approved.Any(x=>"scope:"+x.Id==scope))
            approved=["default",scope[6..]];
        else return Page(id,client.Name,request.Redirect,
                "Choose an approved workspace access scope.",403);
        var before=CloneStore();
        client.Approved=true;client.Workspaces=approved;
        _store.Access=RemoveClient(_store.Access,request.Client);
        _store.Refresh=RemoveClient(_store.Refresh,request.Client);
        _store.ConsumedRefresh=RemoveClient(_store.ConsumedRefresh,request.Client);
        try {Persist();}
        catch {_store=before;return Error("server_error",500);}
        _requests.Remove(id);
        var code=Random();
        _codes[Hash(code)]=request with{Expires=DateTimeOffset.UtcNow.AddMinutes(1)};
        var uri=new Uri(request.Redirect);
        var prefix=uri.GetLeftPart(UriPartial.Path)+
            (string.IsNullOrEmpty(uri.Query)?"?":uri.Query+"&");
        var location=prefix+"code="+Uri.EscapeDataString(code)+
            "&state="+Uri.EscapeDataString(request.State)+
            "&iss="+Uri.EscapeDataString(Issuer);
        return new Response(302,"text/plain",[],new Dictionary<string,string>{
            ["Location"]=location
        });
    }

    private Response RevokeToken(IReadOnlyDictionary<string,string> form)
    {
        if(!form.TryGetValue("token",out var token)||
           !form.TryGetValue("client_id",out var client))return Error("invalid_request");
        var digest=Hash(token);
        if((_store.Access.TryGetValue(digest,out var grant)||
            _store.Refresh.TryGetValue(digest,out grant))&&
            grant.Client==client)
        {
            var before=CloneStore();
            RevokeFamily(grant.Family);
            try{Persist();}catch{_store=before;return Error("server_error",500);}
        }
        return Json(new{});
    }

    private Response IssueToken(IReadOnlyDictionary<string,string> form)
    {
        var clientID=form.GetValueOrDefault("client_id","");
        if(form.GetValueOrDefault("resource")!=Resource||
           !_store.Clients.TryGetValue(clientID,out var record)||!record.Approved)
            return Error("invalid_grant");
        var before=CloneStore();
        string family;
        if(form.GetValueOrDefault("grant_type")=="authorization_code")
        {
            var digest=Hash(form.GetValueOrDefault("code",""));
            if(!_codes.Remove(digest,out var authorization) ||
               authorization.Expires<=DateTimeOffset.UtcNow ||
               authorization.Client!=clientID ||
               authorization.Redirect!=form.GetValueOrDefault("redirect_uri"))
                return Error("invalid_grant");
            var verifier=form.GetValueOrDefault("code_verifier","");
            if(verifier.Length is < 43 or > 128||
               !Regex.IsMatch(verifier,@"\A[A-Za-z0-9._~-]+\z")||
               !Equal(Base64URL(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))),
                      authorization.Challenge))
                return Error("invalid_grant");
            family=Random();
        }
        else if(form.GetValueOrDefault("grant_type")=="refresh_token")
        {
            var digest=Hash(form.GetValueOrDefault("refresh_token",""));
            if(_store.ConsumedRefresh.TryGetValue(digest,out var consumed)&&
               consumed.Client==clientID)
            {
                RevokeFamily(consumed.Family);
                try{Persist();}catch{_store=before;return Error("server_error",500);}
                return Error("invalid_grant");
            }
            if(!_store.Refresh.TryGetValue(digest,out var grant)||
               grant.Client!=clientID||grant.Expires<=DateTimeOffset.UtcNow||
               _store.ConsumedRefresh.Count>=512)return Error("invalid_grant");
            _store.Refresh.Remove(digest);
            family=grant.Family;
            _store.Access=_store.Access.Where(x=>x.Value.Family!=family)
                .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            _store.ConsumedRefresh[digest]=grant;
        }
        else return Error("unsupported_grant_type");
        if(_store.Access.Count>=128||_store.Refresh.Count>=128)
        {
            _store=before;return Error("temporarily_unavailable",429);
        }
        var access=Random();var refresh=Random();
        _store.Access[Hash(access)]=new Grant{Client=clientID,Family=family,
            Expires=DateTimeOffset.UtcNow.AddHours(1)};
        _store.Refresh[Hash(refresh)]=new Grant{Client=clientID,Family=family,
            Expires=DateTimeOffset.UtcNow.AddDays(7)};
        try{Persist();}catch{_store=before;return Error("server_error",500);}
        return Json(new{access_token=access,token_type="Bearer",
            expires_in=3600,refresh_token=refresh,scope="mcp"});
    }

    private static Dictionary<string, Grant> RemoveClient(
        Dictionary<string, Grant> entries, string clientID) =>
        entries.Where(entry => entry.Value.Client != clientID)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private void RevokeFamily(string family)
    {
        _store.Access=_store.Access.Where(x=>x.Value.Family!=family)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        _store.Refresh=_store.Refresh.Where(x=>x.Value.Family!=family)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
    }
    private void Prune()
    {
        var now=DateTimeOffset.UtcNow;
        foreach(var item in _requests.Where(x=>x.Value.Expires<=now).ToArray())_requests.Remove(item.Key);
        foreach(var item in _codes.Where(x=>x.Value.Expires<=now).ToArray())_codes.Remove(item.Key);
        _store.Clients=_store.Clients.Where(x=>x.Value.Approved||(now-x.Value.Created).TotalMinutes<10)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        _store.Access=_store.Access.Where(x=>x.Value.Expires>now)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        _store.Refresh=_store.Refresh.Where(x=>x.Value.Expires>now)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        _store.ConsumedRefresh=_store.ConsumedRefresh.Where(x=>x.Value.Expires>now)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        _loginAttempts.RemoveAll(time=>(now-time).TotalSeconds>60);
        _registrationTimes.RemoveAll(time=>(now-time).TotalSeconds>60);
    }

    private Store CloneStore() =>
        JsonSerializer.Deserialize<Store>(JsonSerializer.Serialize(_store,Serializer),Serializer)!;

    private void Persist()
    {
        RefuseLink(_file);
        var durable=CloneStore();
        durable.Clients=durable.Clients.Where(x=>x.Value.Approved)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        var temp=_file+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp,JsonSerializer.Serialize(durable,Serializer));
            File.Move(temp,_file,true);
        }
        finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
    }

    private static void EnsurePrivateDirectory(string folder)
    {
        for(var current=folder;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
        {
            if(Directory.Exists(current) &&
               (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new FileMcpException("OAuth state directory may not be a junction");
            if(Path.GetPathRoot(current)?.Equals(current,StringComparison.OrdinalIgnoreCase)==true)
                break;
        }
        Directory.CreateDirectory(folder);
        if(!OperatingSystem.IsWindows())return;
        var account=WindowsIdentity.GetCurrent().User ??
            throw new FileMcpException("No Windows SID available");
        var rules=new DirectorySecurity();
        rules.SetAccessRuleProtection(true,false);
        rules.AddAccessRule(new FileSystemAccessRule(account,FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,
            PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(rules);
    }
    private static void RefuseLink(string filename)
    {
        try
        {
            if((File.GetAttributes(filename)&FileAttributes.ReparsePoint)!=0)
                throw new FileMcpException("OAuth state file may not be a link");
        }
        catch(FileNotFoundException){}
        catch(DirectoryNotFoundException){}
    }

    private Response Page(string requestID="",string clientName="Connection request",
        string redirect="",string? message=null,int status=200)
    {
        var options=string.Concat(_approved.Select(item=>
            "<label><input type='radio' name='workspace_scope' value='scope:"+
            WebUtility.HtmlEncode(item.Id)+"'>"+WebUtility.HtmlEncode(item.Name)+"</label>"));
        if(_approved.Length>0) options+=
            "<label><input type='radio' name='workspace_scope' value='scope:all'>All approved workspaces</label>";
        var form=string.IsNullOrEmpty(requestID)?"": "<form method='post' action='/oauth/authorize'>"+
            "<input type='hidden' name='request_id' value='"+WebUtility.HtmlEncode(requestID)+"'>"+
            "<label>Login code<input type='password' name='pairing_code' autocomplete='off' required></label>"+
            "<fieldset><legend>Approved workspaces</legend>"+
            "<label><input type='radio' name='workspace_scope' value='scope:default' checked>"+
            "Default workspace only (recommended)</label>"+options+"</fieldset>"+
            "<button>Authorize connection</button></form>";
        var nonce=Random();
        var alert=message is null?"":"<p role='alert'>"+WebUtility.HtmlEncode(message)+"</p>";
        var html="<!doctype html><html lang='en'><head><meta charset='utf-8'>"+
            "<meta name='viewport' content='width=device-width,initial-scale=1'>"+
            "<title>Connect to ChatToComputerMCP</title>"+
            "<style nonce='"+nonce+"'>body{font-family:system-ui,sans-serif;margin:4rem auto;max-width:30rem;"+
            "padding:1rem;background:#fafafa;color:#151515}main{background:white;padding:2rem;"+
            "border:1px solid #ddd;border-radius:1rem}label{display:block;margin:1rem 0}"+
            "input[type=password]{display:block;width:100%;padding:.7rem}"+
            "button{width:100%;padding:.8rem}fieldset{border:1px solid #ddd;border-radius:.4rem}"+
            "</style></head><body><main><h1>Authorize your computer connection</h1>"+
            "<p>Client-provided name (NOT a trusted identity): "+
            WebUtility.HtmlEncode(clientName)+"</p><p>"+
            WebUtility.HtmlEncode(_permissionDescription)+"</p>"+alert+form+
            "<p>Only authorize connections you started. Never share your local login code.</p>"+
            "<small>Callback: "+WebUtility.HtmlEncode(redirect)+"</small></main></body></html>";
        var destination = Uri.TryCreate(redirect,UriKind.Absolute,out var callback)
            ?callback.GetLeftPart(UriPartial.Authority):"";
        return new Response(status,"text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(html),
            new Dictionary<string,string>{
                ["Content-Security-Policy"]="default-src 'none'; style-src 'nonce-"+nonce+
                    "'; form-action 'self'"+(destination.Length>0?" "+destination:"")+
                    "; frame-ancestors 'none'; base-uri 'none'",
                ["Referrer-Policy"]="same-origin",
                ["X-Frame-Options"]="DENY"
            });
    }
    private static Response Json(object value,int status=200)=>
        new(status,"application/json",Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
    private static Response Error(string code,int status=400)=>
        Json(new{error=code},status);
    private static string Random()=>Convert.ToHexString(
        RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string value)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Base64URL(byte[] value)=>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static bool Equal(string a,string b)
    {
        var x=Encoding.UTF8.GetBytes(a);var y=Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(x,y);
    }
    private static Dictionary<string,string> ParseForm(string query)
    {
        if(Encoding.UTF8.GetByteCount(query)>16_384)
            throw new FileMcpException("OAuth form exceeds limit");
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var part in query.Split('&',StringSplitOptions.RemoveEmptyEntries))
        {
            var at=part.IndexOf('=');
            if(at<1)throw new FileMcpException("Invalid OAuth form field");
            var rawName=part[..at];var rawValue=part[(at+1)..];
            if(Regex.IsMatch(rawName+rawValue,@"%(?![0-9A-Fa-f]{2})"))
                throw new FileMcpException("Malformed URL encoding");
            var name=Uri.UnescapeDataString(rawName.Replace('+',' '));
            var value=Uri.UnescapeDataString(rawValue.Replace('+',' '));
            if(string.IsNullOrEmpty(name)||!result.TryAdd(name,value))
                throw new FileMcpException("Duplicate or invalid OAuth parameter");
        }
        return result;
    }
    private static bool ValidRedirect(string value)
    {
        if(Encoding.UTF8.GetByteCount(value)>2048 ||
           !Uri.TryCreate(value,UriKind.Absolute,out var uri) ||
           uri.UserInfo.Length>0||uri.Fragment.Length>0||
           value.Any(ch=>ch<' '||ch==127))return false;
        return uri.Scheme=="https" ||
            (uri.Scheme=="http" && uri.Host is ("localhost" or "127.0.0.1" or "::1"));
    }
}
