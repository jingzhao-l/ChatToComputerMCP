#!/usr/bin/env python3
"""Check actual MCP tool contracts by name, not fragile tools/list offsets."""
import json
import sys

result = json.load(sys.stdin)["result"]
assert result["resultType"] == "complete"
assert result["cacheScope"] == "private"
items = result["tools"]
names = [entry["name"] for entry in items]
assert len(names) == len(set(names)), "Duplicate tool names"
tools = {entry["name"]: entry for entry in items}
for required in (
    "list_files", "read_file", "read_file_range", "grep",
    "search_code", "repo_overview", "glob", "file_info",
    "create_directory", "copy_file", "move_file",
    "write_file", "delete_file", "delete_directory", "run_command",
    "batch_read", "save_conversation_to_codex", "command_capacity_status", "connection_identity", "list_my_commands",
):
    assert required in tools, f"Missing tool {required}"
assert tools["list_files"]["outputSchema"]["properties"]["result"]["type"] == "array"
assert tools["read_file"]["outputSchema"]["properties"]["result"]["type"] == "string"
assert tools["read_file_range"]["outputSchema"]["properties"]["content"]["type"] == "string"
assert "files_with_matches" in tools["grep"]["inputSchema"]["properties"]["output_mode"]["enum"]
assert tools["search_code"]["outputSchema"]["properties"]["query_results"]["type"] == "array"
assert tools["repo_overview"]["outputSchema"]["properties"]["manifests"]["type"] == "array"
assert tools["list_files"]["annotations"]["openWorldHint"] is False
assert tools["write_file"]["annotations"]["destructiveHint"] is True
assert tools["move_file"]["annotations"]["readOnlyHint"] is False
assert tools["run_command"]["annotations"]["openWorldHint"] is True
assert tools["save_conversation_to_codex"]["annotations"]["openWorldHint"] is False
assert tools["batch_read"]["annotations"]["readOnlyHint"] is True
assert tools["file_info"]["annotations"]["readOnlyHint"] is True
assert tools["command_capacity_status"]["annotations"]["readOnlyHint"] is True
assert "list_workspaces" in tools and "workspace_call" in tools
for name, definition in tools.items():
    if name in ("connection_identity", "list_workspaces", "workspace_call"):
        continue
    schema = definition["inputSchema"]
    assert schema["properties"]["workspace_id"]["type"] == "string", name
    assert "workspace_id" not in schema.get("required", []), name
print("MCP modern tool definitions validated by stable name:", len(tools), "tools")
