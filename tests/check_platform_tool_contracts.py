#!/usr/bin/env python3
"""Source-level MCP tool name parity guard (not an E2E parity claim).

Each platform implements its tools independently; this catches accidental
API additions on only one platform. Functional tests remain separately
required for both systems and all privileged actions.
"""
from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent
mac = (root / 'macos/LocalMCPServer.swift').read_text()
windows = '\n'.join((root / f).read_text() for f in (
    'windows/src/FileMCP.Core/LocalTools.cs',
    'windows/src/FileMCP.Core/AgentTools.cs',
))
mac_names = set(re.findall(r'\bname:\s*"([a-z_]+)"', mac))
windows_names = set(re.findall(r'\bTool\("([a-z_]+)"', windows))
# Non-tool fields in OAuth metadata and MCP examples.
mac_names.difference_update({'code', 'iss', 'state'})
required = {
    'list_files', 'read_file', 'read_file_range', 'file_info',
    'grep', 'glob', 'search_code', 'repo_overview',
    'write_file', 'edit_file', 'apply_patch',
    'create_directory', 'copy_file', 'move_file',
    'delete_file', 'delete_directory',
    'git_init', 'git_status', 'git_log', 'git_diff',
    'git_add', 'git_commit', 'git_push',
    'run_command', 'start_command', 'command_receipt_status', 'read_command_output',
    'cancel_command', 'command_capacity_status', 'list_my_commands',
    'workspace_context', 'batch_read', 'save_conversation_to_codex',
}
mac_cua = set(re.findall(r'\("(cua_[a-z_]+)",', (root / 'macos/CUA.swift').read_text()))
win_cua = set(re.findall(r'\bDef\("(cua_[a-z_]+)",', (root / 'windows/src/FileMCP.Core/CuaService.cs').read_text()))
cua = {'cua_capabilities','cua_list_apps','cua_list_windows','cua_create_session','cua_list_sessions',
       'cua_observe','cua_act','cua_verify','cua_stop_session','cua_launch_app','cua_receipt_status','cua_extension_list','cua_extension_open','cua_extension_call','cua_extension_close'}
assert mac_cua == win_cua == cua, (mac_cua, win_cua, cua)
if mac_names != windows_names or mac_names != required:
    raise SystemExit(
        'MCP tool names differ across platforms:\n'
        f'Mac only: {sorted(mac_names - windows_names)}\n'
        f'Windows only: {sorted(windows_names - mac_names)}\n'
        f'Unexpected: {sorted((mac_names | windows_names) - required)}\n'
        f'Missing: {sorted(required - (mac_names & windows_names))}'
    )
print(f'cross-platform MCP tool names: {len(required)} coding + {len(cua)} CUA matched')
