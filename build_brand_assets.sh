#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")"
bash build_macos_icon.sh
brand="$PWD/assets/branding/chat-to-computer-mcp"
cp build/AppIcon.icns "$brand/ChatToComputerMCP.icns"
mkdir -p windows/assets
cp "$brand/ChatToComputerMCP.ico" windows/assets/chat-to-computer-mcp.ico
mkdir -p dist
python3 - "$brand" "$PWD/dist/ChatToComputerMCP-brand-kit.zip" <<'PYZIP'
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import sys
source,destination=map(Path,sys.argv[1:])
with ZipFile(destination,'w',compression=ZIP_DEFLATED) as z:
    for p in sorted(source.iterdir()):
        if p.suffix in {'.svg','.png','.icns','.ico'}:
            z.write(p,arcname='ChatToComputerMCP/'+p.name)
print('Built',destination)
PYZIP
