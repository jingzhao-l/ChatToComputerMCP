#!/usr/bin/env python3
"""Generate/check the public inventory from the reviewed base MCP contract."""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOC = ROOT / "docs/BASE_TOOL_CONTRACT.md"
DATA = ROOT / "contracts/mcp-tools.json"

def render():
    data = json.loads(DATA.read_text())
    tools, profiles = data["tools"], data["profiles"]
    rows = [
        "# Base MCP tool contract (generated)",
        "",
        "Generated from [the shared machine-readable contract](../contracts/mcp-tools.json).",
        "This is the non-CUA tool inventory, not a runtime certification.",
        "CUA has its own independent [reference](CUA.md).",
        "Actual discovery also depends on the locally approved workspace and OAuth grants.",
        "",
        "| Permission profile | Advertised base tools |",
        "| --- | ---: |",
    ]
    for name in ("default", "full", "read_only"):
        rows.append(f"| {name} | {len(profiles[name])} |")
    rows += ["", "| Tool | Required arguments | Default | Full | Read-only | Read-only hint |",
             "| --- | --- | --- | --- | --- | --- |"]
    for name, tool in sorted(tools.items()):
        args = ", ".join(f"`{arg}`" for arg in tool["inputSchema"]["required"]) or "—"
        available = ["Yes" if name in profiles[p] else "—" for p in ("default","full","read_only")]
        hint = "Yes" if tool["annotations"]["readOnlyHint"] else "No"
        rows.append(f"| `{name}` | {args} | " + " | ".join(available + [hint]) + " |")
    rows += ["", "The native fixtures compare complete input and output schemas, annotations,",
             "and permission-profile availability against the same reviewed JSON.",
             "When a contract changes intentionally, update both implementations, the JSON",
             "and this generated inventory; run the native behavior regression suites as well.",
             "Regenerate this file with `python3 tests/check_base_tool_docs.py --write`.", ""]
    return "\n".join(rows)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    content = render()
    if args.write:
        DOC.write_text(content)
    elif not DOC.exists() or DOC.read_text() != content:
        raise SystemExit("Generated base tool documentation drift; inspect the contract and regenerate.")
    print("Base MCP generated documentation: ok")
