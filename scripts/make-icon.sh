#!/bin/bash
# 重新生成图标（Mac 上跑）。源码目录带中文时 swift 可能崩，所以在 /tmp 里干活。
set -euo pipefail
cd "$(dirname "$0")/.."
TMP="$(mktemp -d /tmp/mab-icon.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT
cp scripts/make-icon.swift "$TMP/main.swift"
( cd "$TMP" && swiftc -O -o mkicon main.swift -framework AppKit && ./mkicon "$TMP" )
iconutil -c icns "$TMP/AppIcon.iconset" -o Resources/AppIcon.icns
cp "$TMP/app.ico" windows/app.ico
mkdir -p .out && cp "$TMP/icon-preview.png" .out/icon-preview.png
echo "==> Resources/AppIcon.icns, windows/app.ico"
