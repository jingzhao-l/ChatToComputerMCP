# Base MCP tool contract (generated)

Generated from [the shared machine-readable contract](../contracts/mcp-tools.json).
This is the non-CUA tool inventory, not a runtime certification.
CUA has its own independent [reference](CUA.md).
Actual discovery also depends on the locally approved workspace and OAuth grants.

| Permission profile | Advertised base tools |
| --- | ---: |
| default | 27 |
| full | 36 |
| read_only | 16 |

| Tool | Required arguments | Default | Full | Read-only | Read-only hint |
| --- | --- | --- | --- | --- | --- |
| `apply_patch` | `changes` | Yes | Yes | — | No |
| `batch_read` | `operations` | Yes | Yes | Yes | Yes |
| `cancel_command` | `session_id` | — | Yes | — | No |
| `command_capacity_status` | — | — | Yes | — | Yes |
| `command_receipt_status` | `request_id` | — | Yes | — | Yes |
| `connection_identity` | — | Yes | Yes | Yes | Yes |
| `copy_file` | `destination_path`, `source_path` | Yes | Yes | — | No |
| `create_directory` | `relative_path` | Yes | Yes | — | No |
| `delete_directory` | `relative_path` | Yes | Yes | — | No |
| `delete_file` | `relative_path` | Yes | Yes | — | No |
| `edit_file` | `new_text`, `old_text`, `relative_path` | Yes | Yes | — | No |
| `file_info` | `relative_path` | Yes | Yes | Yes | Yes |
| `git_add` | — | Yes | Yes | — | No |
| `git_commit` | — | Yes | Yes | — | No |
| `git_diff` | — | Yes | Yes | Yes | Yes |
| `git_init` | — | Yes | Yes | — | No |
| `git_log` | — | Yes | Yes | Yes | Yes |
| `git_push` | — | — | Yes | — | No |
| `git_status` | — | Yes | Yes | Yes | Yes |
| `glob` | `pattern` | Yes | Yes | Yes | Yes |
| `grep` | `pattern` | Yes | Yes | Yes | Yes |
| `list_files` | — | Yes | Yes | Yes | Yes |
| `list_my_commands` | — | — | Yes | — | Yes |
| `list_workspaces` | — | Yes | Yes | Yes | Yes |
| `move_file` | `destination_path`, `source_path` | Yes | Yes | — | No |
| `read_command_output` | `session_id` | — | Yes | — | Yes |
| `read_file` | `relative_path` | Yes | Yes | Yes | Yes |
| `read_file_range` | `end_line`, `relative_path`, `start_line` | Yes | Yes | Yes | Yes |
| `repo_overview` | — | Yes | Yes | Yes | Yes |
| `run_command` | `command` | — | Yes | — | No |
| `save_conversation_to_codex` | `messages`, `title` | — | Yes | — | No |
| `search_code` | `queries` | Yes | Yes | Yes | Yes |
| `start_command` | `command`, `request_id` | — | Yes | — | No |
| `workspace_call` | `arguments`, `tool_name`, `workspace_id` | Yes | Yes | Yes | No |
| `workspace_context` | — | Yes | Yes | Yes | Yes |
| `write_file` | `content`, `relative_path` | Yes | Yes | — | No |

The native fixtures compare complete input and output schemas, annotations,
and permission-profile availability against the same reviewed JSON.
When a contract changes intentionally, update both implementations, the JSON
and this generated inventory; run the native behavior regression suites as well.
Regenerate this file with `python3 tests/check_base_tool_docs.py --write`.
