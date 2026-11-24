#!/usr/bin/env python3
"""Compare actual native definitions with the reviewed shared base MCP contract.

The input must be the JSON emitted by one of the native fixtures. This checks
full input/output schema, annotations, and visibility under each permission
profile; descriptions and order of set-like JSON Schema keywords are ignored.
It does not infer runtime semantics or compare the separately maintained CUA.
"""
import argparse
import difflib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONTRACT = ROOT / "contracts/mcp-tools.json"

def normalize(value, key=None):
    if isinstance(value, dict):
        return {k: normalize(v, k) for k, v in value.items() if k not in {"description", "title"}}
    if isinstance(value, list):
        values = [normalize(v) for v in value]
        return sorted(values, key=str) if key in {"required", "enum", "type"} else values
    return value

def canonicalize(profiles):
    normalized = {}
    for profile, definitions in profiles.items():
        tools = {t["name"]: normalize(t) for t in definitions}
        if len(tools) != len(definitions):
            raise ValueError(f"{profile}: duplicate published tool name")
        normalized[profile] = tools
    return normalized

def expected_profiles(contract):
    assert contract["schema_version"] == 1
    return {profile: {name: contract["tools"][name] for name in names}
            for profile, names in contract["profiles"].items()}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("actual", type=Path)
    args = parser.parse_args()
    contract = json.loads(CONTRACT.read_text())
    expected = expected_profiles(contract)
    actual = canonicalize(json.loads(args.actual.read_text(encoding="utf-8-sig")))
    if actual != expected:
        lines = difflib.unified_diff(
            json.dumps(expected, sort_keys=True, indent=2).splitlines(),
            json.dumps(actual, sort_keys=True, indent=2).splitlines(),
            fromfile="reviewed shared contract", tofile=str(args.actual))
        print("\n".join(lines))
        raise SystemExit("Base MCP contract drift. Review the implementation and contract; do not blindly regenerate.")
    print("Base MCP contract matched: " + ", ".join(f"{p}={len(ts)}" for p, ts in actual.items()))

if __name__ == "__main__":
    main()
