; 音频桥 Windows 安装包（NSIS 3.x，在 macOS/Linux 上用 makensis 交叉编译）
; 用法：makensis -INPUTCHARSET UTF8 scripts/windows-installer.nsi
Unicode true

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define APP_NAME "音频桥"
!define APP_NAME_EN "Audio Bridge"
!define APP_VER "3.0.2"
!define APP_EXE "AudioBridge.exe"
!define APP_PUBLISHER "大肥鱼"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioBridge"

Name "${APP_NAME} ${APP_NAME_EN} ${APP_VER}"
OutFile "..\dist\音频桥-${APP_VER}-Windows-x64-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\AudioBridge"
InstallDirRegKey HKCU "Software\AudioBridge" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APP_NAME} ${APP_VER} · 局域网实时音频桥"

VIProductVersion "${APP_VER}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME} ${APP_NAME_EN}"
VIAddVersionKey /LANG=2052 "FileDescription" "局域网实时音频桥"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VER}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VER}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "MIT License"

!define MUI_ICON "..\windows\app.ico"
!define MUI_UNICON "..\windows\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行${APP_NAME}"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\用前必读.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "先看一遍用前必读"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Section "AudioBridge" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File "..\windows\${APP_EXE}"
  File "..\windows\用前必读.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  WriteRegStr HKCU "Software\AudioBridge" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME} ${APP_NAME_EN}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VER}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\用前必读.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\AudioBridge"
SectionEnd
