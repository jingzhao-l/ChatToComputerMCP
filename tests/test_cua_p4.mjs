// Real Chromium + actual native loopback MCP gateway. Guest transport is
// exercised between two local native test processes, NOT a VM acceptance.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import net from 'node:net';
import cp from 'node:child_process';
import crypto from 'node:crypto';
import {once} from 'node:events';
const executable=process.env.CTCMCP_CUA_TEST_SERVER;
if(!executable)throw Error('Set CTCMCP_CUA_TEST_SERVER to the native test apphost');
const runtime=fs.realpathSync(process.env.CTCMCP_CUA_P4_RUNTIME??'cua-runtime');
const hash=f=>crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'ctcmcp-p4-integration-'));
const hosts=[],servers=[];
const requestID=()=>crypto.randomBytes(24).toString('hex');
const token=()=>crypto.randomBytes(32).toString('hex');
async function listen(server){
  servers.push(server);
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  return server.address().port;
}
async function availablePort(){
  const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));
  const p=s.address().port;await new Promise(r=>s.close(r));return p;
}
function privateFile(file,data){
  fs.writeFileSync(file,data,{mode:0o600,flag:'wx'});
  if(process.platform==='win32'){
    const r=cp.spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',
      '$ErrorActionPreference="Stop"; $p=$env:CTCMCP_TEST_PRIVATE_FILE; $sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User; $acl=New-Object System.Security.AccessControl.FileSecurity; $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true,$false); $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid,"FullControl","Allow"))); Set-Acl -LiteralPath $p -AclObject $acl'],
      {env:{...process.env,CTCMCP_TEST_PRIVATE_FILE:file},encoding:'utf8'});
    assert.equal(r.status,0,r.stderr);
  }
}
async function spawnServer(port,policy,auth,mode){
  const proc=cp.spawn(executable,['cua-test-server',String(port),policy,auth,mode],{stdio:['pipe','pipe','pipe']});
  hosts.push(proc);
  let error='';proc.stderr.on('data',d=>error+=d.toString().slice(0,2000));
  await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>reject(Error('native server not ready: '+error)),10000);
    proc.once('exit',code=>{clearTimeout(timer);reject(Error('server exited '+code+' '+error))});
    proc.stdout.on('data',d=>{if(d.toString().includes('READY')){clearTimeout(timer);resolve()}});
  });
}
async function rpc(port,auth,method,params){
  const id=crypto.randomUUID();
  const response=await fetch('http://127.0.0.1:'+port+'/mcp',{method:'POST',
    headers:{'Content-Type':'application/json','X-FileMCP-Local-Token':auth},
    body:JSON.stringify({jsonrpc:'2.0',id,method,params}),signal:AbortSignal.timeout(16000)});
  if(!response.ok)return {http:response.status};
  const value=await response.json();assert.equal(value.id,id);return value;
}
async function tool(port,auth,name,args,refusal=false){
  const value=await rpc(port,auth,'tools/call',{name,arguments:args});
  if(refusal){assert(value.error||value.result?.isError||value.http,JSON.stringify(value).slice(0,300));return value}
  assert(!value.error&&!value.result?.isError&&value.result?.structuredContent,JSON.stringify(value).slice(0,800));
  return value.result;
}
try{
  let forbiddenRequests=0;
  const forbidden=await listen(http.createServer((q,r)=>{forbiddenRequests++;r.end('forbidden')}));
  const site=await listen(http.createServer((q,r)=>{
    if(q.url==='/safe-redirect'){r.writeHead(302,{location:'/'}).end();return}
    if(q.url==='/unsafe-redirect'){r.writeHead(302,{location:'http://127.0.0.1:'+forbidden+'/'}).end();return}
    r.setHeader('Content-Type','text/html; charset=utf-8');
    r.end('<title>CUA fixture</title><input aria-label="Name"><button onclick="document.querySelector(\'output\').textContent=\'Hello \'+document.querySelector(\'input\').value">Apply</button><output>Ready</output>');
  }));
  const origin='http://127.0.0.1:'+site;
  const guestPort=await availablePort(),hostPort=await availablePort(),guestAuth=token(),hostAuth=token();
  const guestDir=path.join(tmp,'guest'),hostDir=path.join(tmp,'host');fs.mkdirSync(guestDir);fs.mkdirSync(hostDir);
  const node=fs.realpathSync(process.execPath),worker=path.join(runtime,'worker.mjs');
  const base={enabled:true,driver:node,driverSHA256:'0'.repeat(64),grants:[]};
  const guestPolicy=path.join(guestDir,'policy.json'),hostPolicy=path.join(hostDir,'policy.json'),guestCredential=path.join(hostDir,'fixture-token');
  privateFile(guestCredential,guestAuth+'\n');
  privateFile(guestPolicy,JSON.stringify({...base,grants:[{application:process.platform==='darwin'?'com.example.CuaFixture':path.join(guestDir,'fixture.exe'),clients:['shared-connection']}]}));
  privateFile(hostPolicy,JSON.stringify({...base,extensionRuntime:{node,nodeSHA256:hash(node),worker,workerSHA256:hash(worker)},profiles:[
    {id:'web',kind:'browser',clients:['shared-connection'],origins:[origin]},
    {id:'guest',kind:'guest',clients:['shared-connection'],port:guestPort,tokenFile:fs.realpathSync(guestCredential)}
  ]}));
  await spawnServer(guestPort,guestPolicy,guestAuth,'guest');
  await spawnServer(hostPort,hostPolicy,hostAuth,'host');
  const guestList=await rpc(guestPort,guestAuth,'tools/list',{});
  assert(guestList.result.tools.every(t=>t.name.startsWith('cua_')&&!t.name.startsWith('cua_extension_')));
  await tool(guestPort,guestAuth,'read_file',{relative_path:'policy.json'},true);
  await tool(guestPort,'invalid','cua_capabilities',{},true);
  const call=(name,args,refusal=false)=>tool(hostPort,hostAuth,name,args,refusal);
  const profiles=(await call('cua_extension_list',{})).structuredContent.profiles;
  assert.deepEqual(profiles.map(p=>p.id).sort(),['guest','web']);
  const opened=await call('cua_extension_open',{profile_id:'web',request_id:requestID()});
  const browser=opened.structuredContent.session_id;
  assert(opened.structuredContent.result.headless);assert(opened.content[0].text.includes(browser));
  const step=(operation,parameters={},refusal=false,request_id=requestID())=>call('cua_extension_call',{session_id:browser,operation,parameters,request_id},refusal);
  await step('navigate',{url:origin+'/safe-redirect'});
  let snap=await step('observe',{include_screenshot:true});
  assert(snap.content.some(b=>b.type==='image'&&b.mimeType==='image/png'));
  const elements=snap.structuredContent.result.elements;
  const input=elements.find(e=>e.tag==='input'),oldButton=elements.find(e=>e.tag==='button');
  assert(input&&oldButton);
  await step('fill',{ref:input.ref,text:'MCP'});
  await step('click',{ref:oldButton.ref},true); // stale snapshot, must not retire
  snap=await step('observe',{include_screenshot:false});
  const button=snap.structuredContent.result.elements.find(e=>e.tag==='button');
  const actionId=requestID();
  await step('click',{ref:button.ref},false,actionId);
  const verified=await step('verify',{text_contains:'Hello MCP'});
  assert.equal(verified.structuredContent.result.verified,true);
  assert.equal((await call('cua_receipt_status',{request_id:actionId})).structuredContent.state,'exited');
  await step('click',{ref:button.ref},true,actionId); // persistent receipt refuses replay
  await step('navigate',{url:'http://127.0.0.1:'+forbidden+'/'},true);
  await step('observe',{include_screenshot:false});
  // Test a redirect to an unapproved origin. Browser may show its error page;
  // it may not contact the target server or return its content.
  await step('navigate',{url:origin+'/unsafe-redirect'},true);
  assert.equal(forbiddenRequests,0);
  // Failed uncertain navigation retires this lease; if recoverable, explicit close.
  await call('cua_extension_close',{session_id:browser},true).catch(()=>{});
  const g=(await call('cua_extension_open',{profile_id:'guest',request_id:requestID()})).structuredContent;
  assert.equal(g.result.guest_mode,true);
  const guest=(operation,parameters={},refusal=false)=>call('cua_extension_call',{session_id:g.session_id,operation,parameters,request_id:requestID()},refusal);
  await guest('cua_list_apps');
  await guest('read_file',{relative_path:'policy.json'},true);
  await guest('cua_observe',{session_id:'unowned'},true);
  await call('cua_extension_open',{profile_id:'guest',request_id:requestID()},true); // exclusive guest
  await call('cua_extension_close',{session_id:g.session_id});
  console.log('PASS real headless browser through native MCP: screenshot, action, verification, stale refs, receipt replay, redirect denial');
  console.log('PASS native guest-only endpoint and authenticated relay: no shell/files, no foreign session, exclusive guest lease');
}finally{
  for(const p of hosts){p.stdin.end('\n');if(p.exitCode===null){await Promise.race([once(p,'exit'),new Promise(r=>setTimeout(r,3000))]);if(p.exitCode===null)p.kill()}}
  for(const s of servers){s.closeAllConnections?.();s.close()}
  fs.rmSync(tmp,{recursive:true,force:true});
}
