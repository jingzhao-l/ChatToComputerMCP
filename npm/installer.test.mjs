import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import cp from 'node:child_process';
import url from 'node:url';
import {validVersion,compareVersions,readPolicy,writePolicy,
  policyFile,installedVersion,activateMacRelease,assertMacSigningTeamCompatible,
  parseRegistryLatest,selectMacSigningIdentity
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
  fs.copyFileSync(path.join(root,'bin','cua-support.mjs'),
    path.join(src,'bin','cua-support.mjs'));
  fs.copyFileSync(path.join(root,'bin','cua-extension-support.mjs'),path.join(src,'bin','cua-extension-support.mjs'));
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
    '<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.localfilesmcp.app</string><key>CFBundleShortVersionString</key><string>'+version+'</string><key>CFBundleExecutable</key><string>ChatToComputerMCP</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>',
    'PLIST',
    '/usr/bin/codesign --force --sign - --timestamp=none dist/ChatToComputerMCP.app',
    ''
  ].join('\n'));
  function cli(args,extraEnv={}) {
    return cp.spawnSync(process.execPath,
      [path.join(src,'bin','chat-to-computer-mcp.mjs'),...args],
      {encoding:'utf8',env:{...process.env,HOME:fakeHome,...extraEnv},timeout:10000});
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
  const releases=path.join(fakeHome,'.local','share','chat-to-computer-mcp','releases');
  const retained=path.join(releases,version);
  // Simulate a rollback retaining both signed bundles. The fixture's old
  // bundle is used only for signing-team continuity; it is never launched.
  const older='0.0.1';
  fs.cpSync(retained,path.join(releases,older),{recursive:true});
  activateMacRelease(releases,older);
  const fakeBin=path.join(temp,'npm-fixture');
  fs.mkdirSync(fakeBin);
  fs.writeFileSync(path.join(fakeBin,'npm'),'#!/bin/sh\nprintf "%s\\n" '+version+'\n',{mode:0o700});
  const reactivated=cli(['update','install'],{PATH:fakeBin+path.delimiter+process.env.PATH});
  assert.equal(reactivated.status,0,reactivated.stderr);
  assert.match(reactivated.stdout,/Selected verified retained release/);
  assert.equal(installedVersion(releases,'darwin'),version);
  assert.match(reactivated.stdout,/Version .* installed/);
  // The retained release must not have been rebuilt or the old one removed.
  assert.equal(fs.existsSync(path.join(releases,older,'ChatToComputerMCP.app')),true);
  activateMacRelease(releases,older);
  fs.appendFileSync(expected,'# changed after signing\n');
  const damaged=cli(['install']);
  assert.notEqual(damaged.status,0);
  assert.match(damaged.stderr,/codesign exited/);
  assert.equal(installedVersion(releases,'darwin'),older);
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
test('npm Registry latest accepts npm 11 and npm 12 JSON shapes, rejects ambiguity',()=>{
  for (const [output,expected] of [
    ['"0.4.3"','0.4.3'],
    ['["0.4.3"]','0.4.3'],
    ['0.4.3\n','0.4.3'],
    ['["1.2.3"]\n','1.2.3']
  ]) assert.equal(parseRegistryLatest(output),expected);
  for(const bad of [
    '[]','["0.4.3","0.4.2"]','{"latest":"0.4.3"}',
    '"0.4.3-beta.1"','["not-a-version"]','["0.4.3",null]',
    '0.4.3\n0.4.2','null','42', ''
  ]) assert.throws(()=>parseRegistryLatest(bad),/not a valid stable release version/);
});

test('Mac update auto-signing reuses only a locally available original certificate',()=>{
  const identity='Apple Development: Test Developer (EXAMPLE1)';
  const info='Identifier=com.example.app\nAuthority='+identity+'\nTeamIdentifier=EXAMPLETEAM\n';
  const available='  1) ABCDEF123 "'+identity+'"\n    1 valid identities found\n';
  assert.equal(selectMacSigningIdentity(info,available),identity);
  assert.equal(selectMacSigningIdentity('Identifier=com.example.app\nTeamIdentifier=not set',''),null);
  assert.throws(()=>selectMacSigningIdentity(info,'0 valid identities found'),
    /not available locally/);
  assert.throws(()=>selectMacSigningIdentity('TeamIdentifier=EXAMPLETEAM\nAuthority=Unknown Cert',available),
    /Cannot identify/);
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
  assert.throws(()=>activateMacRelease(temp,'0.9.0'),/missing or incomplete/);
  fs.rmSync(path.join(temp,'current'));
  fs.symlinkSync('0.9.0',path.join(temp,'current'));
  assert.throws(()=>installedVersion(temp,'darwin'),/Current Mac release is missing or incomplete/);
  fs.rmSync(path.join(temp,'current'));
  fs.symlinkSync('../../outside',path.join(temp,'current'));
  assert.throws(()=>installedVersion(temp,'darwin'),/Unsafe current release pointer/);
  assert.throws(()=>activateMacRelease(temp,'0.2.1'),/Unsafe current release pointer/);
  fs.rmSync(path.join(temp,'0.1.0'),{recursive:true});
  fs.rmSync(path.join(temp,'0.2.1'),{recursive:true});
  assert.throws(()=>installedVersion(temp,'darwin'),/Unsafe current release pointer/);
  fs.rmSync(path.join(temp,'current'));
  assert.equal(installedVersion(temp,'darwin'),null);
  assert.equal(installedVersion(temp,'win32'),null);
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

test('npm 12 update check and install select official CLI in isolated cwd',()=>{
  if(process.platform==='win32')return;
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-npm12-update-'));
  try {
    const bin=path.join(temp,'bin');
    fs.mkdirSync(bin);
    const npm=path.join(bin,'npm');
    const calls=path.join(temp,'npm-calls');
    const selectedCwd=path.join(temp,'selected-cwd');
    fs.writeFileSync(npm,[
      '#!/bin/sh',
      'printf "%s\\n" "$*" >> "'+calls+'"',
      'if [ "$1" = "view" ]; then printf \'%s\\n\' "$CTCMCP_TEST_LATEST_JSON"; exit 0; fi',
      'if [ "$1" = "exec" ]; then',
      '  pwd > "'+selectedCwd+'"',
      '  test ! -f package.json || exit 83',
      '  exit 0',
      'fi',
      'exit 4',
      ''
    ].join('\n'));
    fs.chmodSync(npm,0o755);
    const env={PATH:bin+path.delimiter+process.env.PATH,
      CTCMCP_TEST_LATEST_JSON:'["99.0.0"]'};
    const checked=invoke(['update','check'],temp,env);
    assert.equal(checked.status,0,checked.stderr);
    assert.match(checked.stdout,/npm latest: 99\.0\.0/);
    const installed=invoke(['update','install'],temp,env);
    assert.notEqual(installed.status,0);
    assert.match(installed.stderr,/expected release was not installed/);
    const cwd=fs.readFileSync(selectedCwd,'utf8').trim();
    assert.notEqual(cwd,root);
    assert.match(path.basename(cwd),/^ctcmcp-official-update-/);
    assert.match(fs.readFileSync(calls,'utf8'),/--package=chat-to-computer-mcp@99\.0\.0/);
    const bad=invoke(['update','check'],temp,{
      ...env,CTCMCP_TEST_LATEST_JSON:'["99.0.0","99.0.1"]'
    });
    assert.notEqual(bad.status,0);
    assert.match(bad.stderr,/not a valid stable release version/);
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

test('CUA is explicit, defaults disabled and rejects unpinned archives', async()=>{
  const {verifyArchive,driverAssets}=await import('../bin/cua-support.mjs');
  const home=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-cua-test-'));
  try{
    const help=invoke(['cua','help'],home);
    assert.equal(help.status,0,help.stderr);
    assert.match(help.stdout,/cua disable/);
    const bad=path.join(home,'bad-archive');
    fs.writeFileSync(bad,'not the official driver');
    assert.throws(()=>verifyArchive(bad,driverAssets['darwin-arm64']),/SHA-256/);
    if(['darwin','win32'].includes(process.platform)){
      const status=invoke(['cua','status'],home);
      assert.equal(status.status,0,status.stderr);
      assert.equal(JSON.parse(status.stdout).enabled,false);
      const refused=invoke(['cua','grant','--application',process.platform==='darwin'?'com.apple.calculator':process.execPath,'--client','shared-connection'],home);
      assert.notEqual(refused.status,0);
      assert.equal(JSON.parse(invoke(['cua','status'],home).stdout).enabled,false);
      assert.equal(invoke(['cua','disable'],home).status,0);
    }
  }finally{fs.rmSync(home,{recursive:true,force:true});}
});

test('CUA guest transport pins the host and provisions only explicit local templates', async()=>{
  const {guestConnectPlan,vmProvisionPlan}=await import('../bin/cua-extension-support.mjs');
  const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-cua-plans-'));
  try{
    const known=path.join(tmp,'known hosts'),key=path.join(tmp,'test identity'),disk=path.join(tmp,'prepared.vhdx');
    for(const file of [known,key,disk])fs.writeFileSync(file,'fixture',{mode:0o600});
    const profile={id:'vm',kind:'guest',port:18766};
    const opts=['--profile','vm','--host','guest.example','--user','guest','--guest-port','18765','--known-hosts',known,'--identity',key];
    const plan=guestConnectPlan(opts,profile,'darwin');
    assert.equal(plan.exe,'/usr/bin/ssh');
    assert.ok(plan.args.includes('StrictHostKeyChecking=yes'));
    assert.ok(plan.args.includes('127.0.0.1:18766:127.0.0.1:18765'));
    assert.ok(plan.args.includes('IdentityAgent=none'));
    assert.throws(()=>guestConnectPlan(opts,{...profile,id:'other'},'darwin'));
    assert.throws(()=>guestConnectPlan(opts.map(v=>v==='guest.example'?'-oProxyCommand=x':v),profile,'darwin'));
    const mac=vmProvisionPlan(['--provider','lume','--template','clean','--name','new-guest'],'darwin');
    assert.deepEqual(mac.map(x=>x.args),[['get','clean'],['clone','clean','new-guest']]);
    assert.throws(()=>vmProvisionPlan(['--provider','lume','--template','clean','--name','clean'],'darwin'));
    const winArgs=['--provider','hyper-v','--template',disk,'--name','new-guest','--destination',tmp,'--switch','Private'];
    const [win]=vmProvisionPlan(winArgs,'win32');
    assert.ok(win.args.at(-1).includes('[IO.File]::Copy($source,$disk,$false)'));
    assert.match(win.args.at(-1),/ParentPath/);
    assert.equal(win.env.CTCMCP_VM_NAME,'new-guest');
    fs.mkdirSync(path.join(tmp,'new-guest'));
    assert.throws(()=>vmProvisionPlan(winArgs,'win32'));
  }finally{fs.rmSync(tmp,{recursive:true,force:true});}
});

test('CUA relay registry separates domain credentials and refuses duplicate guest bindings', async()=>{
  const {extensionCommand}=await import('../bin/cua-extension-support.mjs');
  const {createHash}=await import('node:crypto');
  const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-relay-registry-'));
  try{
    // Fixture for the CLI registry only; real native HTTP tests use separate guest listeners.
    const source=path.join(root,'cua-runtime','worker.mjs');
    const digest=p=>createHash('sha256').update(fs.readFileSync(p)).digest('hex');
    const workerHash=digest(source),nodeHash=digest(process.execPath);
    const runtimeDir=path.join(tmp,'cua-extension','1.0.0-'+workerHash.slice(0,16));
    fs.mkdirSync(runtimeDir,{recursive:true});
    fs.copyFileSync(source,path.join(runtimeDir,'worker.mjs'));
    fs.writeFileSync(path.join(runtimeDir,'runtime.json'),JSON.stringify({workerSHA256:workerHash,nodeSHA256:nodeHash}));
    const h={root:()=>tmp,hashFile:digest,installedDriver:()=>({driver:process.execPath,driverSHA256:nodeHash}),
      readPolicy:()=>null,writePrivate:(p,data)=>fs.writeFileSync(p,data,{mode:0o600,flag:'wx'})};
    const tokenA=path.join(tmp,'guest-a'),tokenB=path.join(tmp,'guest-b'),copyA=path.join(tmp,'copy-a');
    fs.writeFileSync(tokenA,'a'.repeat(64)+'\n',{mode:0o600});
    fs.writeFileSync(tokenB,'b'.repeat(64)+'\n',{mode:0o600});
    fs.writeFileSync(copyA,'a'.repeat(64)+'\n',{mode:0o600});
    const args=(id,port,guest,file)=>['--domain',id,'--port',String(port),'--guest-port',String(guest),'--token-file',file,'--confirm-dedicated-guest'];
    assert.throws(()=>extensionCommand('relay-init',args('one',18801,18766,tokenA),h),/disabled/);
    h.writePrivate(path.join(tmp,'cua-guest-approval'),'c'.repeat(64)+'\n');
    extensionCommand('relay-init',args('one',18801,18766,tokenA),h);
    extensionCommand('relay-init',args('two',18802,18767,tokenB),h);
    const read=(id,file)=>fs.readFileSync(path.join(tmp,'cua-relays',id,file),'utf8');
    assert.notEqual(read('one','relay-token'),read('two','relay-token'));
    const policy=JSON.parse(read('one','cua-policy.json'));
    assert.deepEqual(policy.grants,[]);
    assert.equal(policy.profiles.length,1);assert.equal(policy.profiles[0].kind,'guest');
    assert.throws(()=>extensionCommand('relay-init',args('three',18803,18768,copyA),h),/distinct/);
    assert.throws(()=>extensionCommand('relay-init',args('three',18801,18768,tokenB),h),/distinct/);
    assert.throws(()=>extensionCommand('guest-grant',['--profile','oops','--port','18769','--token-file',copyA,'--client','shared-connection'],h),/reserved/);
    assert.equal(fs.existsSync(path.join(tmp,'cua-relays','three')),false);
  }finally{fs.rmSync(tmp,{recursive:true,force:true})}
});


test('Guest is disabled by default and noninteractive CLI cannot enable it',()=>{
  const home=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-guest-default-'));
  try{
    if(!['darwin','win32'].includes(process.platform))return;
    const state=()=>JSON.parse(invoke(['cua','guest-status'],home).stdout);
    assert.equal(state().enabled,false);
    assert.equal(state().preferredExecution,'native_background');
    const refused=invoke(['cua','guest-enable'],home);
    assert.notEqual(refused.status,0);assert.match(refused.stderr,/interactive/);
    for(const args of [['guest-init','--confirm-isolated-desktop'],['guest-grant'],['relay-init'],['guest-connect'],['guest-serve'],['relay-serve','--domain','one'],['vm','start'],['vm','provision']]){
      const r=invoke(['cua',...args],home);
      assert.notEqual(r.status,0,args.join(' '));assert.match(r.stderr,/disabled/i);
    }
    assert.equal(state().enabled,false);
    assert.equal(invoke(['cua','guest-disable'],home).status,0);
    assert.equal(state().enabled,false);
  }finally{fs.rmSync(home,{recursive:true,force:true})}
});


test('CUA private configuration writer protects before writing and replaces atomically',async()=>{
  const {writePrivate}=await import('../bin/cua-support.mjs');
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-private-write-'));
  try{
    const f=path.join(dir,'policy.json');
    writePrivate(f,'long fixture contents');
    writePrivate(f,'short');
    assert.equal(fs.readFileSync(f,'utf8'),'short');
    assert.deepEqual(fs.readdirSync(dir),['policy.json']);
    if(process.platform!=='win32')assert.equal(fs.statSync(f).mode&0o777,0o600);
  }finally{fs.rmSync(dir,{recursive:true,force:true})}
});
