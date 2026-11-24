// SPDX-License-Identifier: Apache-2.0
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import cp from 'node:child_process';
import crypto from 'node:crypto';

export const driverVersion = '0.61.0';
export const driverAssets = Object.freeze({
  'darwin-arm64': ['cua-driver-rs-0.61.0-darwin-arm64.tar.gz','5c9770ca6514f788c5f6c24e0cb4a8002c8869b1707aa3fa14b1d284c13b225e'],
  'darwin-x64': ['cua-driver-rs-0.61.0-darwin-x86_64.tar.gz','c6e74982ac673e8268aa2342fbae211a2dcbeda80c688cd24b38cba8e978d6d3'],
  'win32-arm64': ['cua-driver-rs-0.61.0-windows-arm64.zip','10ef9ef2f2dcd2e6c669d6027442d02de725a253db16666046dfaf7f3e73c4a1'],
  'win32-x64': ['cua-driver-rs-0.61.0-windows-x86_64.zip','4b33ce784056effdbdc1816b97918d0e41d98ef2672cc1773dcef26c391138a3']
});
const baseURL = 'https://github.com/trycua/cua/releases/download/cua-driver-rs-v'+driverVersion+'/';

function root() {
  const home=os.homedir();
  if(process.platform==='darwin')return path.join(home,'Library','Application Support','ChatToComputerMCP');
  if(process.platform==='win32')return path.join(process.env.LOCALAPPDATA || path.join(home,'AppData','Local'),'ChatToComputerMCP');
  throw Error('CUA is available on macOS 14+ and interactive Windows 10/11 only');
}
export function policyPath() { return path.join(root(),'cua-policy.json'); }
const driverRoot=()=>path.join(root(),'cua-driver',driverVersion,process.platform+'-'+process.arch);
function run(exe,args,opts={}) {
  const r=cp.spawnSync(exe,args,{stdio:'inherit',timeout:180000,windowsHide:true,...opts});
  if(r.error||r.status!==0)throw Error(exe+' failed: '+(r.error?.message??r.status));
  return r;
}
function hashFile(file) {
  const stat=fs.statSync(file);
  if(!stat.isFile()||stat.size<=0||stat.size>250000000)throw Error('Invalid driver or archive size');
  const h=crypto.createHash('sha256'),fd=fs.openSync(file,'r'),buffer=Buffer.alloc(1024*1024);
  try { for(let n;(n=fs.readSync(fd,buffer,0,buffer.length,null))>0;)h.update(buffer.subarray(0,n)); }
  finally{fs.closeSync(fd);}
  return h.digest('hex');
}
export function verifyArchive(file,asset) {
  if(hashFile(file)!==asset[1])throw Error('Cua Driver archive SHA-256 does not match the pinned official release');
}
function privateAcl(file) {
  if(process.platform!=='win32'){fs.chmodSync(file,0o600);return;}
  const script = [
    '$ErrorActionPreference="Stop"',
    '$p=$env:CTCMCP_CUA_PRIVATE_FILE',
    '$sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User',
    '$acl=New-Object System.Security.AccessControl.FileSecurity',
    '$acl.SetOwner($sid)',
    '$acl.SetAccessRuleProtection($true,$false)',
    '$rule=New-Object System.Security.AccessControl.FileSystemAccessRule($sid,"FullControl","Allow")',
    '$acl.AddAccessRule($rule)',
    'Set-Acl -LiteralPath $p -AclObject $acl'
  ].join('; ');
  run('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{
    env:{...process.env,CTCMCP_CUA_PRIVATE_FILE:file}});
}
function readPolicy() {
  const file=policyPath();
  if(!fs.existsSync(file))return null;
  const st=fs.lstatSync(file);
  if(!st.isFile()||st.isSymbolicLink()||st.size>65536)throw Error('Invalid local CUA policy');
  if(process.platform==='darwin'&&((st.mode&0o077)!==0||st.uid!==process.getuid()))throw Error('CUA policy must be private and owned by this user');
  const result=JSON.parse(fs.readFileSync(file,'utf8'));
  if(!Array.isArray(result.grants))throw Error('Invalid local CUA policy');
  return result;
}
function writePolicy(policy) {
  const file=policyPath();
  fs.mkdirSync(path.dirname(file),{recursive:true,mode:0o700});
  const temp=file+'.'+crypto.randomBytes(12).toString('hex')+'.tmp';
  try{
    fs.writeFileSync(temp,JSON.stringify(policy,null,2)+'\n',{mode:0o600,flag:'wx'});
    privateAcl(temp);
    fs.renameSync(temp,file);
  }finally{fs.rmSync(temp,{force:true});}
}
function findDriver(directory) {
  const exe=process.platform==='win32'?'cua-driver.exe':'cua-driver';
  const matches=[];
  function visit(dir,depth) {
    if(depth>6)throw Error('Unexpected Cua Driver archive layout');
    for(const item of fs.readdirSync(dir,{withFileTypes:true})){
      const p=path.join(dir,item.name);
      if(item.isSymbolicLink())continue;
      if(item.isDirectory())visit(p,depth+1);
      else if(item.isFile()&&item.name===exe)matches.push(p);
    }
  }
  visit(directory,0);
  const preferred=process.platform==='darwin'?matches.filter(p=>p.includes(path.join('CuaDriver.app','Contents','MacOS',exe))):matches;
  if(preferred.length!==1)throw Error('Pinned driver archive did not contain one expected executable');
  return fs.realpathSync(preferred[0]);
}
function installedDriver() {
  const dir=driverRoot();
  const marker=JSON.parse(fs.readFileSync(path.join(dir,'ctcmcp-driver.json'),'utf8'));
  if(marker.version!==driverVersion||marker.asset!==driverAssets[process.platform+'-'+process.arch]?.[0])throw Error('Invalid driver installation marker');
  const file=findDriver(dir);
  const hash=hashFile(file);
  if(hash!==marker.sha256)throw Error('Installed Cua Driver integrity mismatch; reinstall explicitly');
  return {driver:file,driverSHA256:hash};
}
function install(localArchive) {
  const asset=driverAssets[process.platform+'-'+process.arch];
  if(!asset)throw Error('Unsupported Cua Driver platform/architecture');
  const destination=driverRoot();
  if(fs.existsSync(destination)){installedDriver();console.log('Pinned Cua Driver already installed; no permissions changed.');return;}
  fs.mkdirSync(path.dirname(destination),{recursive:true,mode:0o700});
  const stage=fs.mkdtempSync(path.join(path.dirname(destination),'.cua-stage-'));
  try{
    const archive=path.join(stage,asset[0]),extracted=path.join(stage,'payload');
    fs.mkdirSync(extracted,{mode:0o700});
    if(localArchive)fs.copyFileSync(path.resolve(localArchive),archive,fs.constants.COPYFILE_EXCL);
    else run(process.platform==='win32'?'curl.exe':'/usr/bin/curl',
      ['--fail','--location','--proto','=https','--max-time','150','--output',archive,baseURL+asset[0]]);
    verifyArchive(archive,asset);
    if(process.platform==='darwin')run('/usr/bin/tar',['-xzf',archive,'-C',extracted]);
    else run('powershell.exe',['-NoProfile','-NonInteractive','-Command',
      '$ErrorActionPreference="Stop"; Expand-Archive -LiteralPath $env:CTCMCP_CUA_ARCHIVE -DestinationPath $env:CTCMCP_CUA_DEST'],
      {env:{...process.env,CTCMCP_CUA_ARCHIVE:archive,CTCMCP_CUA_DEST:extracted}});
    const driver=findDriver(extracted);
    if(process.platform==='darwin'){
      const bundle=driver.slice(0,driver.lastIndexOf(path.join('Contents','MacOS','cua-driver')));
      run('/usr/bin/codesign',['--verify','--deep','--strict',bundle]);
    }
    // The checked release includes its license and third-party notices. Keep
    // the entire original payload; never re-sign or auto-update the driver.
    fs.writeFileSync(path.join(extracted,'ctcmcp-driver.json'),JSON.stringify({
      version:driverVersion,asset:asset[0],archiveSHA256:asset[1],sha256:hashFile(driver)
    },null,2)+'\n',{mode:0o600,flag:'wx'});
    fs.renameSync(extracted,destination);
    installedDriver();
    console.log('Pinned Cua Driver installed. CUA remains disabled until a local grant is created. No OS permissions were changed.');
  }finally{fs.rmSync(stage,{recursive:true,force:true});}
}
function parseGrant(args) {
  const opts={};
  for(let i=0;i<args.length;i+=2){
    if(!['--application','--client'].includes(args[i])||opts[args[i]]!==undefined||!args[i+1])
      throw Error('Use --application <bundle-id or absolute exe path> --client <verified OAuth ID or shared-connection>');
    opts[args[i]]=args[i+1];
  }
  let application=opts['--application'],client=opts['--client'];
  if(!application||!client||!/^[A-Za-z0-9._-]{1,128}$/.test(client))throw Error('Explicit application and client are required');
  if(process.platform==='darwin'){
    if(!/^[A-Za-z0-9][A-Za-z0-9.-]{0,254}$/.test(application))throw Error('An exact macOS bundle identifier is required');
  }else{
    if(!path.isAbsolute(application)||!/^\.exe$/i.test(path.extname(application)))throw Error('An absolute Windows .exe path is required');
    application=fs.realpathSync(application);
    if(!fs.statSync(application).isFile())throw Error('Invalid application executable');
  }
  return {application,client};
}
export function cuaCommand(action,args) {
  if(!action||action==='help'){
    console.log([
      'cua install [--archive <file>]     Install the checksum-pinned optional driver',
      'cua status                        Inspect local driver and authorization policy',
      'cua grant --application <id> --client <id>    Enable one explicit local grant',
      'cua revoke --application <id> --client <id>   Revoke that grant',
      'cua disable                       Emergency revoke all CUA grants',
      'Mac: application is a bundle identifier; Windows: exact absolute .exe path.',
      'Use shared-connection only if all chats on that tunnel may access this application.',
      'Grant OS Accessibility and Screen Recording manually to ChatToComputerMCP on Mac.'
    ].join('\n'));return;
  }
  if(action==='install'&&(args.length===0||(args.length===2&&args[0]==='--archive')))return install(args[1]);
  if(action==='status'&&args.length===0){
    let driver='not installed or integrity mismatch';
    try{installedDriver();driver=driverVersion;}catch{}
    const p=readPolicy();
    console.log(JSON.stringify({driver,enabled:p?.enabled===true,grants:p?.grants??[],
      note:'Local configuration only; live OS permission and desktop compatibility require an MCP probe/real test.'},null,2));
    return;
  }
  if(action==='disable'&&args.length===0){
    fs.rmSync(policyPath(),{force:true});
    console.log('CUA grants removed. Existing leases are invalidated by the native runtime; dispatched actions cannot be undone.');
    return;
  }
  if(action==='grant'||action==='revoke'){
    const {application,client}=parseGrant(args);
    let p=readPolicy();
    if(action==='grant'){
      const driver=installedDriver();
      p={enabled:true,...driver,grants:p?.grants??[]};
      let g=p.grants.find(g=>process.platform==='win32'?g.application.toLowerCase()===application.toLowerCase():g.application===application);
      if(!g){g={application,clients:[]};p.grants.push(g);}
      if(!g.clients.includes(client))g.clients.push(client);
      if(p.grants.length>64)throw Error('At most 64 applications are allowed');
    }else{
      if(!p){console.log('No CUA grants exist.');return;}
      for(const g of p.grants)if(process.platform==='win32'?g.application.toLowerCase()===application.toLowerCase():g.application===application)
        g.clients=g.clients.filter(c=>c!==client);
      p.grants=p.grants.filter(g=>g.clients.length);
      p.enabled=p.grants.length>0;
    }
    writePolicy(p);
    console.log(action==='grant'?'Explicit local CUA grant saved. Any older leases are invalidated.':'CUA grant revoked; older leases are invalidated.');
    if(action==='grant'&&client==='shared-connection')console.log('Warning: this is a shared tunnel grant, not a verified ChatGPT conversation identity.');
    return;
  }
  throw Error('Unknown or invalid cua command; run cua help');
}
