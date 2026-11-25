#!/bin/bash
# 编译 Mac 端（只需要 Command Line Tools，不需要装 Xcode）
#
# 说明：源码目录名里带中文，swiftc 在某些沙盒/受限环境里会直接崩（Trace/BPT trap）。
# 所以统一把源码复制到一个纯 ASCII 的临时目录里编译，再把 .app 搬回来。
set -euo pipefail
cd "$(dirname "$0")"

ROOT="$PWD"
OUT="${1:-build}"
case "$OUT" in *[!A-Za-z0-9._-]*) echo "输出目录名不合法: $OUT"; exit 1;; esac
TMP="$(mktemp -d /tmp/mab-build.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/src" "$TMP/modcache"
cp Sources/*.swift "$TMP/src/"
cp Resources/Info.plist "$TMP/Info.plist"

echo "==> 编译"
( cd "$TMP/src" && swiftc -O -swift-version 5 -target arm64-apple-macosx15.0 \
    -module-cache-path "$TMP/modcache" \
    -framework AppKit -framework SwiftUI -framework AVFoundation -framework CoreAudio -framework AudioToolbox \
    -o "$TMP/MonitorAudioBridge" ./*.swift )

APP="$ROOT/$OUT/MonitorAudioBridge.app"
rm -rf "$ROOT/$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$TMP/MonitorAudioBridge" "$APP/Contents/MacOS/MonitorAudioBridge"
cp "$TMP/Info.plist" "$APP/Contents/Info.plist"
[ -f Resources/AppIcon.icns ] && cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
codesign --force --deep --sign - "$APP" 2>/dev/null && echo "==> 已 ad-hoc 签名" || echo "==> 跳过签名"

echo "==> 完成: $APP"
echo "    运行:  open '$APP'"
echo "    自测:  '$APP/Contents/MacOS/MonitorAudioBridge' --selftest"
