/* SPDX-License-Identifier: Apache-2.0 */
import fs from 'node:fs';
import path from 'node:path';

export const packageName='chat-to-computer-mcp';
export const registry='https://registry.npmjs.org/';

// An active macOS app's Keychain items may be bound to its signing team.
// Installing a differently signed bundle may remove access without deleting
// the items. Refuse silent team transitions before changing release pointers.
export function assertMacSigningTeamCompatible(previous, candidate) {
  if(typeof previous!=='string'||typeof candidate!=='string'||
     !previous.trim()||!candidate.trim())
    throw Error('Invalid macOS signing team metadata');
  if(previous!=='not set'&&previous!==candidate)
    throw Error('Mac signing team would change from '+previous+' to '+candidate+
      '. Supply CHAT_TO_COMPUTER_CODESIGN_IDENTITY for the original team; '+
      'the existing application and Keychain data have not been replaced.');
}


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

// npm 11 emits a JSON string for a single dist-tag value, while npm 12
// can return a one-element array. Accept only a single stable version; never
// silently choose a version from a multi-valued or malformed Registry result.
export function parseRegistryLatest(stdout) {
  if(typeof stdout!=='string')throw Error('npm latest is not a valid stable release version');
  const trimmed=stdout.trim();
  let value;
  try {value=JSON.parse(trimmed);}
  catch {value=trimmed;}
  if(Array.isArray(value)) {
    if(value.length!==1)throw Error('npm latest is not a valid stable release version');
    value=value[0];
  }
  if(!validVersion(value))throw Error('npm latest is not a valid stable release version');
  return value;
}

export function selectMacSigningIdentity(details, availableIdentities) {
  if(typeof details!=='string'||typeof availableIdentities!=='string')
    throw Error('Missing Mac signing identity details');
  const team=details.match(/^TeamIdentifier=(.+)$/m)?.[1]?.trim();
  if(!team || team==='not set')return null;
  const authority=details.match(/^Authority=(.+)$/m)?.[1]?.trim();
  if(!authority||
     !/^(?:Apple Development|Developer ID Application|Apple Distribution|Mac Developer): [^\r\n]{1,200}$/.test(authority))
    throw Error('Cannot identify the existing Mac signing certificate; set CHAT_TO_COMPUTER_CODESIGN_IDENTITY explicitly.');
  if(!availableIdentities.includes('"'+authority+'"'))
    throw Error('The existing Mac signing certificate is not available locally. Supply CHAT_TO_COMPUTER_CODESIGN_IDENTITY for the original signing team.');
  return authority;
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
