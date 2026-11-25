@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem 改过 .cs 源码之后用这个：删掉旧的 exe 重新编译并启动。先从托盘退出正在运行的音频桥。
taskkill /im AudioBridge.exe /f >nul 2>&1
if exist AudioBridge.exe del AudioBridge.exe
call "%~dp0启动.bat"
