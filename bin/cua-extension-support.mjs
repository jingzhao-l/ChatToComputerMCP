// SPDX-License-Identifier: Apache-2.0
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import cp from 'node:child_process';
import readline from 'node:readline';
import {fileURLToPath} from 'node:url';

export const extensionHelp=[
  'cua extension-install              Install pinned Playwright and headless Chromium',
  'cua browser-grant --profile <id> --origin <origin> --client <id> (repeat --origin; optional --allow-files)',
  'cua guest-enable                   Manually enable optional independent Guest (interactive local confirmation)',
  'cua guest-disable                  Disable Guest access and revoke all Guest leases',
  'cua guest-status                   Report the local Guest master switch',
  'cua guest-init --port <port> --confirm-isolated-desktop (inside a dedicated guest)',
  'cua guest-serve                    Run CUA-only guest listener inside that guest',
  'cua relay-init --domain <id> --port <listener-port> --guest-port <forwarded-port> --token-file <private-guest-token> --confirm-dedicated-guest',
  'cua relay-serve --domain <id>       Run an independent guest-only relay listener',
  'cua relay-status --domain <id>      Inspect relay configuration (never print token contents)',
  'cua guest-grant --profile <id> --port <forwarded-port> --token-file <file> --client <id>',
  'cua profile-revoke --profile <id> --client <id>',
  'cua vm status|start|stop --profile <id> (registered Lume/Hyper-V VM)',
  'cua vm provision --provider lume --template <existing-template> --name <new-name>',
  'cua vm provision --provider hyper-v --template <prepared.vhdx> --name <new-name> --destination <existing-dir> --switch <existing-switch>',
  'cua guest-connect --profile <id> --host <host> --user <user> --guest-port <port> --known-hosts <file> --identity <file> (foreground SSH)'
];
export function guestApprovalFile(root){return path.join(root,'cua-guest-approval')}
export function guestEpoch(root){
  try{
    const file=guestApprovalFile(root),st=fs.lstatSync(file);
    if(!st.isFile()||st.isSymbolicLink()||st.size!==65||process.platform!=='win32'&&(st.uid!==process.getuid()||(st.mode&0o077)!==0))return null;
    const value=fs.readFileSync(file,'utf8');
    return /^[a-f0-9]{64}\n$/.test(value)?value.slice(0,64):null;
  }catch{return null}
}
export function requireGuestEnabled(root){
  const epoch=guestEpoch(root);
  if(!epoch)throw Error('Independent Guest is disabled. The user must run cua guest-enable in a local interactive terminal first.');
  return epoch;
}
function guestEnable(args,h){
  if(args.length)throw Error('guest-enable takes no arguments');
  if(!process.stdin.isTTY||!process.stdout.isTTY)throw Error('Guest activation requires an interactive local terminal; non-interactive commands are refused.');
  if(guestEpoch(h.root())){console.log('Independent Guest is already enabled.');return}
  const terminal=readline.createInterface({input:process.stdin,output:process.stdout,terminal:false});
  let answered=false;
  terminal.once('close',()=>{process.stdin.pause();if(!answered){console.error('Guest activation cancelled.');process.exitCode=1}});
  terminal.once('SIGINT',()=>terminal.close());
  terminal.question('Enable independent Guest on this computer? Native background CUA remains preferred. Type ENABLE to confirm: ',answer=>{
    answered=true;
    try{
      if(answer.trim()!=='ENABLE')throw Error('Guest activation cancelled.');
      h.writePrivate(guestApprovalFile(h.root()),crypto.randomBytes(32).toString('hex')+'\n');
      console.log('Independent Guest enabled by local confirmation. Existing grants still apply; no VM or listener was started.');
    }catch(e){console.error('ChatToComputerMCP: '+e.message);process.exitCode=1}
    finally{terminal.close()}
  });
}
function guestDisable(args,h){
  if(args.length)throw Error('guest-disable takes no arguments');
  fs.rmSync(guestApprovalFile(h.root()),{force:true});
  console.log('Independent Guest disabled. Guest leases are revoked; managed guest listeners and forwards close. Native CUA is unaffected. External VMs are not powered off.');
}
function guestStatus(args,h){
  if(args.length)throw Error('guest-status takes no arguments');
  console.log(JSON.stringify({enabled:guestEpoch(h.root())!==null,defaultEnabled:false,preferredExecution:'native_background'},null,2));
}
const source=path.join(path.dirname(fileURLToPath(import.meta.url)),'..','cua-runtime');
function dir(h){return path.join(h.root(),'cua-extension','1.0.0-'+h.hashFile(path.join(source,'worker.mjs')).slice(0,16))}
function runtime(h){
  const d=dir(h),worker=fs.realpathSync(path.join(d,'worker.mjs')),node=fs.realpathSync(process.execPath);
  const marker=JSON.parse(fs.readFileSync(path.join(d,'runtime.json'),'utf8'));
  if(marker.workerSHA256!==h.hashFile(path.join(source,'worker.mjs'))||marker.workerSHA256!==h.hashFile(worker)||marker.nodeSHA256!==h.hashFile(node))
    throw Error('CUA extension runtime integrity mismatch; reinstall explicitly');
  return {node,nodeSHA256:marker.nodeSHA256,worker,workerSHA256:marker.workerSHA256};
}
function install(args,h){
  if(args.length)throw Error('extension-install takes no arguments');
  const destination=dir(h);
  if(fs.existsSync(destination)){runtime(h);console.log('CUA extension already installed.');return;}
  fs.mkdirSync(path.dirname(destination),{recursive:true,mode:0o700});
  const stage=fs.mkdtempSync(path.join(path.dirname(destination),'.extension-'));
  try{
    for(const f of ['package.json','package-lock.json','worker.mjs'])fs.copyFileSync(path.join(source,f),path.join(stage,f));
    const env={...process.env,PLAYWRIGHT_BROWSERS_PATH:path.join(stage,'browsers')};
    h.run(process.platform==='win32'?'npm.cmd':'npm',['ci','--ignore-scripts','--no-audit','--no-fund','--registry','https://registry.npmjs.org/'],
      {cwd:stage,env,shell:process.platform==='win32'});
    h.run(process.execPath,[path.join(stage,'node_modules','playwright','cli.js'),'install','chromium'],{cwd:stage,env});
    fs.writeFileSync(path.join(stage,'runtime.json'),JSON.stringify({workerSHA256:h.hashFile(path.join(stage,'worker.mjs')),nodeSHA256:h.hashFile(fs.realpathSync(process.execPath))})+'\n',{mode:0o600,flag:'wx'});
    fs.renameSync(stage,destination);
    runtime(h);
    console.log('Pinned headless Chromium installed. No remote grants were created.');
  }finally{fs.rmSync(stage,{recursive:true,force:true});}
}
function options(args,allowed){
  const o={};
  for(let i=0;i<args.length;i+=2){
    const k=args[i],v=args[i+1];
    if(!allowed.includes(k)||!v||(k!=='--origin'&&Object.hasOwn(o,k)))throw Error('Invalid option: '+k);
    if(k==='--origin'){o[k]??=[];o[k].push(v)}else o[k]=v;
  }
  return o;
}
function id(v){if(typeof v!=='string'||!/^[A-Za-z0-9_-]{1,48}$/.test(v))throw Error('Invalid profile ID');return v}
function client(v){if(typeof v!=='string'||!/^[A-Za-z0-9._-]{1,128}$/.test(v))throw Error('Explicit client required');return v}
function port(v){if(!/^\d{4,5}$/.test(v??'')||Number(v)<1024||Number(v)>65535)throw Error('Invalid port');return Number(v)}
function exactOrigin(v){
  const u=new URL(v);
  if(!['http:','https:'].includes(u.protocol)||u.username||u.password||u.pathname!=='/'||u.search||u.hash)
    throw Error('Only exact HTTP(S) origins are allowed');
  return u.origin;
}
function saveProfile(profile,h){
  const p=h.readPolicy()??{grants:[]},existing=(p.profiles??[]).find(x=>x.id===profile.id);
  if(p.requireVerifiedClient===true&&profile.clients.includes('shared-connection'))throw Error('Verified-client-only mode refuses shared profile grants');
  if(existing){
    const {clients:a,...old}=existing,{clients:b,...next}=profile;
    if(JSON.stringify(old)!==JSON.stringify(next))throw Error('Revoke existing profile before changing its scope');
    profile.clients=[...new Set([...a,...b])];
  }
  const profiles=[...(p.profiles??[]).filter(x=>x.id!==profile.id),profile];
  if(profiles.length>32)throw Error('At most 32 CUA extension profiles');
  h.writePolicy({...p,...h.installedDriver(),extensionRuntime:runtime(h),profiles,enabled:true});
  console.log('Local profile grant saved; all previous leases are invalidated.');
}
function browserGrant(args,h){
  const allowFiles=args.includes('--allow-files');
  const o=options(args.filter(a=>a!=='--allow-files'),['--profile','--client','--origin']);
  const origins=[...new Set((o['--origin']??[]).map(exactOrigin))].sort();
  if(!origins.length||origins.length>32)throw Error('One to 32 exact origins required');
  saveProfile({id:id(o['--profile']),kind:'browser',origins,allowFiles,clients:[client(o['--client'])]},h);
}
function guestGrant(args,h){
  requireGuestEnabled(h.root());
  const o=options(args,['--profile','--client','--port','--token-file','--vm-provider','--vm-name']);
  const profile=id(o['--profile']),p=port(o['--port']);
  // Explicitly supplied credential file, created inside the dedicated guest.
  // Do not inspect unrelated files or print credential contents.
  if(!o['--token-file'])throw Error('Specify the dedicated guest credential file');
  if(fs.lstatSync(o['--token-file']).isSymbolicLink())throw Error('Guest credential cannot be a symlink');
  const tokenFile=fs.realpathSync(o['--token-file']);
  const stat=fs.lstatSync(tokenFile);
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size!==65||process.platform!=='win32'&&(stat.mode&0o077)!==0)
    throw Error('Guest credential must be a private regular file created by guest-init');
  const digest=guestDigestFor(tokenFile);
  if(relayRecords(h).some(x=>x.guestPort===p||x.port===p||x.guestDigest===digest))throw Error('Guest is reserved for a dedicated relay execution domain');
  const existing=h.readPolicy()?.profiles??[];
  if(existing.some(x=>x.kind==='guest'&&x.port===p&&x.id!==profile))throw Error('Guest endpoint is already registered');
  const grant={id:profile,kind:'guest',port:p,tokenFile,clients:[client(o['--client'])]};
  if(o['--vm-provider']||o['--vm-name']){
    if(!['lume','hyper-v'].includes(o['--vm-provider'])||!/^[A-Za-z0-9][A-Za-z0-9_. -]{0,79}$/.test(o['--vm-name']??''))
      throw Error('Specify exact existing VM name and supported provider');
    grant.vmProvider=o['--vm-provider'];grant.vmName=o['--vm-name'];
  }
  saveProfile(grant,h);
}
function guestInit(args,h){
  requireGuestEnabled(h.root());
  if(!args.includes('--confirm-isolated-desktop'))throw Error('Explicit confirmation of an isolated guest desktop is required');
  const o=options(args.filter(x=>x!=='--confirm-isolated-desktop'),['--port']);
  const p=port(o['--port']??'18765'),configuration=path.join(h.root(),'cua-guest.json'),credential=path.join(h.root(),'cua-guest-token');
  if(fs.existsSync(configuration)||fs.existsSync(credential))throw Error('Guest already initialized; existing credentials are preserved');
  h.writePrivate(credential,crypto.randomBytes(32).toString('hex')+'\n');
  try{h.writePrivate(configuration,JSON.stringify({isolatedDesktopConfirmed:true,port:p})+'\n')}
  catch(e){fs.rmSync(credential,{force:true});throw e}
  console.log('Dedicated guest configured. Keep its credential file private.');
}
function relayRecords(h){
  const parent=path.join(h.root(),'cua-relays');
  if(!fs.existsSync(parent))return [];
  if(fs.lstatSync(parent).isSymbolicLink())throw Error('Unsafe relay registry directory');
  return fs.readdirSync(parent,{withFileTypes:true}).filter(item=>item.name!=='.registry-lock').map(item=>{
    if(!item.isDirectory()||item.isSymbolicLink())throw Error('Unsafe relay registry entry');
    const file=path.join(parent,item.name,'relay.json');
    const info=fs.lstatSync(file);
    if(!info.isFile()||info.isSymbolicLink()||info.size>4096)throw Error('Unsafe relay registry configuration');
    const record=JSON.parse(fs.readFileSync(file,'utf8'));
    if(record.domain!==item.name||record.dedicatedGuestConfirmed!==true||!Number.isInteger(record.port)||!Number.isInteger(record.guestPort)||
        !/^[a-f0-9]{64}$/.test(record.guestDigest??''))throw Error('Invalid relay registry configuration');
    return record;
  });
}
function guestDigestFor(file){
  const info=fs.lstatSync(file);
  if(!info.isFile()||info.isSymbolicLink()||info.size!==65)throw Error('Invalid dedicated guest token file');
  const bytes=fs.readFileSync(file);
  if(!/^[a-f0-9]{64}\n$/.test(bytes.toString('utf8')))throw Error('Invalid dedicated guest credential format');
  return crypto.createHash('sha256').update(bytes).digest('hex');
}
function relayDirectory(h,domain){
  return path.join(h.root(),'cua-relays',id(domain));
}
function relayInit(args,h){
  requireGuestEnabled(h.root());
  if(!args.includes('--confirm-dedicated-guest'))throw Error('Confirm a dedicated guest not shared with other execution domains');
  const o=options(args.filter(x=>x!=='--confirm-dedicated-guest'),['--domain','--port','--guest-port','--token-file']);
  const domain=id(o['--domain']),listener=port(o['--port']),guestPort=port(o['--guest-port']);
  if(listener===guestPort)throw Error('Listener and guest forwarding ports must differ');
  const tokenFile=regularFile(o['--token-file']);
  const st=fs.lstatSync(tokenFile);
  if(st.size!==65||process.platform!=='win32'&&((st.mode&0o077)!==0||st.uid!==process.getuid()))
    throw Error('Use a private dedicated guest credential file');
  const guestDigest=guestDigestFor(tokenFile);
  const parent=path.join(h.root(),'cua-relays');
  fs.mkdirSync(parent,{recursive:true,mode:0o700});
  const lock=path.join(parent,'.registry-lock');
  fs.mkdirSync(lock,{mode:0o700});
  let created=false;
  const destination=relayDirectory(h,domain);
  try{
    for(const old of relayRecords(h)){
      if(old.port===listener||old.port===guestPort||old.guestPort===listener||old.guestPort===guestPort||old.guestDigest===guestDigest)
        throw Error('Each execution domain requires distinct ports and a distinct dedicated guest credential');
    }
    const ordinary=h.readPolicy();
    if((ordinary?.profiles??[]).some(p=>p.kind==='guest'&&(p.port===guestPort||p.port===listener||p.tokenFile===tokenFile||
        guestDigestFor(p.tokenFile)===guestDigest)))
      throw Error('Guest is already registered with the ordinary gateway; use a different dedicated guest');
    fs.mkdirSync(destination,{mode:0o700});created=true;
    h.writePrivate(path.join(destination,'relay-token'),crypto.randomBytes(32).toString('hex')+'\n');
    h.writePrivate(path.join(destination,'cua-policy.json'),JSON.stringify({
      enabled:true,...h.installedDriver(),extensionRuntime:runtime(h),grants:[],
      profiles:[{id:domain,kind:'guest',port:guestPort,tokenFile,clients:['shared-connection']}]
    })+'\n');
    h.writePrivate(path.join(destination,'relay.json'),JSON.stringify({domain,port:listener,guestPort,guestDigest,dedicatedGuestConfirmed:true})+'\n');
    console.log('Dedicated relay configured. Use a distinct tunnel/connector for this domain; do not share its credential.');
    console.log('Private credential file: '+path.join(destination,'relay-token'));
  }catch(e){if(created)fs.rmSync(destination,{recursive:true,force:true});throw e}
  finally{fs.rmdirSync(lock)}
}
function relayStatus(args,h){
  const o=options(args,['--domain']),domain=id(o['--domain']);
  const config=JSON.parse(fs.readFileSync(path.join(relayDirectory(h,domain),'relay.json'),'utf8'));
  console.log(JSON.stringify({domain,port:config.port,guestPort:config.guestPort,credential:'private_file',relayOnly:true,guestEnabled:guestEpoch(h.root())!==null,conversationAuthenticated:false},null,2));
}
function revoke(args,h){
  const o=options(args,['--profile','--client']),p=h.readPolicy();
  const profile=id(o['--profile']),owner=client(o['--client']);
  if(!p)return;
  p.profiles=(p.profiles??[]).map(x=>x.id===profile?{...x,clients:x.clients.filter(c=>c!==owner)}:x).filter(x=>x.clients.length);
  p.enabled=p.grants.length>0||p.profiles.length>0;
  h.writePolicy(p);
  console.log('Profile grant revoked; current leases will be invalidated.');
}
function vmName(v){if(typeof v!=='string'||!/^[A-Za-z0-9][A-Za-z0-9_. -]{0,79}$/.test(v))throw Error('Invalid exact VM name');return v}
function regularFile(v){
  if(!v||!path.isAbsolute(v)||!fs.lstatSync(v).isFile()||fs.lstatSync(v).isSymbolicLink())throw Error('Explicit absolute regular file required');
  return fs.realpathSync(v);
}
export function guestConnectPlan(args,profile,platform=process.platform){
  const o=options(args,['--profile','--host','--user','--guest-port','--known-hosts','--identity']);
  if(!profile||profile.kind!=='guest'||profile.id!==id(o['--profile']))throw Error('Registered guest profile required');
  const host=o['--host'],user=o['--user'];
  if(!/^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$/.test(host??'')||! /^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$/.test(user??''))throw Error('Explicit DNS/IPv4 host and SSH username required');
  const known=regularFile(o['--known-hosts']),identity=regularFile(o['--identity']);
  if(/[\r\n"]/u.test(known)||/[\r\n"]/u.test(identity))throw Error('Unsupported SSH path characters');
  return {exe:platform==='win32'?'ssh.exe':'/usr/bin/ssh',args:['-F','none','-N','-T','-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','IdentityAgent=none','-o','ForwardAgent=no','-o','ForwardX11=no','-o','StrictHostKeyChecking=yes','-o','UpdateHostKeys=no','-o','GlobalKnownHostsFile=none','-o','UserKnownHostsFile="'+known+'"','-o','ExitOnForwardFailure=yes','-o','ServerAliveInterval=15','-o','ServerAliveCountMax=3','-i',identity,'-L','127.0.0.1:'+port(String(profile.port))+':127.0.0.1:'+port(o['--guest-port']),'-l',user,host]};
}
function guestConnect(args,h){
  const epoch=requireGuestEnabled(h.root());
  const o=options(args,['--profile','--host','--user','--guest-port','--known-hosts','--identity']);
  const p=h.readPolicy()?.profiles?.find(x=>x.id===id(o['--profile']));
  const plan=guestConnectPlan(args,p);
  console.log('Starting pinned-host SSH forwarding on loopback. Keep this terminal open; interrupt to close.');
  // Own only this SSH process. Revocation stops this forward, not other
  // SSH sessions, and re-enabling never restarts it automatically.
  const child=cp.spawn(plan.exe,plan.args,{stdio:'inherit',windowsHide:true});
  let stopping=false,killTimer;
  const stop=()=>{if(stopping)return;stopping=true;child.kill();killTimer=setTimeout(()=>child.kill('SIGKILL'),2000);killTimer.unref()};
  const monitor=setInterval(()=>{if(guestEpoch(h.root())!==epoch)stop()},250);
  const cleanup=()=>{clearInterval(monitor);clearTimeout(killTimer);process.removeListener('SIGINT',stop);process.removeListener('SIGTERM',stop)};
  process.once('SIGINT',stop);process.once('SIGTERM',stop);
  child.on('error',err=>{cleanup();console.error('Guest forward failed: '+err.message);process.exitCode=1});
  child.on('exit',code=>{cleanup();if(!stopping&&code!==0)process.exitCode=1});

}
export function vmProvisionPlan(args,platform=process.platform){
  const o=options(args,['--provider','--template','--name','--destination','--switch']);
  const name=vmName(o['--name']);
  if(o['--provider']==='lume'&&platform==='darwin'){
    const template=vmName(o['--template']);
    if(template===name||o['--destination']||o['--switch'])throw Error('Lume requires a distinct local template and new VM name');
    return [{exe:'lume',args:['get',template]},{exe:'lume',args:['clone',template,name]}];
  }
  if(o['--provider']==='hyper-v'&&platform==='win32'){
    if(/[. ]$/.test(name)||/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(name))throw Error('Invalid Windows VM destination name');
    const template=regularFile(o['--template']);
    if(!/\.vhdx$/i.test(template))throw Error('Prepared standalone VHDX required');
    const root=o['--destination'];
    if(!root||!path.isAbsolute(root)||!fs.lstatSync(root).isDirectory()||fs.lstatSync(root).isSymbolicLink())throw Error('Explicit existing VM destination directory required');
    const destination=path.join(fs.realpathSync(root),name);
    if(fs.existsSync(destination))throw Error('VM destination already exists');
    const sw=vmName(o['--switch']);
    const script=[
      '$ErrorActionPreference="Stop"',
      '$name=$env:CTCMCP_VM_NAME; $source=$env:CTCMCP_VM_TEMPLATE; $dest=$env:CTCMCP_VM_DEST; $switch=$env:CTCMCP_VM_SWITCH',
      'if (@(Get-VM | Where-Object {$_.Name -ieq $name}).Count -ne 0) {throw "VM name already exists"}',
      'if (@(Get-VMSwitch | Where-Object {$_.Name -ceq $switch}).Count -ne 1) {throw "Exact virtual switch required"}',
      '$vhd=Get-VHD -Path $source; if ($vhd.Attached -or $vhd.ParentPath) {throw "Template must be detached and standalone"}',
      'if (Test-Path -LiteralPath $dest) {throw "Destination already exists"}',
      'New-Item -ItemType Directory -Path $dest | Out-Null',
      '$disk=Join-Path $dest "guest.vhdx"; [IO.File]::Copy($source,$disk,$false)',
      'New-VM -Name $name -Generation 2 -MemoryStartupBytes 4GB -VHDPath $disk -Path $dest -SwitchName $switch -ErrorAction Stop | Out-Null',
      'Set-VMProcessor -VMName $name -Count 2 -ErrorAction Stop',
      'Get-VM -Name $name | Select-Object Name,State,Id | ConvertTo-Json'
    ].join('; ');
    return [{exe:'powershell.exe',args:['-NoProfile','-NonInteractive','-Command',script],env:{CTCMCP_VM_NAME:name,CTCMCP_VM_TEMPLATE:template,CTCMCP_VM_DEST:destination,CTCMCP_VM_SWITCH:sw}}];
  }
  throw Error('VM provider not supported on this host');
}
function vm(args,h){
  const [action,...rest]=args;
  if(action==='provision'){
    requireGuestEnabled(h.root());
    for(const plan of vmProvisionPlan(rest))h.run(plan.exe,plan.args,{timeout:1800000,...(plan.env?{env:{...process.env,...plan.env}}:{})});
    console.log('VM provisioned from local template. Set up unique guest identity and guest CUA service before registering it.');
    return;
  }
  if(!['status','start','stop'].includes(action))throw Error('Use cua vm status|start|stop --profile <id>');
  if(action==='start')requireGuestEnabled(h.root());
  const o=options(rest,['--profile']),profile=id(o['--profile']);
  const p=h.readPolicy()?.profiles?.find(x=>x.id===profile&&x.kind==='guest');
  if(!p||!/^[A-Za-z0-9][A-Za-z0-9_. -]{0,79}$/.test(p.vmName??''))throw Error('No registered VM');
  if(p.vmProvider==='lume'&&process.platform==='darwin'){
    // Resolve an existing VM first, never pull an image from an arbitrary name.
    h.run('lume',['get',p.vmName]);
    if(action==='start')h.run('lume',['run',p.vmName,'--detach','--display','none']);
    if(action==='stop')h.run('lume',['stop',p.vmName]);
  }else if(p.vmProvider==='hyper-v'&&process.platform==='win32'){
    const script='$ErrorActionPreference="Stop"; $vm=Get-VM | Where-Object {$_.Name -ceq $env:CTCMCP_VM_NAME}; if(@($vm).Count -ne 1){throw "VM not found"}; '+
      (action==='status'?'$vm | Select-Object Name,State,Id | ConvertTo-Json':action==='start'?'Start-VM -VM $vm -Confirm:$false':'Stop-VM -VM $vm -Confirm:$false');
    h.run('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{env:{...process.env,CTCMCP_VM_NAME:p.vmName}});
  }else throw Error('VM provider not supported on this host');
}
export function extensionCommand(action,args,h){
  const handler={'guest-enable':guestEnable,'guest-disable':guestDisable,'guest-status':guestStatus,'extension-install':install,'browser-grant':browserGrant,'guest-grant':guestGrant,'guest-init':guestInit,'guest-connect':guestConnect,'relay-init':relayInit,'relay-status':relayStatus,'profile-revoke':revoke,'vm':vm}[action];
  if(!handler)return false;
  handler(args,h);return true;
}
