#!/bin/zsh
# Regression test for Capture/ScrollStitcher.swift (no XCTest needed; works with Command Line Tools only).
set -euo pipefail
ROOT="${0:A:h:h}"
BIN="$(mktemp -d)/stitcher-test"
trap 'rm -rf "${BIN:h}"' EXIT
swiftc -O -o "$BIN" "$ROOT/Sources/SuYanShot/Capture/ScrollStitcher.swift" "$ROOT/scripts/stitcher-test/main.swift"
failed=0
for seed in 42 7 1234 99 2026 5 77 31337; do
    "$BIN" $seed || failed=1
    "$BIN" $seed edge || failed=1
done
if (( failed )); then echo "✗ stitcher test failed"; exit 1; fi
echo "✓ stitcher test passed"
