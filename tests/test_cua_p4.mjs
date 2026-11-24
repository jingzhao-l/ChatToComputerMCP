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
import readline from 'node:readline';
const workerOnly=process.env.CTCMCP_CUA_WORKER_ONLY==='1';
const executable=process.env.CTCMCP_CUA_TEST_SERVER;
if(!workerOnly&&!executable)throw Error('Set CTCMCP_CUA_TEST_SERVER to the native test apphost');
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
    if(q.url==='/download'){r.writeHead(200,{'Content-Type':'text/plain','Content-Disposition':'attachment; filename=fixture.txt'});r.end('bounded artifact');return}
    if(q.url==='/frame'){r.setHeader('Content-Type','text/html');r.end(`<button onclick="this.textContent='Frame done'">Frame action</button>`);return}
    if(q.url==='/advanced'){
      r.setHeader('Content-Type','text/html');
      r.end(`<title>Advanced fixture</title><iframe src="/frame"></iframe>
      <button onclick="document.getElementById('dialog-result').textContent=confirm('Approve fixture?')?'Accepted':'Dismissed'">Confirm</button><output id="dialog-result"></output>
      <input type="file" aria-label="Fixture file" onchange="document.getElementById('file-result').textContent=this.files[0].name"><output id="file-result"></output>
      <a href="/download">Download fixture</a>
      <div role="button" draggable="true" style="width:100px;height:60px;background:#ddd" ondragstart="event.dataTransfer.setData('text/plain','fixture')">Drag source</div>
      <div role="button" style="width:100px;height:60px;background:#eee" ondragover="event.preventDefault()" ondrop="event.preventDefault();this.textContent='Drop done'">Drag destination</div>`);return
    }
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
    {id:'web',kind:'browser',clients:['shared-connection'],origins:[origin],allowFiles:true},
    {id:'guest',kind:'guest',clients:['shared-connection'],port:guestPort,tokenFile:fs.realpathSync(guestCredential)}
  ]}));
  let call,step,browser;
  if(workerOnly){
    const manifest=path.join(tmp,'browser-manifest.json');
    privateFile(manifest,JSON.stringify({kind:'browser',origins:[origin],allowFiles:true}));
    const child=cp.spawn(node,[worker],{env:{...process.env,CTCMCP_CUA_EXTENSION_MANIFEST:manifest,PLAYWRIGHT_BROWSERS_PATH:path.join(runtime,'browsers')},stdio:['pipe','pipe','inherit']});
    hosts.push(child);
    const lines=readline.createInterface({input:child.stdout});
    const pending=new Map();
    lines.on('line',line=>{const x=JSON.parse(line);pending.get(x.id)?.(x.result)});
    let sequence=0;
    async function request(name,args){
      const id=++sequence;
      const response=new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>{pending.delete(id);reject(Error('worker timed out: '+name))},16000);
        pending.set(id,x=>{clearTimeout(timer);pending.delete(id);resolve(x)});
      });
      child.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name,arguments:args}})+'\n');
      return await response;
    }
    const opened=await request('extension_open',{});
    assert.equal(opened.structuredContent?.headless,true,JSON.stringify(opened).slice(0,500));
    step=async(operation,parameters={},refusal=false)=>{
      console.log('browser step:',operation);
      const reply=await request('extension_call',{operation,parameters});
      if(refusal){assert(reply.isError,JSON.stringify(reply).slice(0,500));return reply}
      assert(!reply.isError&&reply.structuredContent,JSON.stringify(reply).slice(0,800));
      return {content:reply.content,structuredContent:{result:reply.structuredContent}};
    };
  }else{
    await spawnServer(guestPort,guestPolicy,guestAuth,'guest');
    await spawnServer(hostPort,hostPolicy,hostAuth,'host');
    const guestList=await rpc(guestPort,guestAuth,'tools/list',{});
    assert(guestList.result.tools.every(t=>t.name.startsWith('cua_')&&!t.name.startsWith('cua_extension_')));
    await tool(guestPort,guestAuth,'read_file',{relative_path:'policy.json'},true);
    await tool(guestPort,'invalid','cua_capabilities',{},true);
    call=(name,args,refusal=false)=>tool(hostPort,hostAuth,name,args,refusal);
    const profiles=(await call('cua_extension_list',{})).structuredContent.profiles;
    assert.deepEqual(profiles.map(p=>p.id).sort(),['guest','web']);
    const opened=await call('cua_extension_open',{profile_id:'web',request_id:requestID()});
    browser=opened.structuredContent.session_id;
    assert(opened.structuredContent.result.headless);assert(opened.content[0].text.includes(browser));
    step=(operation,parameters={},refusal=false,request_id=requestID())=>{
      console.log('browser step:',operation);
      return call('cua_extension_call',{session_id:browser,operation,parameters,request_id},refusal);
    };
  }
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
  if(!workerOnly){assert.equal((await call('cua_receipt_status',{request_id:actionId})).structuredContent.state,'exited');
  await step('click',{ref:button.ref},true,actionId);} // persistent receipt refuses replay
  const original=(await step('tab_list')).structuredContent.result.active_tab_id;
  const newTab=(await step('tab_open',{url:origin+'/advanced'})).structuredContent.result.active_tab_id;
  assert.notEqual(newTab,original);
  assert.equal((await step('tab_list')).structuredContent.result.tabs.length,2);
  assert.equal((await step('wait_for',{text_contains:'Frame action',timeout_ms:3000})).structuredContent.result.verified,true);
  async function fresh(name){
    let view=await step('observe',{include_screenshot:false});
    const e=view.structuredContent.result.elements.find(x=>x.name===name);
    assert(e,'missing '+name);return e;
  }
  const frameButton=await fresh('Frame action');
  assert(frameButton.frame_id);
  await step('click',{ref:frameButton.ref});
  assert.equal((await step('wait_for',{text_contains:'Frame done',timeout_ms:3000})).structuredContent.result.verified,true);
  const confirm=await fresh('Confirm');
  await step('dialog_prepare',{type:'confirm',message:'Approve fixture?',action:'accept'});
  await step('click',{ref:confirm.ref});
  assert.equal((await step('verify',{text_contains:'Accepted'})).structuredContent.result.verified,true);
  const inputFile=await fresh('Fixture file');
  await step('upload',{ref:inputFile.ref,name:'fixture.txt',mime_type:'text/plain',data:Buffer.from('test').toString('base64')});
  assert.equal((await step('wait_for',{text_contains:'fixture.txt',timeout_ms:3000})).structuredContent.result.verified,true);
  const download=await fresh('Download fixture');
  const artifact=(await step('download',{ref:download.ref})).structuredContent.result;
  assert.equal(Buffer.from(artifact.data,'base64').toString(),'bounded artifact');
  const dragSnap=(await step('observe',{include_screenshot:false})).structuredContent.result.elements;
  await step('drag',{ref:dragSnap.find(e=>e.name==='Drag source').ref,destination_ref:dragSnap.find(e=>e.name==='Drag destination').ref});
  assert.equal((await step('verify',{text_contains:'Drop done'})).structuredContent.result.verified,true);
  const advancedRef=await fresh('Confirm');
  await step('tab_select',{tab_id:original});
  await step('click',{ref:advancedRef.ref},true);
  await step('tab_close',{tab_id:newTab});
  assert.equal((await step('tab_list')).structuredContent.result.tabs.length,1);
  console.log('PASS browser tabs, cross-frame refs, dialog, bounded file artifacts, drag and wait-for');
  await step('navigate',{url:'http://127.0.0.1:'+forbidden+'/'},true);
  await step('observe',{include_screenshot:false});
  // Test a redirect to an unapproved origin. Browser may show its error page;
  // it may not contact the target server or return its content.
  await step('navigate',{url:origin+'/unsafe-redirect'},true);
  assert.equal(forbiddenRequests,0);
  // Failed uncertain navigation retires this lease; if recoverable, explicit close.
  if(!workerOnly){
  await call('cua_extension_close',{session_id:browser},true).catch(()=>{});
  const g=(await call('cua_extension_open',{profile_id:'guest',request_id:requestID()})).structuredContent;
  assert.equal(g.result.guest_mode,true);
  const guest=(operation,parameters={},refusal=false)=>call('cua_extension_call',{session_id:g.session_id,operation,parameters,request_id:requestID()},refusal);
  await guest('cua_list_apps');
  await guest('read_file',{relative_path:'policy.json'},true);
  await guest('cua_observe',{session_id:'unowned'},true);
  await call('cua_extension_open',{profile_id:'guest',request_id:requestID()},true); // exclusive guest
  await call('cua_extension_close',{session_id:g.session_id});
  // Same-policy second server must not become a second receipt authority.
  const competingPort=await availablePort(),competingAuth=token();
  await spawnServer(competingPort,hostPolicy,competingAuth,'host');
  await tool(competingPort,competingAuth,'cua_receipt_status',{request_id:actionId},true);
  // Strict client-only mode cannot be defeated by a body-supplied claimed identity.
  const strictPolicy=JSON.parse(fs.readFileSync(hostPolicy,'utf8'));strictPolicy.requireVerifiedClient=true;
  fs.writeFileSync(hostPolicy,JSON.stringify(strictPolicy));
  const strict=(await call('cua_capabilities',{})).structuredContent;
  assert.equal(strict.requires_verified_client,true);assert.equal(strict.enabled,false);assert.equal(strict.conversation_identity_verified,false);
  await call('cua_extension_list',{},true);
  await call('cua_receipt_status',{request_id:actionId},true);
  await call('cua_extension_list',{conversation_id:'forged'},true);
  console.log('PASS process-exclusive receipt authority and fail-closed verified-client policy');
  console.log('PASS real headless browser through native MCP: screenshot, action, verification, stale refs, receipt replay, redirect denial');
  console.log('PASS native guest-only endpoint and authenticated relay: no shell/files, no foreign session, exclusive guest lease');
  // Two independent relay endpoints, two guest credentials, two private policy
  // roots. Native HTTP dispatch is exercised, not just a schema mock.
  const relayPorts=[await availablePort(),await availablePort()];
  const relayAuths=[token(),token()];
  const relayPolicies=[];
  const guestPortB=await availablePort(),guestAuthB=token();
  const guestDirB=path.join(tmp,'guest-b');fs.mkdirSync(guestDirB);
  const guestPolicyB=path.join(guestDirB,'policy.json'),guestCredentialB=path.join(guestDirB,'fixture-token');
  privateFile(guestCredentialB,guestAuthB+'\n');
  privateFile(guestPolicyB,JSON.stringify({...base,grants:[{application:process.platform==='darwin'?'com.example.CuaFixture':path.join(guestDirB,'fixture.exe'),clients:['shared-connection']}]}));
  await spawnServer(guestPortB,guestPolicyB,guestAuthB,'guest');
  for(let i=0;i<2;i++){
    const dir=path.join(tmp,'relay-'+i);fs.mkdirSync(dir);
    const policy=path.join(dir,'policy.json');relayPolicies.push(policy);
    privateFile(policy,JSON.stringify({...base,extensionRuntime:{node,nodeSHA256:hash(node),worker,workerSHA256:hash(worker)},profiles:[
      {id:'domain-'+i,kind:'guest',clients:['shared-connection'],port:i===0?guestPort:guestPortB,tokenFile:fs.realpathSync(i===0?guestCredential:guestCredentialB)}
    ]}));
    await spawnServer(relayPorts[i],policy,relayAuths[i],'relay');
  }
  const relay=(i,name,args={},refusal=false)=>tool(relayPorts[i],relayAuths[i],name,args,refusal);
  const listed=(await rpc(relayPorts[0],relayAuths[0],'tools/list',{})).result.tools.map(t=>t.name).sort();
  assert.deepEqual(listed,['cua_capabilities','cua_extension_call','cua_extension_close','cua_extension_list','cua_extension_open','cua_receipt_status'].sort());
  assert.equal((await relay(0,'cua_capabilities')).structuredContent.identity,'dedicated_relay_credential');
  assert.equal((await relay(0,'cua_capabilities')).structuredContent.conversation_identity_verified,false);
  assert.equal((await relay(0,'cua_capabilities')).structuredContent.host_tools_allowed,false);
  await tool(relayPorts[1],relayAuths[0],'cua_extension_list',{},true);
  for(const name of ['read_file','run_command','start_command','list_workspaces','workspace_call','git_status','connection_identity','cua_list_apps','cua_launch_app','cua_act'])
    await relay(0,name,{},true);
  await relay(0,'workspace_call',{workspace_id:'default',tool_name:'read_file',arguments:{relative_path:'policy.json'}},true);
  await relay(0,'cua_extension_open',{profile_id:'domain-1',request_id:requestID()},true);
  const leaseA=(await relay(0,'cua_extension_open',{profile_id:'domain-0',request_id:requestID()})).structuredContent.session_id;
  const leaseB=(await relay(1,'cua_extension_open',{profile_id:'domain-1',request_id:requestID()})).structuredContent.session_id;
  await relay(1,'cua_extension_call',{session_id:leaseA,operation:'cua_list_apps',parameters:{},request_id:requestID()},true);
  await relay(1,'cua_extension_close',{session_id:leaseA},true);
  await relay(0,'cua_extension_call',{session_id:leaseA,operation:'cua_list_apps',parameters:{},request_id:requestID()});
  await relay(1,'cua_extension_call',{session_id:leaseB,operation:'cua_list_apps',parameters:{},request_id:requestID()});
  await relay(0,'cua_extension_call',{session_id:leaseA,operation:'run_command',parameters:{command:'echo forbidden'},request_id:requestID()},true);
  await relay(0,'cua_extension_close',{session_id:leaseA});
  await relay(1,'cua_extension_close',{session_id:leaseB});
  const tampered=JSON.parse(fs.readFileSync(relayPolicies[0],'utf8'));
  tampered.profiles.push({id:'extra',kind:'browser',origins:[origin],clients:['shared-connection']});
  fs.writeFileSync(relayPolicies[0],JSON.stringify(tampered));
  await relay(0,'cua_extension_list',{},true);
  await relay(0,'cua_extension_open',{profile_id:'extra',request_id:requestID()},true);
  await relay(0,'run_command',{command:'echo denied'},true);
  console.log('PASS two dedicated relay credentials, cross-domain lease rejection, host-tool denial and fail-closed policy widening');

  }else console.log('PASS direct real Chromium worker regression (native HTTP and receipt tests excluded in worker-only mode)');
}finally{
  for(const p of hosts){p.stdin.end('\n');if(p.exitCode===null){await Promise.race([once(p,'exit'),new Promise(r=>setTimeout(r,3000))]);if(p.exitCode===null)p.kill()}}
  for(const s of servers){s.closeAllConnections?.();s.close()}
  fs.rmSync(tmp,{recursive:true,force:true});
}
