; ------------------------------------------------------------
;  StickyNotes Pro - Setup (NSIS 3, Modern UI 2)
;  Build:  makensis setup.nsi   ->  StickyNotesPro_Setup_1.0.0.exe
; ------------------------------------------------------------
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!define APPNAME      "StickyNotes Pro"
!define APPEXE       "StickyNotesPro.exe"
!define VERSION      "1.3.0"
!define PUBLISHER    "StickyNotes Pro"
!define UNINSTKEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\StickyNotesPro"
!define APPREGKEY    "Software\StickyNotesPro"

Name "${APPNAME} ${VERSION}"
OutFile "..\StickyNotesPro_Setup_${VERSION}.exe"

; ---------- Code signing (installer + uninstaller) ----------
; build/release.sh passes /DSIGNSCRIPT=<path to build/sign-file.sh> and /DEXE_SHA256=<hash of the signed exe>
!ifdef SIGNSCRIPT
  !finalize '"${SIGNSCRIPT}" "%1"' = 0
  !uninstfinalize '"${SIGNSCRIPT}" "%1"' = 0
!endif
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel admin
BrandingText "${APPNAME} ${VERSION} Setup"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName"     "${APPNAME}"
VIAddVersionKey "CompanyName"     "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APPNAME} Setup"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "LegalCopyright"  "(c) 2026 ${PUBLISHER}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

; ---------- Look ----------
!define MUI_ICON   "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP   "wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "wizard.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP   "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"
!define MUI_ABORTWARNING
!define MUI_ABORTWARNING_TEXT "Are you sure you want to cancel the ${APPNAME} setup?"

; ---------- Pages ----------
!define MUI_WELCOMEPAGE_TITLE "Welcome to the ${APPNAME} Setup"
!define MUI_WELCOMEPAGE_TEXT "This wizard will install ${APPNAME} ${VERSION} on your computer.$\r$\n$\r$\nSticky notes for your desktop with checklists, deadlines & alerts, toggle lists, backup & transfer, notes that stick to specific windows (like Google Chrome), tags, skins, encryption and more.$\r$\n$\r$\nYou must read and accept the Terms and Conditions on the next page before the setup can continue.$\r$\n$\r$\nIf an older version is installed it will be upgraded – your notes are kept.$\r$\n$\r$\nClick Next to continue."
!insertmacro MUI_PAGE_WELCOME

; Terms & Conditions - "I accept" must be selected, otherwise Next stays disabled
!define MUI_PAGE_HEADER_TEXT "Terms and Conditions"
!define MUI_PAGE_HEADER_SUBTEXT "Please read the License Agreement carefully before installing ${APPNAME}."
!define MUI_LICENSEPAGE_TEXT_TOP "Scroll down to read the whole agreement."
!define MUI_LICENSEPAGE_TEXT_BOTTOM "You must accept the Terms and Conditions to install ${APPNAME}. If you do not accept, the setup cannot continue."
!define MUI_LICENSEPAGE_RADIOBUTTONS
!define MUI_LICENSEPAGE_RADIOBUTTONS_TEXT_ACCEPT  "I &accept the terms of the License Agreement"
!define MUI_LICENSEPAGE_RADIOBUTTONS_TEXT_DECLINE "I &do not accept the terms of the License Agreement"
!insertmacro MUI_PAGE_LICENSE "LICENSE.rtf"

!define MUI_COMPONENTSPAGE_SMALLDESC
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_TITLE "${APPNAME} is installed"
!define MUI_FINISHPAGE_TEXT "${APPNAME} has been installed on your computer.$\r$\n$\r$\nIt runs in the system tray (next to the clock). Press Ctrl+Alt+N anytime to create a new sticky note."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APPNAME} now"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Show the quick start guide"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_FINISH

; Uninstaller pages
!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

; ---------- Checks ----------
Function .onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
  ; .NET Framework 4.8 (Release >= 528040) - included in Windows 10 (1903+) and Windows 11
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "${APPNAME} needs Microsoft .NET Framework 4.8, which was not found on this computer.$\r$\n$\r$\nOpen the Microsoft download page now?$\r$\n(You can continue the setup and install .NET 4.8 afterwards.)" IDNO +2
      ExecShell "open" "https://dotnet.microsoft.com/download/dotnet-framework/net48"
  ${EndIf}
FunctionEnd

