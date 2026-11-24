// SPDX-License-Identifier: Apache-2.0
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';

export const extensionHelp=[
  'cua extension-install              Install pinned Playwright and headless Chromium',
  'cua browser-grant --profile <id> --origin <origin> --client <id> (repeat --origin)',
  'cua guest-init --port <port> --confirm-isolated-desktop (inside a dedicated guest)',
  'cua guest-serve                    Run CUA-only guest listener inside that guest',
  'cua guest-grant --profile <id> --port <forwarded-port> --token-file <file> --client <id>',
  'cua profile-revoke --profile <id> --client <id>',
  'cua vm status|start|stop --profile <id> (registered Lume/Hyper-V VM)'
];
function dir(h){return path.join(h.root(),'cua-extension','1.0.0')}
function runtime(h){
  const d=dir(h),worker=fs.realpathSync(path.join(d,'worker.mjs')),node=fs.realpathSync(process.execPath);
  const marker=JSON.parse(fs.readFileSync(path.join(d,'runtime.json'),'utf8'));
  if(marker.workerSHA256!==h.hashFile(worker)||marker.nodeSHA256!==h.hashFile(node))
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
    const source=path.join(path.dirname(fileURLToPath(import.meta.url)),'..','cua-runtime');
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
  const o=options(args,['--profile','--client','--origin']);
  const origins=[...new Set((o['--origin']??[]).map(exactOrigin))].sort();
  if(!origins.length||origins.length>32)throw Error('One to 32 exact origins required');
  saveProfile({id:id(o['--profile']),kind:'browser',origins,clients:[client(o['--client'])]},h);
}
function guestGrant(args,h){
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
  if(!args.includes('--confirm-isolated-desktop'))throw Error('Explicit confirmation of an isolated guest desktop is required');
  const o=options(args.filter(x=>x!=='--confirm-isolated-desktop'),['--port']);
  const p=port(o['--port']??'18765'),configuration=path.join(h.root(),'cua-guest.json'),credential=path.join(h.root(),'cua-guest-token');
  if(fs.existsSync(configuration)||fs.existsSync(credential))throw Error('Guest already initialized; existing credentials are preserved');
  h.writePrivate(credential,crypto.randomBytes(32).toString('hex')+'\n');
  try{h.writePrivate(configuration,JSON.stringify({isolatedDesktopConfirmed:true,port:p})+'\n')}
  catch(e){fs.rmSync(credential,{force:true});throw e}
  console.log('Dedicated guest configured. Keep its credential file private.');
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
function vm(args,h){
  const [action,...rest]=args;
  if(!['status','start','stop'].includes(action))throw Error('Use cua vm status|start|stop --profile <id>');
  const o=options(rest,['--profile']),profile=id(o['--profile']);
  const p=h.readPolicy()?.profiles?.find(x=>x.id===profile&&x.kind==='guest');
  if(!p||!/^[A-Za-z0-9][A-Za-z0-9_. -]{0,79}$/.test(p.vmName??''))throw Error('No registered VM');
  if(p.vmProvider==='lume'&&process.platform==='darwin'){
    // Resolve an existing VM first, never pull an image from an arbitrary name.
    h.run('lume',['get',p.vmName]);
    if(action==='start')h.run('lume',['run',p.vmName,'--detach','--display','none','--vnc','disabled']);
    if(action==='stop')h.run('lume',['shutdown',p.vmName]);
  }else if(p.vmProvider==='hyper-v'&&process.platform==='win32'){
    const script='$ErrorActionPreference="Stop"; $vm=Get-VM | Where-Object {$_.Name -ceq $env:CTCMCP_VM_NAME}; if(@($vm).Count -ne 1){throw "VM not found"}; '+
      (action==='status'?'$vm | Select-Object Name,State,Id | ConvertTo-Json':action==='start'?'Start-VM -VM $vm -Confirm:$false':'Stop-VM -VM $vm -Confirm:$false');
    h.run('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{env:{...process.env,CTCMCP_VM_NAME:p.vmName}});
  }else throw Error('VM provider not supported on this host');
}
export function extensionCommand(action,args,h){
  const handler={'extension-install':install,'browser-grant':browserGrant,'guest-grant':guestGrant,'guest-init':guestInit,'profile-revoke':revoke,'vm':vm}[action];
  if(!handler)return false;
  handler(args,h);return true;
}
