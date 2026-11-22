import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import cp from 'node:child_process';
import url from 'node:url';
const root=path.dirname(path.dirname(url.fileURLToPath(import.meta.url)));
const entry=path.join(root,'bin','chat-to-computer-mcp.mjs');
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
  assert.match(noApp.stderr,/Run install first/);
  assert.equal(fs.existsSync(path.join(tmp,'Library','LaunchAgents',
    'local.chat-to-computer-mcp.plist')),false);
  const disabled=invoke(['launchagent','disable'],tmp);
  assert.equal(disabled.status,0,disabled.stderr);
  const after=invoke(['status'],tmp);
  assert.match(after.stdout,/LaunchAgent: disabled, not loaded/);
 } finally {fs.rmSync(tmp,{recursive:true,force:true});}
});
test('CLI help and version are stable',()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-help-test-'));
 try {
  assert.match(invoke(['version'],temp).stdout,/0\.1\.0/);
  assert.match(invoke(['help'],temp).stdout,/npx --yes chat-to-computer-mcp/);
  assert.notEqual(invoke(['unknown'],temp).status,0);
 } finally {fs.rmSync(temp,{recursive:true,force:true});}
});

test('dry-run LaunchAgent has explicit label, safe defaults, and no registration',()=>{
 if(process.platform!=='darwin') return;
 const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-agent-preview-'));
 try {
  const app=path.join(tmp,'.local','share','chat-to-computer-mcp','releases','0.1.0','ChatToComputerMCP.app');
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
