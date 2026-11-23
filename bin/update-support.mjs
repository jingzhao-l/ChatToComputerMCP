/* SPDX-License-Identifier: Apache-2.0 */
import fs from 'node:fs';
import path from 'node:path';

export const packageName='chat-to-computer-mcp';
export const registry='https://registry.npmjs.org/';

export function validVersion(value) {
  return typeof value==='string' &&
    /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value);
}
export function compareVersions(a,b) {
  if(!validVersion(a)||!validVersion(b))throw Error('Invalid stable version');
  for(let i=0;i<3;i++) {
    const x=BigInt(a.split('.')[i]), y=BigInt(b.split('.')[i]);
    if(x!==y)return x>y?1:-1;
  }
  return 0;
}
export const defaultPolicy=Object.freeze({
  checkAutomatically:false,
  installAutomatically:false
});
export function policyFile(platform,home,env=process.env) {
  return platform==='win32'
    ? path.join(env.LOCALAPPDATA||path.join(home,'AppData','Local'),
      'ChatToComputerMCP','update-settings.json')
    : path.join(home,'Library','Application Support','ChatToComputerMCP','update-settings.json');
}
export function readPolicy(file) {
  try {
    if(fs.lstatSync(file).isSymbolicLink())
      throw Error('Refusing symlinked update settings');
    const json=JSON.parse(fs.readFileSync(file,'utf8'));
    if(json.schema!==1 || typeof json.checkAutomatically!=='boolean' ||
       typeof json.installAutomatically!=='boolean' ||
       (json.installAutomatically&&!json.checkAutomatically))
      throw Error('Invalid updater configuration');
    return {checkAutomatically:json.checkAutomatically,
      installAutomatically:json.installAutomatically};
  } catch(e) {
    if(e.code==='ENOENT')return {...defaultPolicy};
    throw e;
  }
}
export function writePolicy(file,policy) {
  if(typeof policy.checkAutomatically!=='boolean' ||
     typeof policy.installAutomatically!=='boolean' ||
     (policy.installAutomatically&&!policy.checkAutomatically))
    throw Error('Invalid updater policy');
  fs.mkdirSync(path.dirname(file),{recursive:true,mode:0o700});
  try {
    if(fs.lstatSync(file).isSymbolicLink())
      throw Error('Refusing symlinked update settings');
  } catch(e) {
    if(e.code!=='ENOENT')throw e;
  }
  const temp=file+'.tmp-'+process.pid;
  try {
    fs.writeFileSync(temp,JSON.stringify({schema:1,...policy},null,2)+'\n',
      {flag:'wx',mode:0o600});
    fs.renameSync(temp,file);
  } finally {
    if(fs.existsSync(temp))fs.unlinkSync(temp);
  }
}
export function installedVersions(parent,platform) {
  if(!fs.existsSync(parent))return [];
  const executable=platform==='win32'?'ChatToComputerMCP.exe':
    path.join('ChatToComputerMCP.app','Contents','MacOS','ChatToComputerMCP');
  return fs.readdirSync(parent,{withFileTypes:true})
    .filter(e=>e.isDirectory()&&validVersion(e.name))
    .map(e=>e.name)
    .filter(version=>fs.existsSync(path.join(parent,version,executable)))
    .sort(compareVersions);
}
export function installedVersion(parent,platform) {
  const versions=installedVersions(parent,platform);
  if(!versions.length)return null;
  if(platform==='darwin') {
    const current=path.join(parent,'current');
    try {
      const entry=fs.lstatSync(current);
      if(!entry.isSymbolicLink())
        throw Error('Refusing non-symlink current release pointer');
      const link=fs.readlinkSync(current);
      if(!validVersion(link))throw Error('Unsafe current release pointer');
      if(versions.includes(link))return link;
    } catch(e) {
      if(e.code!=='ENOENT')throw e;
    }
  }
  // Windows and older installations without a current pointer use the
  // highest successfully built version, never an unrelated FileMCP app.
  return versions.at(-1);
}
export function activateMacRelease(parent,version) {
  if(!validVersion(version))throw Error('Invalid release version');
  const current=path.join(parent,'current');
  try {
    const existing=fs.lstatSync(current);
    if(!existing.isSymbolicLink()||!validVersion(fs.readlinkSync(current)))
      throw Error('Unsafe current release pointer');
  } catch(e) {
    if(e.code!=='ENOENT')throw e;
  }
  const temp=path.join(parent,'.current-'+process.pid+'-'+Date.now());
  fs.symlinkSync(version,temp,'dir');
  try {fs.renameSync(temp,current);}
  finally {if(fs.existsSync(temp))fs.unlinkSync(temp);}
}
