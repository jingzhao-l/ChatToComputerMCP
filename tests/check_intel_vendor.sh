#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
test "$(cat vendor/tunnel-client/VERSION)" = "0.0.12"
test "$(cat vendor/ripgrep/VERSION)" = "15.2.0"
test "$(shasum -a 256 vendor/tunnel-client/darwin-amd64/tunnel-client | awk '{print $1}')" = "4133dab2575223252732a998210c34b7ed96a51765cf5ea835a8e24cf2be1272"
test "$(shasum -a 256 vendor/tunnel-client/darwin-amd64/THIRD-PARTY-LICENSES.txt | awk '{print $1}')" = "661df2b81de81ec60df1820e2f152f120ad9fd14455c116c159b3bc9041d0ba2"
test "$(shasum -a 256 vendor/ripgrep/darwin-amd64/rg | awk '{print $1}')" = "0c9a0066db0d26b640777db88045b0ccdd58509a746700e43e1c4ff8707a5ed0"
test -x vendor/tunnel-client/darwin-amd64/tunnel-client
test -x vendor/ripgrep/darwin-amd64/rg
if [[ "$(uname -m)" = x86_64 ]]; then
    vendor/tunnel-client/darwin-amd64/tunnel-client --version | grep -q '^0.0.12+881c9a8fed7cccbe6607cd419863bbca506b8215 '
    vendor/ripgrep/darwin-amd64/rg --version | grep -q '^ripgrep 15.2.0 '
fi
echo "Intel vendor integrity: ok"
