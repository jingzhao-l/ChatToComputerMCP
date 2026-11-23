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
    'run_command', 'start_command', 'read_command_output',
    'cancel_command', 'command_capacity_status',
    'workspace_context', 'batch_read', 'save_conversation_to_codex',
}
if mac_names != windows_names or mac_names != required:
    raise SystemExit(
        'MCP tool names differ across platforms:\n'
        f'Mac only: {sorted(mac_names - windows_names)}\n'
        f'Windows only: {sorted(windows_names - mac_names)}\n'
        f'Unexpected: {sorted((mac_names | windows_names) - required)}\n'
        f'Missing: {sorted(required - (mac_names & windows_names))}'
    )
print(f'cross-platform MCP tool names: {len(required)} matched')
