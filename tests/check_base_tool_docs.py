#!/usr/bin/env python3
"""Generate/check the public inventory from the reviewed base MCP contract."""
import argparse
import json
import re
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

def check_desktop_areas(write):
    data = json.loads((ROOT / "contracts/desktop-areas.json").read_text())
    areas = data["areas"]
    assert data["schema_version"] == 1 and len({a["name"] for a in areas}) == len(areas)
    names = [a["name"] for a in areas]
    # Check real WPF tab headings and AppKit navigation IDs, not just docs.
    win = (ROOT / "windows/src/FileMCP.App/MainWindow.xaml").read_text()
    mac = (ROOT / "macos/ChatToComputerMCPApp.swift").read_text()
    actual_windows = re.findall(r'<TabItem Header="([^"]+)"', win)
    wide = [a["wide"] for a in areas]
    compact = [a["compact"] for a in areas]
    if actual_windows != wide:
        raise SystemExit(f"Windows desktop area drift: {actual_windows}")
    def labels(match):
        if not match:
            raise SystemExit("Native navigation declaration missing")
        return re.findall(r'"([^"]+)"', match.group(1))
    if labels(re.search(r'let sectionNames = \[([^\]]+)\]', mac)) != wide:
        raise SystemExit("Mac wide desktop area drift")
    if labels(re.search(r'NSSegmentedControl\(labels: \[([^\]]+)\]', mac)) != compact:
        raise SystemExit("Mac compact desktop area drift")
    win_cs = (ROOT / "windows/src/FileMCP.App/MainWindow.xaml.cs").read_text()
    if labels(re.search(r'string\[\] names = compact \? \[([^\]]+)\]', win_cs)) != compact:
        raise SystemExit("Windows compact desktop area drift")
    if labels(re.search(r'string\[\] names = compact \? \[[^\]]+\]\s*:\s*\[([^\]]+)\]', win_cs)) != wide:
        raise SystemExit("Windows wide desktop area drift")
    begin, end = "<!-- BEGIN GENERATED DESKTOP AREAS -->", "<!-- END GENERATED DESKTOP AREAS -->"
    for filename, locale, header in (("README.md", "en", "| Page | Key controls |"),
                                     ("README.zh-CN.md", "zh", "| 页面 | 主要功能 |")):
        p = ROOT / filename
        current = p.read_text()
        table = "\n".join([begin, header, "|---|---|"] +
            [f"| **{a['wide']}**" + (f" ({a['compact']})" if a['wide'] != a['compact'] else "") + f" | {a[locale]} |" for a in areas] + [end])
        if current.count(begin) != 1 or current.count(end) != 1:
            raise SystemExit(f"{filename}: missing/duplicate generated section")
        target = current[:current.index(begin)] + table + current[current.index(end)+len(end):]
        if write:
            p.write_text(target)
        elif current != target:
            raise SystemExit(f"{filename}: generated desktop area drift")
    print("Bilingual desktop documentation: ok")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    content = render()
    if args.write:
        DOC.write_text(content)
    elif not DOC.exists() or DOC.read_text() != content:
        raise SystemExit("Generated base tool documentation drift; inspect the contract and regenerate.")
    check_desktop_areas(args.write)
    print("Base MCP generated documentation: ok")
