#!/bin/bash
# 打 macOS 安装包：先编通用版 .app，再出 zip 和 dmg（放到 dist/）
# dmg 带卷图标，产物文件本身也挂上 App 图标（Finder 里好看）
# 用法：./scripts/package-macos.sh
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)"
ICON="Resources/AppIcon.icns"
APP="build/MonitorAudioBridge.app"
VOLNAME="音频桥 $VERSION"
WORK="$(mktemp -d /tmp/mab-pkg.XXXXXX)"
STAGE="$WORK/$VOLNAME"
RWDMG="$WORK/rw.dmg"
trap 'rm -rf "$WORK"' EXIT

# 给单个文件挂自定义图标（Finder 里显示成 App 图标）
set_file_icon() {
    local f="$1"
    printf "read 'icns' (-16455) \"%s\";\n" "$PWD/$ICON" > "$WORK/icon.r"
    sips -i "$PWD/$ICON" >/dev/null 2>&1 || true
    DeRez -only icns "$PWD/$ICON" > "$WORK/icon.r" 2>/dev/null || return 0
    Rez -append "$WORK/icon.r" -o "$f" >/dev/null 2>&1 || return 0
    SetFile -a C "$f"
}

./build.sh >/dev/null
mkdir -p "$STAGE" dist
cp -R "$APP" "$STAGE/音频桥.app"
cp scripts/macos-install-notes.txt "$STAGE/安装说明.txt"

echo "==> zip"
rm -f "dist/音频桥-$VERSION-macOS-universal.zip"
ditto -c -k --norsrc --keepParent "$STAGE" "dist/音频桥-$VERSION-macOS-universal.zip"
set_file_icon "dist/音频桥-$VERSION-macOS-universal.zip"

echo "==> dmg（带卷图标）"
rm -f "$RWDMG" "dist/音频桥-$VERSION-macOS-universal.dmg"
hdiutil create -size 16m -fs HFS+ -volname "$VOLNAME" "$RWDMG" >/dev/null
MOUNT="/Volumes/$VOLNAME"
hdiutil attach "$RWDMG" -nobrowse >/dev/null
cp -R "$STAGE/音频桥.app" "$MOUNT/音频桥.app"
cp "$STAGE/安装说明.txt" "$MOUNT/"
ln -s /Applications "$MOUNT/Applications"
cp "$ICON" "$MOUNT/.VolumeIcon.icns"
SetFile -c icnC "$MOUNT/.VolumeIcon.icns"
SetFile -a C "$MOUNT"
case "$(GetFileInfo -a "$MOUNT")" in *C*) echo "    卷图标已设置";; *) echo "    ! 卷图标没设上";; esac
hdiutil detach "$MOUNT" >/dev/null
hdiutil convert "$RWDMG" -format UDZO -o "dist/音频桥-$VERSION-macOS-universal.dmg" >/dev/null
set_file_icon "dist/音频桥-$VERSION-macOS-universal.dmg"
set_file_icon "dist/音频桥-$VERSION-Windows-x64-setup.exe"

# 上传到 GitHub Release 时用 ASCII 文件名（gh 会丢掉非 ASCII 字符），
# 例如：cp "dist/音频桥-$VERSION-macOS-universal.dmg" "/tmp/AudioBridge-$VERSION-macOS-universal.dmg"
ls -lh dist/
