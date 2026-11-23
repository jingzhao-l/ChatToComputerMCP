#!/usr/bin/env python3
"""Mac-only, credential-free fault injection against the real vendored tunnel-client.

Starts an independent loopback-only control plane with a fake tunnel ID and a
dummy API key. Never touches the user's live tunnel, network settings, Keychain,
or LaunchAgent. Proves that actual control-plane polling survives TCP resets
without restarting the tunnel-client process.
"""
from __future__ import annotations

import http.server
import json
import os
from pathlib import Path
import platform
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

if sys.platform != "darwin":
    print("tunnel-transport-recovery: skipped (macOS-only)")
    raise SystemExit(0)

MACHINE = platform.machine().lower()
ARCH = "darwin-arm64" if MACHINE in ("arm64", "aarch64") else "darwin-amd64"
ROOT = Path(__file__).resolve().parents[1]
CLIENT = ROOT / "vendor" / "tunnel-client" / ARCH / "tunnel-client"
if not CLIENT.is_file() or not os.access(CLIENT, os.X_OK):
    raise SystemExit(f"vendored tunnel-client is missing or not executable: {CLIENT}")

TUNNEL_ID = "tunnel_0123456789abcdef0123456789abcdef"
STATE = {"mode": "up", "ok": 0, "reset": 0}
GUARD = threading.Lock()


class FakeControlPlane(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        if self.path.startswith(f"/v1/tunnels/{TUNNEL_ID}/poll?"):
            with GUARD:
                fault = STATE["mode"] == "down"
                if fault:
                    STATE["reset"] += 1
            if fault:
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                self.connection.close()
                return
            time.sleep(0.25)
            payload = b'{"commands":[]}'
            polled = True
        elif self.path == f"/v1/tunnels/{TUNNEL_ID}":
            payload = json.dumps({"id": TUNNEL_ID, "name": "isolated-health-test"}).encode()
            polled = False
        else:
            self.send_error(404)
            return

        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            if polled:
                with GUARD:
                    STATE["ok"] += 1
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, *_args: object) -> None:
        pass


def counters() -> tuple[int, int]:
    with GUARD:
        return STATE["ok"], STATE["reset"]


def wait_for(predicate, duration: float) -> bool:
    deadline = time.monotonic() + duration
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.12)
    return predicate()


def health_snapshot(path: Path, env: dict[str, str]) -> dict:
    check = subprocess.run(
        [str(CLIENT), "health", "--url-file", str(path),
         "--require-control-plane-poll", "--json"],
        env=env, text=True, capture_output=True, timeout=6, check=False,
    )
    if not check.stdout.strip().startswith("{"):
        raise AssertionError(f"health probe failed with status {check.returncode}")
    return json.loads(check.stdout)


def poll_timestamp(data: dict) -> float:
    return float((data.get("control_plane_poll") or {}).get("value") or 0)


def main() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FakeControlPlane)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with tempfile.TemporaryDirectory(prefix="ctcmcp-net-fault-") as tmp:
            scratch = Path(tmp)
            health_url = scratch / "health.url"
            child_log = scratch / "tunnel.log"
            env = os.environ.copy()
            for name in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                         "http_proxy", "https_proxy", "all_proxy",
                         "CONTROL_PLANE_HTTP_PROXY"):
                env.pop(name, None)
            env["CONTROL_PLANE_API_KEY"] = "dummy-local-test-credential"
            env["NO_PROXY"] = env["no_proxy"] = "127.0.0.1,localhost,::1"
            args = [
                str(CLIENT), "run", "--embedded-mcp-stub",
                "--control-plane.tunnel-id", TUNNEL_ID,
                "--control-plane.base-url", f"http://127.0.0.1:{server.server_port}",
                "--health.listen-addr", "127.0.0.1:0",
                "--health.url-file", str(health_url),
                "--control-plane.poll-timeout", "1s",
            ]
            with child_log.open("w") as log:
                child = subprocess.Popen(
                    args, env=env, stdin=subprocess.DEVNULL,
                    stdout=log, stderr=subprocess.STDOUT, start_new_session=True,
                )
                try:
                    assert wait_for(
                        lambda: health_url.is_file() and counters()[0] >= 4,
                        16.0), "isolated control-plane polling never became ready"
                    initial = health_snapshot(health_url, env)
                    assert initial.get("result") == "ok"
                    assert poll_timestamp(initial) > 0
                    assert child.poll() is None

                    # Inject a genuine TCP connection reset on the isolated
                    # control plane; the production tunnel is not affected.
                    with GUARD:
                        STATE["mode"] = "down"
                    assert wait_for(lambda: counters()[1] >= 2, 8.0), (
                        "TCP-reset outage was not observed by the client")
                    time.sleep(1.3)
                    failed = health_snapshot(health_url, env)
                    last_during_fault = poll_timestamp(failed)
                    ok_before_restore = counters()[0]
                    with GUARD:
                        STATE["mode"] = "up"

                    recovered = wait_for(
                        lambda: (
                            counters()[0] > ok_before_restore
                            and poll_timestamp(health_snapshot(health_url, env))
                                > last_during_fault
                        ),
                        24.0,
                    )
                    assert recovered, "tunnel-client did not resume cloud polling after TCP reset"
                    assert child.poll() is None, "tunnel-client process restarted during fault"
                    successes, resets = counters()
                    print(
                        f"tunnel-transport-recovery: ok "
                        f"(live vendor binary, TCP resets={resets}, "
                        f"successful polls={successes}, unchanged PID={child.pid})"
                    )
                except Exception:
                    # Print only local dummy-endpoint diagnostics on failure.
                    log.flush()
                    print(child_log.read_text(errors="replace")[-1800:], file=sys.stderr)
                    raise
                finally:
                    if child.poll() is None:
                        os.killpg(child.pid, signal.SIGTERM)
                        try:
                            child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            os.killpg(child.pid, signal.SIGKILL)
                            child.wait(timeout=5)
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
