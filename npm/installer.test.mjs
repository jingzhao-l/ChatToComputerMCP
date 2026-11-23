import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import cp from 'node:child_process';
import url from 'node:url';
import {validVersion,compareVersions,readPolicy,writePolicy,
  policyFile,installedVersion,activateMacRelease,assertMacSigningTeamCompatible
} from '../bin/update-support.mjs';
const root=path.dirname(path.dirname(url.fileURLToPath(import.meta.url)));
const entry=path.join(root,'bin','chat-to-computer-mcp.mjs');
const version=JSON.parse(fs.readFileSync(path.join(root,'package.json'),'utf8')).version;
function invoke(args,home,extraEnv={}) {
  const sandboxEnv={...process.env,HOME:home,...extraEnv};
  if(process.platform==='win32') {
    sandboxEnv.USERPROFILE=home;
    sandboxEnv.LOCALAPPDATA=path.join(home,'AppData','Local');
  }
  return cp.spawnSync(process.execPath,[entry,...args],{
    encoding:'utf8',env:sandboxEnv,timeout:6000
  });
}
test('installer is explicit; never auto-starts or creates a launchagent',()=>{
 const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-installer-test-'));
 try {
  const before=invoke(['status'],tmp);
  assert.equal(before.status,0,before.stderr);
  assert.match(before.stdout,/Installed: no/);
  const noApp=invoke(['launchagent','enable'],tmp);
  assert.notEqual(noApp.status,0);
  const plist=path.join(tmp,'Library','LaunchAgents',
    'local.chat-to-computer-mcp.plist');
  assert.equal(fs.existsSync(plist),false);
  const disabled=invoke(['launchagent','disable'],tmp);
  if(process.platform==='darwin') {
    assert.match(noApp.stderr,/Run install first/);
    assert.equal(disabled.status,0,disabled.stderr);
    // A temporary HOME shares the real macOS launchd domain; do not
    // assume the developer has no running ChatToComputerMCP service.
    assert.equal(fs.existsSync(plist),false);
  } else {
    // Linux is used only for the GitHub OIDC package gate. It must reject
    // macOS-only launchd commands without modifying the computer.
    assert.match(noApp.stderr,/LaunchAgent is available only on macOS/);
    assert.notEqual(disabled.status,0);
    assert.match(disabled.stderr,/LaunchAgent is available only on macOS/);
  }
  assert.equal(fs.existsSync(plist),false);
 } finally {fs.rmSync(tmp,{recursive:true,force:true});}
});
test('CLI help and version are stable',()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-help-test-'));
 try {
  assert.equal(invoke(['version'],temp).stdout.trim(), version);
  assert.match(invoke(['help'],temp).stdout,/npx --yes chat-to-computer-mcp/);
  assert.notEqual(invoke(['unknown'],temp).status,0);
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});

test('dry-run LaunchAgent has explicit label, safe defaults, and no registration',()=>{
 if(process.platform!=='darwin') return;
 const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-agent-preview-'));
 try {
  const app=path.join(tmp,'.local','share','chat-to-computer-mcp','releases',version,'ChatToComputerMCP.app');
  fs.mkdirSync(path.join(app,'Contents','MacOS'),{recursive:true});
  fs.writeFileSync(path.join(app,'Contents','MacOS','ChatToComputerMCP'),'#!/bin/sh\nexit 0\n');
  const preview=invoke(['launchagent','enable','--dry-run','--always'],tmp);
  assert.equal(preview.status,0,preview.stderr);
  assert.match(preview.stdout,/local.chat-to-computer-mcp/);
  assert.match(preview.stdout,/<key>KeepAlive<\/key><true\/>/);
  assert.match(preview.stdout,/<key>FILEMCP_AUTO_CONNECT<\/key><string>0<\/string>/);
  assert.equal(fs.existsSync(path.join(tmp,'Library','LaunchAgents',
    'local.chat-to-computer-mcp.plist')),false);
 } finally {fs.rmSync(tmp,{recursive:true,force:true});}
});

