// SPDX-License-Identifier: Apache-2.0
// Private stdio worker. No arbitrary code execution, user browser profile,
// filesystem tools, listener or caller-selected executable.
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import readline from 'node:readline';
import crypto from 'node:crypto';
import path from 'node:path';

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
const browserOps=new Set(['observe','navigate','click','fill','press','select','scroll','verify','wait_for','back','forward','hover','drag','tab_list','tab_open','tab_select','tab_close','dialog_prepare','upload','download','close']);
const guestOps=new Set(['cua_capabilities','cua_list_apps','cua_list_windows','cua_launch_app','cua_create_session','cua_observe','cua_act','cua_verify','cua_stop_session','cua_receipt_status']);
let browser,context,page,originSet,proxy,snapshot=null,guestSessions=new Set(),opened=false,closing=false;
const guestRequests=new Set();
const tabs=new Map(),byPage=new Map();
const MAX_TABS=8;
let activeTab=null;
let guestToken;
function origin(url){
  const u=new URL(string(url,4096));
  if(!['http:','https:'].includes(u.protocol)||u.username||u.password)fail('origin_not_allowed');
  return u.origin;
}
function allowedURL(url){try{return originSet.has(origin(url))}catch{return false}}
function tabState(){
  const t=tabs.get(activeTab);
  if(!t||t.page.isClosed()||t.page!==page)fail('browser_closed');
  return t;
}
function assertPage(){
  tabState();
  if(page.url()!=='about:blank'&&!allowedURL(page.url()))fail('origin_not_allowed');
}
function allowedFrame(frame){
  for(let f=frame;f;f=f.parentFrame()){
    if(f.isDetached()||(!['about:blank','about:srcdoc'].includes(f.url())&&!allowedURL(f.url())))return false;
  }
  return true;
}
function resetSnapshot(keep=[]){
  const previous=snapshot;snapshot=null;
  if(previous)for(const entry of previous.refs.values())
    if(!keep.includes(entry.handle))void entry.handle.dispose().catch(()=>{});
}
function selectTab(id){
  const t=tabs.get(id);
  if(!t||t.page.isClosed())refuse('unknown_browser_tab');
  if(t.page.url()!=='about:blank'&&!allowedURL(t.page.url()))refuse('origin_not_allowed');
  resetSnapshot();activeTab=id;page=t.page;
}
function registerTab(p){
  if(byPage.has(p))return byPage.get(p);
  if(tabs.size>=MAX_TABS){void p.close().catch(()=>{});return null}
  const id=crypto.randomBytes(16).toString('hex');
  const t={id,page:p,epoch:0,dialogPolicy:null,lastDialog:null,downloadArmed:false};
  tabs.set(id,t);byPage.set(p,id);
  p.on('close',()=>{
    tabs.delete(id);byPage.delete(p);
    if(activeTab===id){
      resetSnapshot();activeTab=null;page=null;
      const next=[...tabs.values()].find(x=>!x.page.isClosed()&&(x.page.url()==='about:blank'||allowedURL(x.page.url())));
      if(next)selectTab(next.id);
    }
  });
  for(const name of ['framenavigated','framedetached','frameattached'])p.on(name,()=>{
    t.epoch++;if(activeTab===id)resetSnapshot();
  });
  p.on('dialog',async d=>{
    const policy=t.dialogPolicy;t.dialogPolicy=null;
    const match=policy&&performance.now()<policy.expires&&policy.type===d.type()&&policy.message===d.message();
    const action=match?policy.action:'dismiss';
    t.lastDialog={type:d.type(),message:d.message().slice(0,1000),action,matched:!!match};
    try{if(action==='accept')await d.accept(policy.prompt_text);else await d.dismiss()}catch{t.lastDialog.action='failed'}
  });
  p.on('download',d=>{if(!t.downloadArmed)void d.cancel().catch(()=>{});else t.downloadArmed=false});
  return id;
}
async function tabList(){
  return {active_tab_id:activeTab,tabs:await Promise.all([...tabs.values()].map(async t=>({
    tab_id:t.id,url:t.page.url()==='about:blank'||allowedURL(t.page.url())?t.page.url():null,
    title:allowedURL(t.page.url())?(await t.page.title().catch(()=>'' )).slice(0,300):'',
    blocked:t.page.url()!=='about:blank'&&!allowedURL(t.page.url())
  })))};
}
async function createProxy(){
  const sockets=new Set();
  const username=crypto.randomBytes(16).toString('hex'),password=crypto.randomBytes(32).toString('hex');
  const auth='Basic '+Buffer.from(username+':'+password).toString('base64');
  function authorized(req){
    const actual=Buffer.from(req.headers['proxy-authorization']??''),expected=Buffer.from(auth);
    return actual.length===expected.length&&crypto.timingSafeEqual(actual,expected);
  }
  const server=http.createServer((req,res)=>{
    try{
      if(!authorized(req)){res.writeHead(407,{'Proxy-Authenticate':'Basic realm="CUA"'}).end();return}
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
      if(!authorized(req)){client.end('HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm="CUA"\r\nContent-Length: 0\r\n\r\n');return}
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
  return {address:'http://127.0.0.1:'+server.address().port,username,password,close(){for(const s of sockets)s.destroy();server.close()}};
}
async function browserOpen(){
  if(!Array.isArray(cfg.origins)||!cfg.origins.length||cfg.origins.length>32)fail('invalid_origin_grants');
  originSet=new Set(cfg.origins.map(o=>{if(origin(o)!==o)fail('invalid_origin_grant');return o}));
  const {chromium}=await import('playwright');
  proxy=await createProxy();
  browser=await chromium.launch({headless:true,chromiumSandbox:true,timeout:8000,
    proxy:{server:proxy.address,username:proxy.username,password:proxy.password,bypass:'<-loopback>'},
    args:['--disable-quic','--force-webrtc-ip-handling-policy=disable_non_proxied_udp']});
  context=await browser.newContext({viewport:{width:1280,height:800},serviceWorkers:'block',acceptDownloads:cfg.allowFiles===true});
  // Chromium routes ordinary HTTP and HTTPS through the private allowlist
  // proxy, including redirect hops. Route checks add defense in depth.
  await context.route('**/*',async route=>{
    if(!allowedURL(route.request().url()))return await route.abort('blockedbyclient').catch(()=>{});
    return await route.continue().catch(()=>{});
  });
  // WebSocket traffic is not exposed by this version; no alternate bypass.
  await context.routeWebSocket('**/*',socket=>socket.close({code:1008,reason:'WebSocket not enabled for this profile'}));
  context.on('page',registerTab);
  const initial=await context.newPage();
  selectTab(registerTab(initial));
  return {kind:'browser',headless:true,profile_isolated:true,origins:[...originSet],allow_files:cfg.allowFiles===true,url:page.url(),...(await tabList())};
}
async function observe(params){
  object(params,['include_screenshot']);
  const image=params.include_screenshot===undefined?true:boolean(params.include_screenshot);
  assertPage();resetSnapshot();
  const tab=tabState(),epoch=tab.epoch;
  const elements=[],frames=[],refs=new Map(),id=crypto.randomBytes(16).toString('hex');
  let truncated=false;
  try{
    for(const frame of page.frames().slice(0,16)){
      if(!allowedFrame(frame))continue;
      const frameId=id+':frame:'+frames.length;
      frames.push({frame_id:frameId,url:frame.url(),text:(await frame.locator('body').innerText({timeout:800}).catch(()=>'' )).slice(0,4000)});
      const all=frame.locator('a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=link],[role=checkbox],[role=radio],[role=combobox],[contenteditable=true]');
      const handles=await all.elementHandles();
      for(const h of handles.slice(0,200)){
        if(elements.length>=200||!await h.isVisible().catch(()=>false)){await h.dispose();continue}
        const meta=await h.evaluate(e=>({tag:e.tagName.toLowerCase(),role:e.getAttribute('role')||'',name:(e.getAttribute('aria-label')||e.getAttribute('placeholder')||e.innerText||e.getAttribute('title')||'').trim().slice(0,500),type:e.getAttribute('type')||'',disabled:!!e.disabled,value:e.type==='password'?'':('value'in e?String(e.value).slice(0,500):''),checked:'checked'in e?!!e.checked:null})).catch(()=>null);
        if(!meta){await h.dispose();continue}
        const ref=id+':'+elements.length;
        refs.set(ref,{handle:h,frame});elements.push({ref,frame_id:frameId,...meta});
      }
      for(const h of handles.slice(200))await h.dispose();
      if(handles.length>200||elements.length>=200){truncated=true;break}
    }
    const value={tab_id:tab.id,url:page.url(),title:(await page.title()).slice(0,1000),
      text:frames.map(f=>f.text).join('\n').slice(0,24000),elements,frames,truncated:truncated||page.frames().length>16,snapshot_id:id,last_dialog:tab.lastDialog};
    const blocks=[];
    if(image){
      const png=await page.screenshot({type:'png',timeout:5000,animations:'disabled'});
      if(png.length>5_000_000)fail('browser_screenshot_too_large');
      blocks.push({type:'image',mimeType:'image/png',data:png.toString('base64')});
    }
    if(activeTab!==tab.id||tab.epoch!==epoch||!allowedFrame(page.mainFrame()))refuse('page_changed_observe_again');
    snapshot={id,refs,tab:tab.id,epoch,url:page.url(),at:performance.now()};
    return result(value,blocks);
  }catch(e){for(const entry of refs.values())await entry.handle.dispose().catch(()=>{});throw e}
}
async function resolveRefs(names){
  assertPage();
  const t=tabState();
  if(!snapshot||snapshot.tab!==t.id||snapshot.epoch!==t.epoch||snapshot.url!==page.url()||performance.now()-snapshot.at>30000)refuse('observe_again');
  const found=[];
  for(const name of names){
    const entry=snapshot.refs.get(string(name,128));
    if(!entry||!allowedFrame(entry.frame)||!await entry.handle.evaluate(e=>e.isConnected).catch(()=>false)||!await entry.handle.isVisible().catch(()=>false))
      refuse('stale_or_foreign_browser_ref');
    found.push(entry);
  }
  if(!snapshot||snapshot.tab!==t.id||snapshot.epoch!==t.epoch)refuse('page_changed_observe_again');
  resetSnapshot(found.map(e=>e.handle));return found;
}
async function verify(p,wait){
  object(p,wait?['text_contains','url','timeout_ms']:['text_contains','url']);
  if(!Object.hasOwn(p,'text_contains')&&!Object.hasOwn(p,'url'))refuse('verification_predicate_required');
  const text=Object.hasOwn(p,'text_contains')?string(p.text_contains,1000):null;
  const url=Object.hasOwn(p,'url')?string(p.url,4096):null;
  const deadline=performance.now()+(wait?integer(p.timeout_ms??3000,0,6000):0);
  let checks;
  do{
    assertPage();checks={};
    if(text!==null){
      const values=await Promise.all(page.frames().filter(allowedFrame).slice(0,16).map(f=>f.locator('body').innerText({timeout:500}).catch(()=>'')));
      checks.text=values.some(v=>v.includes(text));
    }
    if(url!==null)checks.url=page.url()===url;
    if(Object.values(checks).every(Boolean)||performance.now()>=deadline)break;
    await new Promise(r=>setTimeout(r,100));
  }while(true);
  return result({verified:Object.values(checks).every(Boolean),checks,url:page.url()});
}
function artifactName(v){
  const name=string(v,120);
  if(!/^[A-Za-z0-9][A-Za-z0-9_. -]{0,119}$/.test(name)||name==='.'||name==='..')refuse('invalid_artifact_name');
  return name;
}
function uploadPayload(p){
  if(cfg.allowFiles!==true)refuse('browser_file_transfer_not_granted');
  const name=artifactName(p.name),data=string(p.data,24000),mimeType=string(p.mime_type??'application/octet-stream',120);
  const buffer=Buffer.from(data,'base64');
  if(buffer.length>16000||buffer.toString('base64')!==data||!/^[A-Za-z0-9.+-]+\/[A-Za-z0-9.+-]+$/.test(mimeType))
    refuse('invalid_artifact_payload');
  return {name,mimeType,buffer};
}
async function downloadArtifact(h,tab){
  if(cfg.allowFiles!==true)refuse('browser_file_transfer_not_granted');
  tab.downloadArmed=true;
  let download;
  try{
    [download]=await Promise.all([page.waitForEvent('download',{timeout:5000}),h.click({timeout:5000})]);
    const stream=await download.createReadStream();
    let total=0;const chunks=[];
    for await(const bytes of stream){
      total+=bytes.length;
      if(total>128000){stream.destroy();await download.cancel().catch(()=>{});fail('browser_download_too_large')}
      chunks.push(bytes);
    }
    return result({name:path.basename(download.suggestedFilename()).slice(0,120),encoding:'base64',size:total,data:Buffer.concat(chunks).toString('base64')});
  }finally{tab.downloadArmed=false;if(download)await download.delete().catch(()=>{})}
}
async function browserCall(op,p){
  if(!browserOps.has(op))refuse('unsupported_browser_operation');
  if(op==='tab_list'){object(p,[]);return result(await tabList())}
  if(op==='tab_select'){object(p,['tab_id'],['tab_id']);selectTab(string(p.tab_id,128));return result(await tabList())}
  if(op==='tab_open'){
    object(p,['url']);
    if(p.url!==undefined&&!allowedURL(p.url))refuse('origin_not_allowed');
    if(tabs.size>=MAX_TABS)refuse('browser_tab_capacity');
    const next=await context.newPage();selectTab(registerTab(next));
    if(p.url!==undefined)await next.goto(p.url,{waitUntil:'domcontentloaded',timeout:7000});
    assertPage();return result(await tabList());
  }
  if(op==='tab_close'){
    object(p,['tab_id'],['tab_id']);const t=tabs.get(string(p.tab_id,128));
    if(!t||tabs.size<=1)refuse('cannot_close_unknown_or_last_tab');
    await t.page.close();return result(await tabList());
  }
  assertPage();
  if(op==='observe')return await observe(p);
  if(op==='close'){object(p,[]);await shutdown();return result({closed:true})}
  if(op==='navigate'){
    object(p,['url'],['url']);if(!allowedURL(p.url))refuse('origin_not_allowed');
    resetSnapshot();await page.goto(p.url,{waitUntil:'domcontentloaded',timeout:7000});assertPage();
  }else if(op==='back'||op==='forward'){
    object(p,[]);resetSnapshot();await (op==='back'?page.goBack:page.goForward).call(page,{waitUntil:'domcontentloaded',timeout:7000});assertPage();
  }else if(op==='verify'||op==='wait_for')return await verify(p,op==='wait_for');
  else if(op==='dialog_prepare'){
    object(p,['type','message','action','prompt_text'],['type','message','action']);
    if(!['alert','confirm','prompt','beforeunload'].includes(p.type)||!['accept','dismiss'].includes(p.action)||
       (p.prompt_text!==undefined&&(p.type!=='prompt'||p.action!=='accept')))refuse('invalid_dialog_policy');
    tabState().dialogPolicy={type:p.type,message:string(p.message,1000),action:p.action,
      prompt_text:p.prompt_text===undefined?undefined:string(p.prompt_text,1000),expires:performance.now()+30000};
    return result({armed:true,expires_in_seconds:30});
  }else if(op==='scroll'){
    object(p,['delta_x','delta_y'],['delta_y']);const x=integer(p.delta_x??0,-2000,2000),y=integer(p.delta_y,-2000,2000);
    resetSnapshot();await page.mouse.wheel(x,y);
  }else{
    const keys={click:['ref','button','count'],fill:['ref','text'],press:['ref','key'],select:['ref','value'],hover:['ref'],drag:['ref','destination_ref'],upload:['ref','name','mime_type','data'],download:['ref']}[op];
    object(p,keys,['ref',...(op==='fill'?['text']:op==='press'?['key']:op==='select'?['value']:op==='drag'?['destination_ref']:op==='upload'?['name','data']:[])]);
    let button,count,txt,key,value,payload;
    if(op==='click'){button=p.button??'left';if(!['left','right'].includes(button))refuse('invalid_button');count=integer(p.count??1,1,2)}
    if(op==='fill')txt=string(p.text);
    if(op==='press')key=string(p.key,64);
    if(op==='select')value=string(p.value,1024);
    if(op==='upload')payload=uploadPayload(p);
    if(op==='download'&&cfg.allowFiles!==true)refuse('browser_file_transfer_not_granted');
    const refs=await resolveRefs(op==='drag'?[p.ref,p.destination_ref]:[p.ref]),h=refs[0].handle;
    const options={timeout:5000};
    try{
      if(op==='click')await h.click({...options,button,clickCount:count});
      if(op==='fill'){if(await h.evaluate(e=>e.type==='file'))refuse('file_input_not_supported');await h.fill(txt,options)}
      if(op==='press')await h.press(key,options);
      if(op==='select')await h.selectOption({value},options);
      if(op==='hover')await h.hover(options);
      if(op==='upload'){if(!await h.evaluate(e=>e.tagName==='INPUT'&&e.type==='file'))refuse('not_file_input');await h.setInputFiles(payload,options)}
      if(op==='download')return await downloadArtifact(h,tabState());
      if(op==='drag'){
        if(refs[0].frame!==refs[1].frame)refuse('drag_requires_same_frame');
        const [a,b]=await Promise.all(refs.map(r=>r.handle.boundingBox()));
        const v=page.viewportSize();
        const point=r=>r&&({x:r.x+r.width/2,y:r.y+r.height/2});
        const from=point(a),to=point(b);
        if(!v||![from,to].every(q=>q&&q.x>=0&&q.y>=0&&q.x<v.width&&q.y<v.height))refuse('drag_points_outside_viewport');
        await page.mouse.move(from.x,from.y);await page.mouse.down();
        try{await page.mouse.move(to.x,to.y,{steps:12});await page.mouse.move(to.x,to.y)}finally{await page.mouse.up()}
      }
    }finally{for(const r of refs)await r.handle.dispose().catch(()=>{})}
  }
  assertPage();
  return result({dispatched:true,verification_required:true,tab_id:activeTab,url:page.url(),last_dialog:tabState().lastDialog});
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
