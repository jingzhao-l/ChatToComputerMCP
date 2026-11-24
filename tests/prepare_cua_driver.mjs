// SPDX-License-Identifier: Apache-2.0
// Prepare the exact pinned driver in an isolated temporary directory.
// No registration, local grant, update, OS permission prompt or desktop input.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import cp from 'node:child_process';
import {driverVersion,driverAssets,verifyArchive} from '../bin/cua-support.mjs';

const asset=driverAssets[process.platform+'-'+process.arch];
if(!asset)throw Error('Unsupported driver platform');
const temp=fs.mkdtempSync(path.join(process.env.RUNNER_TEMP??os.tmpdir(),'ctcmcp-driver-'));
const archive=path.join(temp,asset[0]),payload=path.join(temp,'payload');
fs.mkdirSync(payload,{mode:0o700});
function run(exe,args,options={}){
  const r=cp.spawnSync(exe,args,{stdio:['ignore',2,2],timeout:180000,windowsHide:true,...options});
  if(r.error||r.status!==0)throw Error(exe+' failed: '+(r.error?.message??r.status));
}
try{
  if(process.env.CTCMCP_CUA_TEST_ARCHIVE)fs.copyFileSync(process.env.CTCMCP_CUA_TEST_ARCHIVE,archive);
  else run(process.platform==='win32'?'curl.exe':'/usr/bin/curl',
    ['--fail','--location','--proto','=https','--max-time','150','--retry','2','--output',archive,
     'https://github.com/trycua/cua/releases/download/cua-driver-rs-v'+driverVersion+'/'+asset[0]]);
  verifyArchive(archive,asset);
  if(process.platform==='darwin')run('/usr/bin/tar',['-xzf',archive,'-C',payload]);
  else run('powershell.exe',['-NoProfile','-NonInteractive','-Command',
    '$ErrorActionPreference="Stop"; Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory($env:CTCMCP_CUA_ARCHIVE,$env:CTCMCP_CUA_DEST)'],
    {env:{...process.env,CTCMCP_CUA_ARCHIVE:archive,CTCMCP_CUA_DEST:payload}});
  const matches=[];
  function scan(dir,depth){
    if(depth>6)throw Error('Unexpected archive layout');
    for(const item of fs.readdirSync(dir,{withFileTypes:true})){
      const p=path.join(dir,item.name);
      if(item.isDirectory())scan(p,depth+1);
      else if(item.isFile()&&item.name===(process.platform==='win32'?'cua-driver.exe':'cua-driver'))matches.push(p);
    }
  }
  scan(payload,0);
  const candidates=process.platform==='darwin'?matches.filter(p=>p.includes(path.join('CuaDriver.app','Contents','MacOS','cua-driver'))):matches;
  if(candidates.length!==1)throw Error('Unexpected pinned driver layout');
  const driver=fs.realpathSync(candidates[0]);
  if(process.platform==='darwin')run('/usr/bin/codesign',['--verify','--deep','--strict',path.dirname(path.dirname(path.dirname(driver)))]);
  if(process.env.GITHUB_ENV)fs.appendFileSync(process.env.GITHUB_ENV,'CTCMCP_REAL_CUA_DRIVER='+driver+'\n');
  console.log(driver);
}catch(error){fs.rmSync(temp,{recursive:true,force:true});throw error}
