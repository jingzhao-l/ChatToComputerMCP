#!/usr/bin/env bash
# Fast development launcher for the full native Swift ChatToComputerMCP app.
set -Eeuo pipefail
cd "$(dirname "$0")"

ROOT="$PWD"
BUILD_DIR="${MCP_MACOS_DEV_BUILD_DIR:-$ROOT/build/macos-dev}"
APP="$BUILD_DIR/ChatToComputerMCP.app"
UI_BIN="$APP/Contents/MacOS/ChatToComputerMCP"
SWIFTC="${SWIFTC:-swiftc}"

if ! command -v "$SWIFTC" >/dev/null 2>&1; then
    echo "ERROR: swiftc is required. Install Xcode Command Line Tools or Xcode." >&2
    exit 1
fi

ARCH="$(uname -m)"
case "$ARCH" in
    arm64|aarch64) TARGET_TAG="darwin-arm64" ;;
    x86_64|amd64) TARGET_TAG="darwin-amd64" ;;
    *) echo "ERROR: unsupported macOS architecture: $ARCH" >&2; exit 1 ;;
esac

TUNNEL_BIN="$ROOT/vendor/tunnel-client/$TARGET_TAG/tunnel-client"
RG_BIN="$ROOT/vendor/ripgrep/$TARGET_TAG/rg"
for required_binary in "$TUNNEL_BIN" "$RG_BIN"; do
    if [ ! -f "$required_binary" ]; then
        echo "ERROR: missing bundled executable for $TARGET_TAG: $required_binary" >&2
        exit 1
    fi
done

"$ROOT/build_macos_icon.sh"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$ROOT/macos/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/build/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
cp "$ROOT/assets/branding/chat-to-computer-mcp/app-icon-128.png" "$APP/Contents/Resources/ChatToComputerMCP-login.png"
cp "$TUNNEL_BIN" "$APP/Contents/MacOS/tunnel-client"
chmod 755 "$APP/Contents/MacOS/tunnel-client"
cp "$RG_BIN" "$APP/Contents/MacOS/rg"
chmod 755 "$APP/Contents/MacOS/rg"

echo "Compiling full Swift macOS app ..."
"$SWIFTC" \
    -Onone \
    -g \
    -framework AppKit \
    -framework Network \
    -framework Security \
    -o "$UI_BIN" \
    "$ROOT/macos/ProcessRunner.swift" \
    "$ROOT/macos/Ripgrep.swift" \
    "$ROOT/macos/CUA.swift" \
    "$ROOT/macos/LocalMCPServer.swift" \
    "$ROOT/macos/CodexHistory.swift" \
    "$ROOT/macos/CommandReceiptLedger.swift" \
    "$ROOT/macos/CommandHistoryStore.swift" \
    "$ROOT/macos/ActivityAuditStore.swift" \
    "$ROOT/macos/CommandHistoryView.swift" \
    "$ROOT/macos/ActivityAuditView.swift" \
    "$ROOT/macos/LocalMCPRuntime.swift" \
    "$ROOT/macos/LaunchAgentService.swift" \
    "$ROOT/macos/AppUpdates.swift" \
    "$ROOT/macos/ChatToComputerMCPApp.swift" \
    "$ROOT/macos/main.swift"

echo "Starting ChatToComputerMCP dev app"
echo "  App bundle:    $APP"
echo "  Swift binary:  $UI_BIN"
echo "  tunnel-client: $APP/Contents/MacOS/tunnel-client"
echo "  ripgrep:       $APP/Contents/MacOS/rg"
echo
exec "$UI_BIN"