test('macOS local build places a real app bundle under versioned release and can be opened',()=>{
 if(process.platform!=='darwin')return;
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-layout-'));
 try {
  const src=path.join(temp,'source');
  const fakeHome=path.join(temp,'home');
  for(const d of ['bin','macos','vendor','assets/branding/chat-to-computer-mcp'])
    fs.mkdirSync(path.join(src,d),{recursive:true});
  fs.mkdirSync(fakeHome,{recursive:true});
  const previousAgent=path.join(fakeHome,'Library','LaunchAgents',
    'local.chat-to-computer-mcp.plist');
  fs.mkdirSync(path.dirname(previousAgent),{recursive:true});
  // Pretty-printed plist like macOS PlistBuddy emits, not compact CLI markup.
  fs.writeFileSync(previousAgent,[
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<plist version="1.0"><dict>',
    '<key>Label</key>',
    '<string>local.chat-to-computer-mcp</string>',
    '<key>ProgramArguments</key><array><string>/old/ChatToComputerMCP</string></array>',
    '<key>KeepAlive</key><true/>',
    '<key>EnvironmentVariables</key><dict>',
    '<key>FILEMCP_AUTO_CONNECT</key><string>1</string>',
    '<key>HTTPS_PROXY</key><string>http://127.0.0.1:15556</string>',
    '<key>CONTROL_PLANE_HTTP_PROXY</key><string>http://127.0.0.1:15556</string>',
    '<key>UNSAFE_API_KEY</key><string>must-not-survive</string>',
    '</dict></dict></plist>'
  ].join('\n'));
  fs.copyFileSync(entry,path.join(src,'bin','chat-to-computer-mcp.mjs'));
  fs.copyFileSync(path.join(root,'bin','update-support.mjs'),
    path.join(src,'bin','update-support.mjs'));
  fs.writeFileSync(path.join(src,'package.json'),JSON.stringify({
    name:'chat-to-computer-mcp',version:version
  }));
  fs.writeFileSync(path.join(src,'LICENSE'),'Apache-2.0 test fixture');
  fs.writeFileSync(path.join(src,'build_macos_icon.sh'),'#!/bin/bash\nexit 0\n');
  fs.writeFileSync(path.join(src,'assets/branding/render_chat_to_computer_mcp_icon.swift'),'// fixture\n');
  fs.writeFileSync(path.join(src,'build_macos_app.sh'),[
    '#!/bin/bash','set -eu',
    'mkdir -p dist/ChatToComputerMCP.app/Contents/MacOS',
    'printf "#!/bin/sh\\nexit 0\\n" > dist/ChatToComputerMCP.app/Contents/MacOS/ChatToComputerMCP',
    'chmod +x dist/ChatToComputerMCP.app/Contents/MacOS/ChatToComputerMCP',
    "cat > dist/ChatToComputerMCP.app/Contents/Info.plist <<'PLIST'",
    '<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>dev.chat-to-computer-mcp.fixture</string><key>CFBundleExecutable</key><string>ChatToComputerMCP</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>',
    'PLIST',
    '/usr/bin/codesign --force --sign - --timestamp=none dist/ChatToComputerMCP.app',
    ''
  ].join('\n'));
  function cli(args) {
    return cp.spawnSync(process.execPath,
      [path.join(src,'bin','chat-to-computer-mcp.mjs'),...args],
      {encoding:'utf8',env:{...process.env,HOME:fakeHome},timeout:10000});
  }
  const result=cli(['install']);
  assert.equal(result.status,0,result.stdout+'\n'+result.stderr);
  const parsedPlist=cp.spawnSync('/usr/bin/plutil',
    ['-convert','json','-o','-',previousAgent],{encoding:'utf8'});
  assert.equal(parsedPlist.status,0,parsedPlist.stderr);
  const migrated=JSON.parse(parsedPlist.stdout);
  assert.equal(migrated.EnvironmentVariables.FILEMCP_AUTO_CONNECT,'1');
  assert.equal(migrated.EnvironmentVariables.HTTPS_PROXY,'http://127.0.0.1:15556');
  assert.equal(migrated.EnvironmentVariables.CONTROL_PLANE_HTTP_PROXY,'http://127.0.0.1:15556');
  assert.equal(migrated.EnvironmentVariables.UNSAFE_API_KEY,undefined);
  assert.equal(migrated.KeepAlive,true);
  assert.match(migrated.ProgramArguments[0],/releases\/current\/ChatToComputerMCP\.app/);
  const expected=path.join(fakeHome,'.local','share','chat-to-computer-mcp',
    'releases',version,'ChatToComputerMCP.app','Contents','MacOS','ChatToComputerMCP');
  assert.equal(fs.existsSync(expected),true,'expected executable exists in app bundle');
  const state=cli(['status']);
  assert.equal(state.status,0,state.stderr);
  assert.match(state.stdout,/Installed: yes/);
  const preview=cli(['launchagent','enable','--dry-run']);
  assert.equal(preview.status,0,preview.stderr);
  assert.match(preview.stdout,/ChatToComputerMCP\.app\/Contents\/MacOS\/ChatToComputerMCP/);
  const duplicate=cli(['install']);
  assert.equal(duplicate.status,0,duplicate.stderr);
  assert.match(duplicate.stdout,/Already installed/);
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});


test('Mac updater never silently crosses Keychain signing teams',()=>{
 assert.doesNotThrow(()=>assertMacSigningTeamCompatible('not set','not set'));
 assert.doesNotThrow(()=>assertMacSigningTeamCompatible('not set','TEAM123'));
 assert.doesNotThrow(()=>assertMacSigningTeamCompatible('TEAM123','TEAM123'));
 assert.throws(()=>assertMacSigningTeamCompatible('TEAM123','not set'),/signing team would change/);
 assert.throws(()=>assertMacSigningTeamCompatible('TEAM123','DIFFERENT'),/signing team would change/);
 assert.throws(()=>assertMacSigningTeamCompatible('', 'not set'),/Invalid macOS/);
});

test('updater version ordering rejects malicious or prerelease input',()=>{
 assert.equal(compareVersions('0.2.1','0.2.0'),1);
 assert.equal(compareVersions('0.2.1','0.2.1'),0);
 assert.equal(compareVersions('0.2.0','0.10.0'),-1);
 assert.equal(compareVersions('1.0.0','0.999.999'),1);
 for(const x of ['1.0.0;rm -rf ~','0.0.0-stage','1.0.0-beta','1.2','../etc',
                   'v1.2.3','999.1.1 && echo injected','01.1.1']) {
  assert.equal(validVersion(x),false,x);
 }
});
test('updater policy is opt-in, stored locally, and defaults to disabled',()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-update-policy-'));
 try {
  const file=policyFile('darwin',temp);
  assert.deepEqual(readPolicy(file),{checkAutomatically:false,installAutomatically:false});
  writePolicy(file,{checkAutomatically:true,installAutomatically:true});
  assert.deepEqual(readPolicy(file),{checkAutomatically:true,installAutomatically:true});
  assert.throws(()=>writePolicy(file,{checkAutomatically:false,installAutomatically:true}));
  fs.writeFileSync(file,JSON.stringify({
    schema:1,checkAutomatically:false,installAutomatically:true
  }));
  assert.throws(()=>readPolicy(file),/Invalid updater configuration/);
  fs.writeFileSync(file,'{corrupted');
  assert.throws(()=>readPolicy(file),'corrupt policies must fail closed');
  if(process.platform!=='win32') {
    // Windows runner may not grant the symlink-creation privilege.
    fs.rmSync(file);
    fs.symlinkSync(path.join(temp,'outside'),file);
    assert.throws(()=>writePolicy(file,{checkAutomatically:true,installAutomatically:true}));
  }
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});
test('CLI updater policy commands do not enable launchagent or access credentials',()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-update-cli-'));
 try {
  const enabled=invoke(['update','enable','--auto-install'],temp);
  assert.equal(enabled.status,0,enabled.stderr);
  const status=invoke(['update','status'],temp);
  assert.equal(status.status,0,status.stderr);
  assert.match(status.stdout,/Check automatically: enabled/);
  assert.match(status.stdout,/Install automatically: enabled/);
  const disable=invoke(['update','disable'],temp);
  assert.equal(disable.status,0,disable.stderr);
  const after=invoke(['update','status'],temp);
  assert.match(after.stdout,/Install automatically: disabled/);
  assert.equal(fs.existsSync(path.join(temp,'Library','LaunchAgents',
    'local.chat-to-computer-mcp.plist')),false);
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});
test('macOS stable active symlink is atomic, validates targets and avoids old FileMCP',()=>{
 if(process.platform!=='darwin')return;
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-version-pointer-'));
 try {
  for(const release of ['0.1.0','0.2.1']) {
   const binary=path.join(temp,release,'ChatToComputerMCP.app','Contents',
     'MacOS','ChatToComputerMCP');
   fs.mkdirSync(path.dirname(binary),{recursive:true});
   fs.writeFileSync(binary,'fixture');
  }
  assert.equal(installedVersion(temp,'darwin'),'0.2.1');
  activateMacRelease(temp,'0.1.0');
  assert.equal(installedVersion(temp,'darwin'),'0.1.0');
  activateMacRelease(temp,'0.2.1');
  assert.equal(installedVersion(temp,'darwin'),'0.2.1');
  assert.equal(fs.readlinkSync(path.join(temp,'current')),'0.2.1');
  fs.rmSync(path.join(temp,'current'));
  fs.symlinkSync('../../outside',path.join(temp,'current'));
  assert.throws(()=>installedVersion(temp,'darwin'),/Unsafe current release pointer/);
  assert.throws(()=>activateMacRelease(temp,'0.2.1'),/Unsafe current release pointer/);
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});


test('CLI update check uses registry latest and never executes unverified updates',()=>{
 if(process.platform==='win32')return;
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-update-check-'));
 try {
  const bin=path.join(temp,'bin');
  fs.mkdirSync(bin);
  const npm=path.join(bin,'npm');
  const calls=path.join(temp,'npm-args.log');
  fs.writeFileSync(npm,
    '#!/bin/sh\n' +
    'printf "%s\\n" "$*" >> "' + calls + '"\n' +
    'if [ "$1" = "view" ]; then echo "\\\"99.0.0\\\""; exit 0; fi\n' +
    'exit 0\n');
  fs.chmodSync(npm,0o755);
  const env={PATH:bin+path.delimiter+process.env.PATH};
  const checked=invoke(['update','check'],temp,env);
  assert.equal(checked.status,0,checked.stderr);
  assert.match(checked.stdout,/npm latest: 99\.0\.0/);
  assert.match(checked.stdout,/Update available/);
  assert.doesNotMatch(fs.readFileSync(calls,'utf8'),/exec/);
  const installed=invoke(['update','install'],temp,env);
  assert.notEqual(installed.status,0);
  assert.match(installed.stderr,/expected release was not installed/);
  assert.match(fs.readFileSync(calls,'utf8'),/--package=chat-to-computer-mcp@99\.0\.0/);
  assert.equal(fs.existsSync(path.join(temp,'Library','LaunchAgents',
    'local.chat-to-computer-mcp.plist')),false);
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});

test('Windows user-login startup commands are explicit and platform-scoped',()=>{
  const home=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-startup-cli-'));
  try {
    const before=invoke(['startup','status'],home);
    if(process.platform==='win32') {
      assert.equal(before.status,0,before.stderr);
      assert.match(before.stdout,/Windows login startup:/);
      const enable=invoke(['startup','enable'],home);
      assert.notEqual(enable.status,0);
      assert.match(enable.stderr,/Install the application locally before configuring login startup/);
    } else {
      assert.notEqual(before.status,0);
      assert.match(before.stderr,/Windows startup is available only on Windows/);
    }
    assert.equal(fs.existsSync(path.join(home,'Library','LaunchAgents',
      'local.chat-to-computer-mcp.plist')),false);
    assert.equal(fs.existsSync(path.join(home,'AppData','Local',
      'ChatToComputerMCP','startup.json')),false);
  } finally {fs.rmSync(home,{recursive:true,force:true});}
});
