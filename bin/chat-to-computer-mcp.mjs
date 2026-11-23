#!/usr/bin/env node
/* SPDX-License-Identifier: Apache-2.0 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import url from 'node:url';
import cp from 'node:child_process';
import {validVersion,compareVersions,registry,packageName,
  installedVersion,policyFile,readPolicy,writePolicy,activateMacRelease
} from './update-support.mjs';

const root = path.dirname(path.dirname(url.fileURLToPath(import.meta.url)));
const metadata = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const version = metadata.version;
const name = 'ChatToComputerMCP';
const label = 'local.chat-to-computer-mcp';
const platform = process.platform;
const home = os.homedir();
const dataHome = platform === 'win32'
  ? path.join(process.env.LOCALAPPDATA || path.join(home, 'AppData', 'Local'), name)
  : path.join(home, '.local', 'share', 'chat-to-computer-mcp');
const releaseParent = path.join(dataHome, 'releases');
const versionDir = path.join(releaseParent, version);
const app = platform === 'win32'
  ? path.join(versionDir, name + '.exe')
  : path.join(versionDir, name + '.app');
const agentPlist = path.join(home, 'Library', 'LaunchAgents', label + '.plist');

function help() {
  console.log([
    'ChatToComputerMCP v' + version,
    'Usage:',
    ' chat-to-computer-mcp install [--rebuild]',
    ' chat-to-computer-mcp open',
    ' chat-to-computer-mcp status',
    ' chat-to-computer-mcp update check',
    ' chat-to-computer-mcp update install',
    ' chat-to-computer-mcp update status',
    ' chat-to-computer-mcp update enable [--auto-install]',
    ' chat-to-computer-mcp update disable',
    ' chat-to-computer-mcp launchagent enable [--always] [--auto-connect]',
    ' chat-to-computer-mcp launchagent status',
    ' chat-to-computer-mcp launchagent disable',
    ' chat-to-computer-mcp startup enable [--always] [--auto-connect] (Windows)',
    ' chat-to-computer-mcp startup status (Windows)',
    ' chat-to-computer-mcp startup disable (Windows)',
    '',
    'One-step local compilation after npm release:',
    ' npx --yes chat-to-computer-mcp@latest install',
    '',
    'Requirements: macOS with Swift/Xcode Command Line Tools or Windows with .NET 8 SDK.',
    'Update install builds a new version without replacing the running app.',
    'Automatic update checking and background installation are opt-in.',
    'Install never launches a service; LaunchAgent is explicitly opt-in.',
  ].join('\n'));
}
function run(exe,args,options={}) {
  const result = cp.spawnSync(exe,args,{stdio:'inherit',...options});
  if(result.error) throw new Error('Could not launch ' + exe + ': ' + result.error.message);
  if(result.status !== 0) throw new Error(exe + ' exited with ' + String(result.status));
}
function probe(exe,args) {
  const r = cp.spawnSync(exe,args,{encoding:'utf8',stdio:['ignore','pipe','pipe']});
  return {ok:!r.error && r.status===0,text:(r.stdout||'').trim()};
}
function mkdir(p) { fs.mkdirSync(p,{recursive:true,mode:0o700}); }
function copy(source,dest) {
  if(!fs.existsSync(source)) throw new Error('Missing npm package file: ' + path.relative(root,source));
  fs.cpSync(source,dest,{recursive:true,force:false,errorOnExist:true,verbatimSymlinks:true});
}
function macBuild(stage) {
  for(const item of ['macos','vendor','assets','build_macos_app.sh','build_macos_icon.sh','LICENSE']) {
    if(item==='assets') {
      mkdir(path.join(stage,'assets','branding'));
      copy(path.join(root,'assets','branding','chat-to-computer-mcp'),
           path.join(stage,'assets','branding','chat-to-computer-mcp'));
      copy(path.join(root,'assets','branding','render_chat_to_computer_mcp_icon.swift'),
           path.join(stage,'assets','branding','render_chat_to_computer_mcp_icon.swift'));
      continue;
    }
    copy(path.join(root,item),path.join(stage,item));
  }
  run('/bin/bash',['build_macos_app.sh'],{cwd:stage,env:process.env,timeout:900000});
  const built=path.join(stage,'dist',name+'.app');
  if(!fs.existsSync(path.join(built,'Contents','MacOS',name))) throw new Error('Mac executable missing after build');
  return built;
}
function windowsBuild(stage) {
  for(const item of ['windows','vendor','build_windows_app.ps1','LICENSE'])
    copy(path.join(root,item),path.join(stage,item));
  const arch = process.arch==='arm64' ? 'arm64' : 'x64';
  if(!['arm64','x64'].includes(process.arch)) throw new Error('Requires Windows x64 or arm64');
  run('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',
        'build_windows_app.ps1','-Architecture',arch],{cwd:stage,timeout:1200000});
  const built=path.join(stage,'dist','windows-'+arch,name);
  if(!fs.existsSync(path.join(built,name+'.exe'))) throw new Error('Windows executable missing after build');
  return built;
}
function install(args) {
  if(!['darwin','win32'].includes(platform)) throw new Error('macOS and Windows are supported');
  if(fs.existsSync(app) && !args.includes('--rebuild')) {
    // A first post-upgrade invocation can migrate an old installation to
    // the stable, version-independent LaunchAgent target without rebuilding.
    if(platform==='darwin' && selectedVersion()===version &&
       !fs.existsSync(path.join(releaseParent,'current')))
      activateMacRelease(releaseParent,version);
    console.log('Already installed at ' + app + '. Use install --rebuild for a new local build.');
    return;
  }
  mkdir(releaseParent);
  if(fs.existsSync(versionDir)) {
    if(platform==='darwin' && (agentStatus().loaded ||
       probe('/usr/bin/pgrep',['-f',target()]).ok))
      throw new Error('Installed app is active. Refusing to replace its bundle; disable its LaunchAgent and quit it before --rebuild.');
  }
  const stage=fs.mkdtempSync(path.join(releaseParent,'.building-'));
  console.log('Compiling ' + name + ' locally for ' + platform + '/' + process.arch);
  try {
    const built = platform==='darwin' ? macBuild(stage) : windowsBuild(stage);
    const completed=path.join(stage,'release');
    if(platform==='darwin') {
      mkdir(completed);
      fs.renameSync(built,path.join(completed,name+'.app'));
    } else {
      fs.renameSync(built,completed);
    }
    const expected=platform==='darwin'
      ? path.join(completed,name+'.app','Contents','MacOS',name)
      : path.join(completed,name+'.exe');
    if(!fs.existsSync(expected)) throw new Error('Build output layout is incomplete: '+expected);
    let backup;
    if(fs.existsSync(versionDir)) {
      backup=path.join(releaseParent,version+'-previous-'+Date.now());
      fs.renameSync(versionDir,backup);
      console.log('Prior build preserved: ' + backup);
    }
    try { fs.renameSync(completed,versionDir); }
    catch(e) {
      if(backup && !fs.existsSync(versionDir)) fs.renameSync(backup,versionDir);
      throw e;
    }
    if(platform==='darwin') {
      const active=installedVersion(releaseParent,platform);
      // A stale npm CLI may still install an older version, but cannot
      // silently replace a newer successfully activated application.
      if(!active || compareVersions(version,active)>=0)
        activateMacRelease(releaseParent,version);
    }
    if(platform==='darwin' && selectedVersion()===version)
      migrateExistingAgentPlist();
    if(platform==='win32') {
      const sync=cp.spawnSync(path.join(versionDir,name+'.exe'),['--sync-startup'],
        {timeout:15000,stdio:'ignore',windowsHide:true});
      if(sync.error||sync.status!==0)
        console.warn('Warning: could not update the existing Windows login registration. '+
                     'Review the login startup settings after opening the new app.');
    }
    console.log('Installed: ' + app);
    console.log('Not started. Use chat-to-computer-mcp open to launch.');
  } finally { fs.rmSync(stage,{recursive:true,force:true}); }
}
function selectedVersion() {
  return installedVersion(releaseParent,platform);
}
function selectedApp() {
  const selected=selectedVersion();
  return selected ? path.join(releaseParent,selected,
    platform==='win32'?name+'.exe':name+'.app') : app;
}
function target() {
  if(platform!=='darwin')return selectedApp();
  const pointer=path.join(releaseParent,'current');
  const isLink=fs.existsSync(pointer) && fs.lstatSync(pointer).isSymbolicLink();
  return path.join(isLink?pointer:selectedApp(),
    ...(isLink?[name+'.app']:[]),'Contents','MacOS',name);
}
function domain() { return 'gui/'+process.getuid(); }
function agentStatus() {
  if(platform!=='darwin') return {enabled:false,loaded:false};
  return {enabled:fs.existsSync(agentPlist),
          loaded:probe('/bin/launchctl',['print',domain()+'/'+label]).ok};
}
function status() {
  const selected=selectedApp();
  console.log('Version: '+version);
  console.log('Installed: '+(fs.existsSync(selected)?'yes':'no'));
  console.log('Installed version: '+(selectedVersion()??'none'));
  console.log('Path: '+selected);
  if(platform==='darwin') {
    const s=agentStatus();
    console.log('LaunchAgent: '+(s.enabled?'installed':'disabled')+', '+(s.loaded?'loaded':'not loaded'));
  }
}
function updaterNpm(args,options={}) {
  // The arguments are authored here; the only dynamic package version has
  // already passed strict SemVer validation, never from shell arguments.
  if(platform==='win32') {
    return cp.spawnSync(process.env.ComSpec||'cmd.exe',
      ['/d','/s','/c','npm '+args.join(' ')],
      {...options,windowsHide:true});
  }
  return cp.spawnSync('npm',args,options);
}
function registryLatest() {
  const result=updaterNpm(['view',packageName,'dist-tags.latest',
    '--json','--registry='+registry,'--fetch-retries=0','--fetch-timeout=20000'],
    {encoding:'utf8',stdio:['ignore','pipe','pipe'],timeout:30000});
  if(result.error||result.status!==0)
    throw Error('Cannot check npm Registry: '+(result.error?.message||
      (result.stderr||'npm view failed').slice(0,240)));
  let latest;
  try {latest=JSON.parse(result.stdout.trim());}
  catch {latest=result.stdout.trim();}
  if(!validVersion(latest))
    throw Error('npm latest is not a valid stable release version');
  return latest;
}
function update(action,args) {
  const file=policyFile(platform,home);
  if(action==='status') {
    const policy=readPolicy(file);
    console.log('Installed version: '+(selectedVersion()??'none'));
    console.log('Check automatically: '+(policy.checkAutomatically?'enabled':'disabled'));
    console.log('Install automatically: '+(policy.installAutomatically?'enabled':'disabled'));
    return;
  }
  if(action==='enable') {
    const autoInstall=args.includes('--auto-install');
    if(args.some(arg=>arg!=='--auto-install'))
      throw Error('Expected update enable [--auto-install]');
    writePolicy(file,{checkAutomatically:true,installAutomatically:autoInstall});
    console.log('Automatic update checks enabled; automatic installs '+
      (autoInstall?'enabled':'disabled')+'. The running app is never restarted.');
    return;
  }
  if(action==='disable') {
    writePolicy(file,{checkAutomatically:false,installAutomatically:false});
    console.log('Automatic update checks and installs disabled.');
    return;
  }
  if(!['check','install'].includes(action))
    throw Error('Expected update check, install, status, enable or disable');
  const current=selectedVersion();
  const latest=registryLatest();
  console.log('Installed version: '+(current??'none'));
  console.log('npm latest: '+latest);
  if(compareVersions(latest,version)<0 && !current) {
    console.log('This local source build is newer than npm latest; no downgrade.');
    return;
  }
  if(current && compareVersions(latest,current)<=0) {
    console.log('Already up to date.');
    return;
  }
  if(action==='check') {
    console.log('Update available. Run: chat-to-computer-mcp update install');
    return;
  }
  if(!current) {
    console.log('No installed app. Installing the current npm release.');
  }
  if(latest===version) {
    install([]);
  } else {
    const packageSpec=packageName+'@'+latest;
    const result=updaterNpm(['exec','--yes','--registry='+registry,
      '--package='+packageSpec,'--','chat-to-computer-mcp','install'],
      {stdio:'inherit',timeout:30*60*1000});
    if(result.error||result.status!==0)
      throw Error('Failed to build updated app: '+(result.error?.message||String(result.status)));
  }
  if(installedVersion(releaseParent,platform)!==latest)
    throw Error('Updater returned success but the expected release was not installed.');
  console.log('Version '+latest+' installed. Quit and reopen the desktop app to use it.');
  if(platform==='darwin'&&agentStatus().loaded)
    console.log('Loaded LaunchAgent is not restarted. The updated app starts at the next login.');
}
function xmlEscape(s) {
  return s.replaceAll('&','&amp;').replaceAll('<','&lt;')
          .replaceAll('>','&gt;').replaceAll('"','&quot;');
}
function launchPlistText(autoConnect,always) {
  const logs=path.join(home,'Library','Logs',name);
  mkdir(logs);
  return [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">',
    '<plist version="1.0"><dict>',
    '<key>Label</key><string>'+xmlEscape(label)+'</string>',
    '<key>ProgramArguments</key><array><string>'+xmlEscape(target())+'</string></array>',
    '<key>RunAtLoad</key><true/>',
    '<key>KeepAlive</key>'+(always?'<true/>':'<dict><key>SuccessfulExit</key><false/></dict>'),
    '<key>EnvironmentVariables</key><dict><key>FILEMCP_AUTO_CONNECT</key><string>'+(autoConnect?'1':'0')+'</string></dict>',
    '<key>StandardOutPath</key><string>'+xmlEscape(path.join(logs,'launchagent.stdout.log'))+'</string>',
    '<key>StandardErrorPath</key><string>'+xmlEscape(path.join(logs,'launchagent.stderr.log'))+'</string>',
    '</dict></plist>',
    ''
  ].join('\n');
}
function migrateExistingAgentPlist() {
  if(platform!=='darwin'||!fs.existsSync(agentPlist)) return;
  const original=fs.readFileSync(agentPlist,'utf8');
  if(!original.includes('<key>Label</key><string>'+label+'</string>'))
    throw Error('Existing ChatToComputerMCP LaunchAgent is not ours; refusing to update it');
  const autoConnect=original.includes(
    '<key>FILEMCP_AUTO_CONNECT</key><string>1</string>');
  const always=original.includes('<key>KeepAlive</key><true/>');
  const rewritten=launchPlistText(autoConnect,always);
  const temp=agentPlist+'.update-'+process.pid+'-'+Date.now();
  fs.writeFileSync(temp,rewritten,{flag:'wx',mode:0o600});
  try {
    run('/usr/bin/plutil',['-lint',temp],{stdio:'ignore'});
    fs.renameSync(temp,agentPlist);
  } finally {if(fs.existsSync(temp))fs.unlinkSync(temp);}
  console.log('LaunchAgent configuration prepared for the next user login; '+
    'the currently loaded service was not restarted.');
}
function launchagent(action,args) {
  if(platform!=='darwin') throw new Error('LaunchAgent is available only on macOS');
  if(action==='status') return status();
  const service=domain()+'/'+label;
  if(action==='disable') {
    if(agentStatus().loaded) run('/bin/launchctl',['bootout',service]);
    if(fs.existsSync(agentPlist)) fs.unlinkSync(agentPlist);
    console.log('Disabled '+label+'. The original local.filemcp remains untouched.');
    return;
  }
  if(action!=='enable') throw new Error('Expected launchagent enable, status or disable');
  if(!fs.existsSync(target())) throw new Error('Run install first');
  if(agentStatus().enabled || agentStatus().loaded)
    throw new Error('Already enabled; disable the existing ChatToComputerMCP agent before reconfiguring.');
  const autoConnect=args.includes('--auto-connect');
  const always=args.includes('--always');
  if(args.includes('--dry-run')) {
    console.log(launchPlistText(autoConnect,always));
    return;
  }
  if(autoConnect && probe('/bin/launchctl',['print',domain()+'/local.filemcp']).ok)
    throw new Error('Existing FileMCP is active: refusing automatic connection with a potentially shared tunnel profile. Create an independent profile and disable the old service deliberately before auto-connect.');
  mkdir(path.dirname(agentPlist));
  const tmp=agentPlist+'.tmp-'+process.pid;
  fs.writeFileSync(tmp,launchPlistText(autoConnect,always),{flag:'wx',mode:0o600});
  try {
    run('/usr/bin/plutil',['-lint',tmp]);
    fs.renameSync(tmp,agentPlist);
    try { run('/bin/launchctl',['bootstrap',domain(),agentPlist]); }
    catch(e) { fs.unlinkSync(agentPlist); throw e; }
    console.log('Enabled '+label+' ('+(always?'always-on':'restart-on-failure')
      +', auto-connect '+(autoConnect?'enabled':'disabled')+').');
    console.log('Disable: chat-to-computer-mcp launchagent disable');
  } finally { if(fs.existsSync(tmp)) fs.unlinkSync(tmp); }
}
function startup(action,args) {
  if(platform!=='win32')
    throw Error('Windows startup is available only on Windows. On macOS use launchagent.');
  if(action==='status') {
    const query=probe('reg.exe',['query',
      'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run',
      '/v','ChatToComputerMCP']);
    console.log('Windows login startup: '+(query.ok?'registered':'disabled'));
    return;
  }
  if(!['enable','disable'].includes(action)) throw Error('Expected startup enable, disable or status');
  if(action==='enable'&&args.some(x=>!['--always','--auto-connect'].includes(x)))
    throw Error('Expected startup enable [--always] [--auto-connect]');
  if(!fs.existsSync(selectedApp()))throw Error('Install the application locally before configuring login startup');
  const parameters=['--manage-startup',action];
  if(action==='enable') {
    if(args.includes('--always'))parameters.push('--always');
    if(args.includes('--auto-connect'))parameters.push('--auto-connect');
  }
  run(selectedApp(),parameters,{timeout:12000,windowsHide:true});
  console.log('Windows login startup '+(action==='enable'?'enabled':'disabled')+
    '. No currently running app or AI task was restarted.');
}

function main() {
  const [command='help',subcommand,...rest]=process.argv.slice(2);
  switch(command) {
    case 'help': case '--help':case '-h':return help();
    case 'version':case '--version':return console.log(version);
    case 'install':return install(process.argv.slice(3));
    case 'open':
      if(!fs.existsSync(selectedApp())) throw new Error('Run install first');
      if(platform==='darwin') return run('/usr/bin/open',['-a',selectedApp()]);
      // A native Windows GUI runs independently of the invoking terminal;
      // blocking the CLI here would make "open" appear hung until Quit.
      {
        const child=cp.spawn(selectedApp(),[],{stdio:'ignore',detached:true,windowsHide:false});
        child.on('error',e=>console.error('Could not launch desktop app: '+e.message));
        child.unref();
        console.log('Desktop application launch requested.');
        return;
      }
    case 'status':return status();
    case 'update':return update(subcommand,rest);
    case 'launchagent':return launchagent(subcommand,rest);
    case 'startup':return startup(subcommand,rest);
    default:throw new Error('Unknown command: '+command);
  }
}
try { main(); } catch(e) { console.error('ChatToComputerMCP: '+e.message);process.exitCode=1; }
