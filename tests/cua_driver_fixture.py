#!/usr/bin/env python3
"""Deterministic stdio transport fixture. NOT a desktop/driver acceptance test."""
import json
import os
import sys
import time

manifest = json.load(open(os.environ["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"], encoding="utf-8"))
assert os.environ["CUA_DRIVER_PERMISSION_MODE"] == "bounded"
assert os.environ["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] == "1"
assert os.environ["DO_NOT_TRACK"] == "1"
assert manifest["resources"]["desktop"]["display"] is False
assert len(manifest["resources"]["apps"]) == 1
assert "get_desktop_state" not in manifest["allow"]["tools"]
assert sys.argv[1:] == ["mcp", "--direct"]
for line in sys.stdin:
    req = json.loads(line)
    if "id" not in req:
        continue
    if req["method"] == "initialize":
        out = {"protocolVersion":"2025-06-18","serverInfo":{"name":"cua-driver","version":"0.61.0"}}
    elif req["method"] == "tools/list":
        out = {"tools":[{"name":name,"inputSchema":{"type":"object","properties":props,"required":required}} for name,props,required in [
            ("get_window_state",{"pid":{},"window_id":{}},["pid","window_id"]),("hang",{},[]),("oversize",{},[])
        ]]}
    else:
        params = req["params"]
        if params["name"] == "hang":
            time.sleep(60)
        if params["name"] == "oversize":
            sys.stdout.write("x"*12000001+"\n")
            sys.stdout.flush()
            continue
        out = {"content":[{"type":"text","text":"fixture-only"}],
               "structuredContent":{"echo":params["arguments"],"manifest":manifest}}
    print(json.dumps({"jsonrpc":"2.0","id":req["id"],"result":out}), flush=True)
