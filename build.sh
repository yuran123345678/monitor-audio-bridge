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

# 最低系统版本要和 Resources/Info.plist 的 LSMinimumSystemVersion 保持一致
MIN_MACOS="14.2"

echo "==> 编译（arm64 + x86_64，最低 macOS $MIN_MACOS，产出通用二进制）"
for ARCH in arm64 x86_64; do
    echo "    - $ARCH"
    ( cd "$TMP/src" && swiftc -O -swift-version 5 -target "$ARCH-apple-macosx$MIN_MACOS" \
        -module-cache-path "$TMP/modcache-$ARCH" \
        -framework AppKit -framework SwiftUI -framework AVFoundation -framework CoreAudio -framework AudioToolbox \
        -o "$TMP/bin-$ARCH" ./*.swift )
done
lipo -create "$TMP/bin-arm64" "$TMP/bin-x86_64" -output "$TMP/MonitorAudioBridge"

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
