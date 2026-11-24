// SPDX-License-Identifier: Apache-2.0
// Private stdio worker. No arbitrary code execution, user browser profile,
// filesystem tools, listener or caller-selected executable.
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import readline from 'node:readline';
import crypto from 'node:crypto';

const fail=message=>{throw Error(message)};
class Refusal extends Error{}
const refuse=message=>{throw new Refusal(message)};
const cfg=JSON.parse(fs.readFileSync(process.env.CTCMCP_CUA_EXTENSION_MANIFEST,'utf8'));
if(!['browser','guest'].includes(cfg.kind))fail('invalid extension profile');
const object=(v,keys,required=[])=>{
  if(!v||typeof v!=='object'||Array.isArray(v)||Object.keys(v).some(k=>!keys.includes(k))||
      required.some(k=>!Object.hasOwn(v,k)))refuse('invalid_parameters');
  return v;
};
const string=(v,max=16000)=>{if(typeof v!=='string'||Buffer.byteLength(v)>max)refuse('invalid_string');return v};
const integer=(v,min,max)=>{if(!Number.isSafeInteger(v)||v<min||v>max)refuse('invalid_integer');return v};
const boolean=v=>{if(typeof v!=='boolean')refuse('invalid_boolean');return v};
const result=(value,images=[])=>({content:[{type:'text',text:JSON.stringify(value)},...images],structuredContent:value});
const browserOps=new Set(['observe','navigate','click','fill','press','select','scroll','verify','back','forward','hover','close']);
const guestOps=new Set(['cua_capabilities','cua_list_apps','cua_list_windows','cua_launch_app','cua_create_session','cua_observe','cua_act','cua_verify','cua_stop_session','cua_receipt_status']);
let browser,context,page,originSet,proxy,snapshot=null,guestSessions=new Set(),opened=false,closing=false;
const guestRequests=new Set();
let guestToken;
function origin(url){
  const u=new URL(string(url,4096));
  if(!['http:','https:'].includes(u.protocol)||u.username||u.password)fail('origin_not_allowed');
  return u.origin;
}
function allowedURL(url){try{return originSet.has(origin(url))}catch{return false}}
function assertPage(){
  if(!page||page.isClosed())fail('browser_closed');
  if(page.url()!=='about:blank'&&!allowedURL(page.url()))fail('origin_not_allowed');
}
function resetSnapshot(){snapshot=null}
async function createProxy(){
  const sockets=new Set();
  const server=http.createServer((req,res)=>{
    try{
      const u=new URL(req.url);
      if(u.protocol!=='http:'||!allowedURL(u.href)){res.writeHead(403).end();return}
      const headers={...req.headers,host:u.host};
      delete headers['proxy-authorization'];delete headers['proxy-connection'];
      const upstream=http.request(u,{method:req.method,headers,timeout:6500},response=>{
        res.writeHead(response.statusCode,response.headers);response.pipe(res);
      });
      upstream.on('timeout',()=>upstream.destroy());
      upstream.on('error',()=>{if(!res.headersSent)res.writeHead(502);res.end()});
      req.on('aborted',()=>upstream.destroy());
      req.pipe(upstream);
    }catch{res.writeHead(400).end()}
  });
  server.on('connect',(req,client,head)=>{
    try{
      // HTTPS CONNECT permits only an exact locally granted HTTPS origin.
      const u=new URL('https://'+req.url);
      if(u.username||u.password||u.pathname!=='/'||u.search||u.hash||!originSet.has(u.origin)){
        client.end('HTTP/1.1 403 Forbidden\r\n\r\n');return;
      }
      const upstream=net.connect({host:u.hostname.replace(/^\[|\]$/g,''),port:Number(u.port||443)});
      sockets.add(upstream);upstream.on('close',()=>sockets.delete(upstream));
      upstream.setTimeout(15000,()=>upstream.destroy());
      upstream.on('connect',()=>{
        client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if(head.length)upstream.write(head);
        client.pipe(upstream);upstream.pipe(client);
      });
      upstream.on('error',()=>client.destroy());client.on('error',()=>upstream.destroy());
      client.on('close',()=>upstream.destroy());upstream.on('close',()=>client.destroy());
    }catch{client.destroy()}
  });
  server.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket))});
  // Never listen on a public or guest interface.
  await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve)});
  return {address:'http://127.0.0.1:'+server.address().port,close(){for(const s of sockets)s.destroy();server.close()}};
}
async function browserOpen(){
  if(!Array.isArray(cfg.origins)||!cfg.origins.length||cfg.origins.length>32)fail('invalid_origin_grants');
  originSet=new Set(cfg.origins.map(o=>{if(origin(o)!==o)fail('invalid_origin_grant');return o}));
  const {chromium}=await import('playwright');
  proxy=await createProxy();
  browser=await chromium.launch({headless:true,chromiumSandbox:true,timeout:8000,
    proxy:{server:proxy.address,bypass:'<-loopback>'},
    args:['--disable-quic','--force-webrtc-ip-handling-policy=disable_non_proxied_udp']});
  context=await browser.newContext({viewport:{width:1280,height:800},serviceWorkers:'block',acceptDownloads:false});
  // Chromium routes ordinary HTTP and HTTPS through the private allowlist
  // proxy, including redirect hops. Route checks add defense in depth.
  await context.route('**/*',async route=>{
    if(!allowedURL(route.request().url()))return await route.abort('blockedbyclient').catch(()=>{});
    return await route.continue().catch(()=>{});
  });
  // WebSocket traffic is not exposed by this version; no alternate bypass.
  await context.routeWebSocket('**/*',socket=>socket.close({code:1008,reason:'WebSocket not enabled for this profile'}));
  page=await context.newPage();
  context.on('page',p=>{if(p!==page)p.close().catch(()=>{})});
  page.on('dialog',d=>d.dismiss().catch(()=>{}));
  page.on('download',d=>d.cancel().catch(()=>{}));
  page.on('framenavigated',frame=>{if(frame===page.mainFrame())resetSnapshot()});
  return {kind:'browser',headless:true,profile_isolated:true,origins:[...originSet],url:page.url()};
}
async function observe(params){
  object(params,['include_screenshot']);
  const image=params.include_screenshot===undefined?true:boolean(params.include_screenshot);
  assertPage();
  // Only top-level visible interactive elements. Fixed code, never a remote JS expression.
  const elements=[];
  const handles=await page.locator('a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=link],[role=checkbox],[role=radio],[role=combobox],[contenteditable=true]').elementHandles();
  const id=crypto.randomBytes(16).toString('hex');
  const refs=new Map();
  for(const h of handles.slice(0,200)){
    if(!await h.isVisible().catch(()=>false))continue;
    const meta=await h.evaluate(e=>({tag:e.tagName.toLowerCase(),role:e.getAttribute('role')||'',name:(e.getAttribute('aria-label')||e.getAttribute('placeholder')||e.innerText||e.getAttribute('title')||'').trim().slice(0,500),type:e.getAttribute('type')||'',disabled:!!e.disabled,value:e.type==='password'?'':('value'in e?String(e.value).slice(0,500):''),checked:'checked'in e?!!e.checked:null})).catch(()=>null);
    if(!meta)continue;
    const ref=id+':'+elements.length;
    refs.set(ref,h);elements.push({ref,...meta});
  }
  const text=(await page.locator('body').innerText({timeout:3000}).catch(()=>'' )).slice(0,24000);
  const value={url:page.url(),title:(await page.title()).slice(0,1000),text,elements,truncated:handles.length>200,snapshot_id:id};
  const blocks=[];
  if(image){
    const png=await page.screenshot({type:'png',timeout:5000,animations:'disabled'});
    if(png.length>5_000_000)fail('browser_screenshot_too_large');
    blocks.push({type:'image',mimeType:'image/png',data:png.toString('base64')});
  }
  snapshot={id,refs,url:page.url(),at:performance.now()};
  return result(value,blocks);
}
async function ref(params){
  assertPage();
  if(!snapshot||snapshot.url!==page.url()||performance.now()-snapshot.at>30000)refuse('observe_again');
  const handle=snapshot.refs.get(string(params.ref,128));
  if(!handle||!await handle.evaluate(e=>e.isConnected).catch(()=>false)||!await handle.isVisible().catch(()=>false))
    refuse('stale_or_foreign_browser_ref');
  resetSnapshot();
  return handle;
}
async function browserCall(op,p){
  if(!browserOps.has(op))fail('unsupported_browser_operation');
  assertPage();
  if(op==='observe')return await observe(p);
  if(op==='close'){object(p,[]);await shutdown();return result({closed:true})}
  if(op==='navigate'){
    object(p,['url'],['url']);if(!allowedURL(p.url))refuse('origin_not_allowed');
    resetSnapshot();await page.goto(p.url,{waitUntil:'domcontentloaded',timeout:7000});assertPage();
  }else if(op==='back'||op==='forward'){
    object(p,[]);resetSnapshot();await (op==='back'?page.goBack:page.goForward).call(page,{waitUntil:'domcontentloaded',timeout:7000});assertPage();
  }else if(op==='verify'){
    object(p,['text_contains','url'],[]);
    if(!Object.keys(p).length)fail('verification_predicate_required');
    const checks={};if(Object.hasOwn(p,'text_contains'))checks.text=(await page.locator('body').innerText({timeout:4000})).includes(string(p.text_contains,1000));
    if(Object.hasOwn(p,'url'))checks.url=page.url()===string(p.url,4096);
    return result({verified:Object.values(checks).every(Boolean),checks,url:page.url()});
  }else if(op==='scroll'){
    object(p,['delta_x','delta_y'],['delta_y']);resetSnapshot();
    await page.mouse.wheel(integer(p.delta_x??0,-2000,2000),integer(p.delta_y,-2000,2000));
  }else{
    const keys={click:['ref','button','count'],fill:['ref','text'],press:['ref','key'],select:['ref','value'],hover:['ref']}[op];
    object(p,keys,['ref',...(op==='fill'?['text']:op==='press'?['key']:op==='select'?['value']:[])]);
    const h=await ref(p);
    const options={timeout:5000};
    if(op==='click'){const button=p.button??'left';if(!['left','right'].includes(button))fail('invalid_button');await h.click({...options,button,clickCount:integer(p.count??1,1,2)})}
    if(op==='fill'){if(await h.evaluate(e=>e.type==='file'))fail('file_input_not_supported');await h.fill(string(p.text),options)}
    if(op==='press')await h.press(string(p.key,64),options);
    if(op==='select')await h.selectOption({value:string(p.value,1024)},options);
    if(op==='hover')await h.hover(options);
  }
  assertPage();
  return result({dispatched:true,verification_required:true,url:page.url()});
}
async function guestRPC(tool,args,timeout=10000){
  const id=crypto.randomUUID();
  const response=await fetch('http://127.0.0.1:'+cfg.port+'/mcp',{
    method:'POST',redirect:'error',signal:AbortSignal.timeout(timeout),
    headers:{'Content-Type':'application/json','X-FileMCP-Local-Token':guestToken},
    body:JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:tool,arguments:args}})
  });
  if(!response.ok||!response.body)fail('guest_unavailable');
  const reader=response.body.getReader();let chunks=[],size=0;
  for(;;){const {done,value}=await reader.read();if(done)break;size+=value.length;if(size>12_000_000){await reader.cancel();fail('guest_response_too_large')}chunks.push(Buffer.from(value))}
  const r=JSON.parse(Buffer.concat(chunks).toString());
  if(r.id!==id||r.error||!r.result||r.result.isError)fail('guest_call_refused: '+String(r.result?.content?.[0]?.text??'RPC failure').slice(0,500));
  if(!Array.isArray(r.result.content)||!r.result.structuredContent)fail('invalid_guest_response');
  return r.result;
}
async function guestOpen(){
  integer(cfg.port,1024,65535);
  const file=fs.lstatSync(cfg.tokenFile);
  if(!file.isFile()||file.isSymbolicLink()||file.size>4096||process.platform!=='win32'&&(file.mode&0o077)!==0)fail('invalid_guest_token_file');
  guestToken=fs.readFileSync(cfg.tokenFile,'utf8').trim();
  if(!/^[a-f0-9]{64}$/.test(guestToken))fail('invalid_guest_token');
  const r=await guestRPC('cua_capabilities',{});
  if(r.structuredContent.guest_mode!==true||r.structuredContent.enabled!==true)fail('not_an_enabled_isolated_guest_gateway');
  return {kind:'guest',guest_mode:true,capabilities:r.structuredContent};
}
async function guestCall(op,p){
  if(op==='close'){object(p,[]);await shutdown();return result({closed:true})}
  if(!guestOps.has(op))fail('unsupported_guest_operation');
  if(!p||Array.isArray(p)||typeof p!=='object')fail('invalid_parameters');
  if(['cua_observe','cua_act','cua_verify','cua_stop_session'].includes(op)&&!guestSessions.has(p.session_id))refuse('guest_session_not_owned');
  if(op==='cua_receipt_status'&&!guestRequests.has(p.request_id))refuse('guest_receipt_not_owned');
  if(['cua_act','cua_launch_app'].includes(op))guestRequests.add(string(p.request_id,128));
  const r=await guestRPC(op,p);
  if(op==='cua_create_session')guestSessions.add(string(r.structuredContent.session_id,128));
  if(op==='cua_stop_session')guestSessions.delete(p.session_id);
  return r;
}
async function shutdown(){
  if(closing)return;closing=true;
  if(cfg.kind==='guest'&&guestToken){
    await Promise.allSettled([...guestSessions].map(session_id=>guestRPC('cua_stop_session',{session_id},1800)));guestSessions.clear();
  }
  if(browser)await browser.close().catch(()=>{});
  proxy?.close();
}
async function call(name,args){
  if(name==='extension_open'){
    object(args,[]);if(opened)fail('extension_already_open');
    const out=cfg.kind==='browser'?await browserOpen():await guestOpen();opened=true;return result(out);
  }
  if(name!=='extension_call'||!opened||closing)fail('extension_not_open');
  object(args,['operation','parameters'],['operation','parameters']);
  return cfg.kind==='browser'?await browserCall(string(args.operation,64),args.parameters):await guestCall(string(args.operation,64),args.parameters);
}
const rl=readline.createInterface({input:process.stdin,crlfDelay:Infinity});
for(const sig of ['SIGTERM','SIGINT'])process.on(sig,()=>{void shutdown().finally(()=>process.exit(0))});
try{
  for await(const line of rl){
    if(Buffer.byteLength(line)>128000)break;
    let msg;
    try{
      msg=JSON.parse(line);
      if(msg.id===undefined)continue;
      const out=msg.method==='initialize'?{protocolVersion:'2025-06-18',serverInfo:{name:'ctcmcp-cua-extension',version:'1.0.0'}}:
        msg.method==='tools/call'?await call(msg.params.name,msg.params.arguments):fail('unsupported_method');
      process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:msg.id,result:out})+'\n');
    }catch(e){
      if(msg?.id!==undefined)process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:msg.id,result:{isError:true,structuredContent:{recoverable:e instanceof Refusal},content:[{type:'text',text:String(e.message).slice(0,500)}]}})+'\n');
    }
  }
}finally{await shutdown()}