Function LaunchApp
  ; start through Explorer so the app runs as the normal (non-elevated) user
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APPEXE}"'
FunctionEnd

!macro CloseRunningApp
  DetailPrint "Closing ${APPNAME} if it is running..."
  nsExec::Exec 'taskkill /F /IM ${APPEXE}'
  Pop $0
  Sleep 800
!macroend

; ---------- Sections ----------
Section "!${APPNAME} (required)" SecMain
  SectionIn RO
  !insertmacro CloseRunningApp
  SetOutPath "$INSTDIR"
  File "..\StickyNotesPro.exe"
  File "..\StickyNotesPro.exe.config"
  File "LICENSE.rtf"
  File "README.txt"
  File "app.ico"

  ; Tamper protection: record the SHA-256 of the signed executable in HKLM (writable by administrators only).
  ; StickyNotes Pro compares its own hash with this value at start-up and continuously while running.
!ifdef EXE_SHA256
  WriteRegStr HKLM "${APPREGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${APPREGKEY}" "ExeSha256" "${EXE_SHA256}"
  WriteRegStr HKLM "${APPREGKEY}" "Version" "${VERSION}"
!endif

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Start menu (all users)
  SetShellVarContext all
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Terms and Conditions.lnk" "$INSTDIR\LICENSE.rtf"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Quick start guide.lnk" "$INSTDIR\README.txt"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"

  ; Add/Remove Programs entry
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKLM "${UNINSTKEY}" "Publisher"       "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${APPEXE}"
  WriteRegStr   HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 400

  ; remember that the terms were accepted
  WriteRegStr HKCU "${APPREGKEY}" "LicenseAccepted" "1.0"
  WriteRegDWORD HKCU "${APPREGKEY}" "StartWithWindows" 0

  ; double-click a *.snpbackup file -> import it into StickyNotes Pro
  WriteRegStr HKLM "Software\Classes\.snpbackup" "" "StickyNotesPro.Backup"
  WriteRegStr HKLM "Software\Classes\StickyNotesPro.Backup" "" "StickyNotes Pro backup"
  WriteRegStr HKLM "Software\Classes\StickyNotesPro.Backup\DefaultIcon" "" "$INSTDIR\${APPEXE},0"
  WriteRegStr HKLM "Software\Classes\StickyNotesPro.Backup\shell\open\command" "" '"$INSTDIR\${APPEXE}" "%1"'
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section "Desktop shortcut" SecDesktop
  SetShellVarContext all
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPEXE}" 0
SectionEnd

Section "Start automatically with Windows" SecStartup
  WriteRegDWORD HKCU "${APPREGKEY}" "StartWithWindows" 1
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "StickyNotesPro" '"$INSTDIR\${APPEXE}"'
SectionEnd

LangString DESC_Main    ${LANG_ENGLISH} "The ${APPNAME} program files (required)."
LangString DESC_Desktop ${LANG_ENGLISH} "Put a ${APPNAME} shortcut on the desktop."
LangString DESC_Startup ${LANG_ENGLISH} "Start ${APPNAME} when you sign in to Windows, so your notes and deadline alerts are always active."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain}    $(DESC_Main)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_Desktop)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartup} $(DESC_Startup)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- Uninstaller ----------
Function un.onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
FunctionEnd

Section "Uninstall"
  !insertmacro CloseRunningApp

  Delete "$INSTDIR\${APPEXE}"
  Delete "$INSTDIR\${APPEXE}.config"
  Delete "$INSTDIR\LICENSE.rtf"
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\app.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  SetShellVarContext all
  Delete "$SMPROGRAMS\${APPNAME}\*.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"

  DeleteRegKey HKLM "${UNINSTKEY}"
  DeleteRegKey HKLM "${APPREGKEY}"
  DeleteRegKey HKLM "Software\Classes\.snpbackup"
  DeleteRegKey HKLM "Software\Classes\StickyNotesPro.Backup"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "StickyNotesPro"

  SetShellVarContext current
  IfSilent keep_data
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Do you also want to delete ALL your notes, checklists and settings?$\r$\n$\r$\n($APPDATA\StickyNotesPro)$\r$\n$\r$\nChoose No to keep them for a future installation." IDNO keep_data
    RMDir /r "$APPDATA\StickyNotesPro"
    DeleteRegKey HKCU "${APPREGKEY}"
  keep_data:
SectionEnd
