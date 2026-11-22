import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import cp from 'node:child_process';
import url from 'node:url';
const root=path.dirname(path.dirname(url.fileURLToPath(import.meta.url)));
const entry=path.join(root,'bin','chat-to-computer-mcp.mjs');
const version=JSON.parse(fs.readFileSync(path.join(root,'package.json'),'utf8')).version;
function invoke(args,home) {
  return cp.spawnSync(process.execPath,[entry,...args],{
    encoding:'utf8',env:{...process.env,HOME:home},timeout:6000
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
    const after=invoke(['status'],tmp);
    assert.match(after.stdout,/LaunchAgent: disabled, not loaded/);
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
  fs.copyFileSync(entry,path.join(src,'bin','chat-to-computer-mcp.mjs'));
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
    ''
  ].join('\n'));
  function cli(args) {
    return cp.spawnSync(process.execPath,
      [path.join(src,'bin','chat-to-computer-mcp.mjs'),...args],
      {encoding:'utf8',env:{...process.env,HOME:fakeHome},timeout:10000});
  }
  const result=cli(['install']);
  assert.equal(result.status,0,result.stdout+'\n'+result.stderr);
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
