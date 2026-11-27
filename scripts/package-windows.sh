#!/bin/bash
# 打 Windows 安装包（NSIS，产物：dist/音频桥-<版本>-Windows-x64-setup.exe）
#
# 前置条件：
#   1) 先编出 windows/AudioBridge.exe（在 Windows 上双击「启动.bat」会自动编，
#      或用 wine 里的 csc 手工编，见 docs/QUICKSTART.md）
#   2) 需要一个能用的 makensis（NSIS 3.x）
#
# 注意：Homebrew 装的 makensis 在 macOS 上会 std::bad_alloc 崩掉（v3.13 实测），
#       推荐下面两条路：
#         A. 在 Windows 上装 NSIS，然后 makensis -INPUTCHARSET UTF8 windows-installer.nsi
#         B. 在 macOS 上装 CrossOver/Wine，跑官方 nsis-3.x-setup.exe /S，
#            再用瓶子里 "C:\Program Files (x86)\NSIS\makensis.exe" 编
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f windows/AudioBridge.exe ] || { echo "缺少 windows/AudioBridge.exe，先编译 Windows 端"; exit 1; }
command -v makensis >/dev/null || { echo "找不到 makensis（见本文开头说明）"; exit 1; }

mkdir -p dist
makensis -INPUTCHARSET UTF8 scripts/windows-installer.nsi
ls -lh dist/*Windows*.exe
