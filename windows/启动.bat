@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

if exist AudioBridge.exe goto run

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo 找不到 csc.exe。这台机器缺 .NET Framework 4，Win10/11 自带，Win7 需要装一个。
    pause
    exit /b 1
)

echo 正在编译（几秒钟）...
rem /codepage:65001 不能省：源码是 UTF-8，不加的话中文界面会变成乱码
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:app.ico /out:AudioBridge.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Protocol.cs Ring.cs Net.cs Wasapi.cs Model.cs Ui.cs Program.cs
if errorlevel 1 (
    echo 编译失败。把上面的报错发给开发者。
    pause
    exit /b 1
)

:run
start "" AudioBridge.exe %*
