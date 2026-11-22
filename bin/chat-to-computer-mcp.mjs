#!/usr/bin/env node
/* SPDX-License-Identifier: Apache-2.0 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import url from 'node:url';
import cp from 'node:child_process';

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
    ' chat-to-computer-mcp launchagent enable [--always] [--auto-connect]',
    ' chat-to-computer-mcp launchagent status',
    ' chat-to-computer-mcp launchagent disable',
    '',
    'One-step local compilation after npm release:',
    ' npx --yes chat-to-computer-mcp@latest install',
    '',
    'Requirements: macOS with Swift/Xcode Command Line Tools or Windows with .NET 8 SDK.',
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
    console.log('Installed: ' + app);
    console.log('Not started. Use chat-to-computer-mcp open to launch.');
  } finally { fs.rmSync(stage,{recursive:true,force:true}); }
}
function target() { return path.join(app,'Contents','MacOS',name); }
function domain() { return 'gui/'+process.getuid(); }
function agentStatus() {
  if(platform!=='darwin') return {enabled:false,loaded:false};
  return {enabled:fs.existsSync(agentPlist),
          loaded:probe('/bin/launchctl',['print',domain()+'/'+label]).ok};
}
function status() {
  console.log('Version: '+version);
  console.log('Installed: '+(fs.existsSync(app)?'yes':'no'));
  console.log('Path: '+app);
  if(platform==='darwin') {
    const s=agentStatus();
    console.log('LaunchAgent: '+(s.enabled?'installed':'disabled')+', '+(s.loaded?'loaded':'not loaded'));
  }
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
function main() {
  const [command='help',subcommand,...rest]=process.argv.slice(2);
  switch(command) {
    case 'help': case '--help':case '-h':return help();
    case 'version':case '--version':return console.log(version);
    case 'install':return install(process.argv.slice(3));
    case 'open':
      if(!fs.existsSync(app)) throw new Error('Run install first');
      if(platform==='darwin') return run('/usr/bin/open',['-a',app]);
      // A native Windows GUI runs independently of the invoking terminal;
      // blocking the CLI here would make "open" appear hung until Quit.
      {
        const child=cp.spawn(app,[],{stdio:'ignore',detached:true,windowsHide:false});
        child.on('error',e=>console.error('Could not launch desktop app: '+e.message));
        child.unref();
        console.log('Desktop application launch requested.');
        return;
      }
    case 'status':return status();
    case 'launchagent':return launchagent(subcommand,rest);
    default:throw new Error('Unknown command: '+command);
  }
}
try { main(); } catch(e) { console.error('ChatToComputerMCP: '+e.message);process.exitCode=1; }
