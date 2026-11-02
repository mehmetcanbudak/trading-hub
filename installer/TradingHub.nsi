; Per-user Windows installer: no administrator rights, Start/Desktop shortcuts, uninstall entry and optional launch.
; Installer language follows Windows (Spanish or Simplified Chinese), otherwise English.
; Build with build-windows.ps1 or build-windows.sh using .NET SDK 10 and NSIS:
;   makensis -DVERSION=1.2.0 -DARCH=x64 -DSRC=<published-app-directory> -DOUT=<installer.exe> installer/TradingHub.nsi
; Updates/uninstall leave %APPDATA%\Trading Hub, Documents\Trading Hub and saved credentials untouched.

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrStr}

!ifndef VERSION
  !define VERSION "1.2.0"
!endif
!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef SRC
  !error "Specify -DSRC=<published-app-directory>"
!endif
!ifndef OUT
  !define OUT "TradingHub-Setup-${ARCH}.exe"
!endif

!define NAME "Trading Hub"
!define EXE "TradingHub.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TradingHub"

Name "${NAME}"
OutFile "${OUT}"
; A dedicated application directory keeps update/uninstall away from user data.
InstallDir "$LOCALAPPDATA\Programs\Trading Hub"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ManifestDPIAware true
BrandingText "Trading Hub ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${NAME}"
VIAddVersionKey "FileDescription" "${NAME} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License with Commons Clause"

!define MUI_ICON "..\src\TradingHub.App\Assets\tradinghub.ico"
!define MUI_UNICON "..\src\TradingHub.App\Assets\tradinghub.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "$(OpenApp)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
; English is the fallback installer language.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Spanish"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString WelcomeText ${LANG_ENGLISH} "This installs Trading Hub ${VERSION} on your PC.$\r$\n$\r$\nIf you already have it, it gets updated: your connections, charts, strategies and indicators stay as they are.$\r$\n$\r$\nClick Install to continue."
LangString WelcomeText ${LANG_SPANISH} "Esto instala Trading Hub ${VERSION} en tu PC.$\r$\n$\r$\nSi ya la tenés, se actualiza: tus conexiones, gráficos, estrategias e indicadores quedan como están.$\r$\n$\r$\nTocá Instalar para seguir."
LangString WelcomeText ${LANG_SIMPCHINESE} "这将在你的电脑上安装 Trading Hub ${VERSION}。$\r$\n$\r$\n如果已经安装，会直接更新：你的连接、图表、策略和指标都保持不变。$\r$\n$\r$\n点击“安装”继续。"
LangString OpenApp ${LANG_ENGLISH} "Open Trading Hub"
LangString OpenApp ${LANG_SPANISH} "Abrir Trading Hub"
LangString OpenApp ${LANG_SIMPCHINESE} "打开 Trading Hub"
LangString StillOpen ${LANG_ENGLISH} "Trading Hub is open. Close it (if you have trades or strategies running, leave them as you want first) and click Retry."
LangString StillOpen ${LANG_SPANISH} "Trading Hub está abierta. Cerrala (si tenés operaciones o estrategias corriendo, primero dejalas como quieras) y tocá Reintentar."
LangString StillOpen ${LANG_SIMPCHINESE} "Trading Hub 正在运行。请先关闭它（如果有交易或策略在运行，请先按你的需要处理好），然后点击“重试”。"
LangString StillOpenUn ${LANG_ENGLISH} "Trading Hub is open. Close it and click Retry."
LangString StillOpenUn ${LANG_SPANISH} "Trading Hub está abierta. Cerrala y tocá Reintentar."
LangString StillOpenUn ${LANG_SIMPCHINESE} "Trading Hub 正在运行。请关闭它，然后点击“重试”。"

; Ask the user to quit safely; never force-close an app that could be managing orders.
Function WaitClosed
  ${Do}
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${StrStr} $2 $1 "${EXE}"
    ${If} $2 == ""
      ${Break}
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(StillOpen)" /SD IDCANCEL IDRETRY +2
    Abort
  ${Loop}
FunctionEnd

Function un.WaitClosed
  ${Do}
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${UnStrStr} $2 $1 "${EXE}"
    ${If} $2 == ""
      ${Break}
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(StillOpenUn)" /SD IDCANCEL IDRETRY +2
    Abort
  ${Loop}
FunctionEnd

; Remove only an application directory named Trading Hub containing its executable.
; An explicit /D pointing elsewhere must not cause unrelated-directory removal.
Function CleanInstallDir
  ${GetFileName} "$INSTDIR" $0
  ${If} $0 == "Trading Hub"
  ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
    SetOutPath "$TEMP"
    RMDir /r "$INSTDIR"
  ${EndIf}
FunctionEnd

Function un.CleanInstallDir
  ${GetFileName} "$INSTDIR" $0
  ${If} $0 == "Trading Hub"
  ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
    SetOutPath "$TEMP"
    RMDir /r "$INSTDIR"
  ${Else}
    ; Preserve unrelated directories; retain the upstream uninstaller filename as a compatibility identity.
    Delete "$INSTDIR\Desinstalar Trading Hub.exe"
  ${EndIf}
FunctionEnd

Section "Trading Hub" SecMain
  Call WaitClosed
  ; Replace the application in its own directory, never its data or strategy directories.
  Call CleanInstallDir
  SetOutPath "$INSTDIR"
  File /r "${SRC}\*.*"
  WriteUninstaller "$INSTDIR\Desinstalar Trading Hub.exe"

  CreateShortcut "$SMPROGRAMS\Trading Hub.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortcut "$DESKTOP\Trading Hub.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0

  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${NAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Trading Hub"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://github.com/mehmetcanbudak/trading-hub"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Desinstalar Trading Hub.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Desinstalar Trading Hub.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Call un.WaitClosed
  Delete "$SMPROGRAMS\Trading Hub.lnk"
  Delete "$DESKTOP\Trading Hub.lnk"
  Call un.CleanInstallDir
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd
