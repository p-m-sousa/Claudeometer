@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "APP_NAME=Claude Usage"
set "APP_ID=ClaudeUsage"

if not defined LOCALAPPDATA (
  echo ERROR: LOCALAPPDATA is not defined for this Windows user.
  exit /b 1
)

if not defined APPDATA (
  echo ERROR: APPDATA is not defined for this Windows user.
  exit /b 1
)

set "INSTALL_DIR=%LOCALAPPDATA%\Programs\%APP_ID%"
set "SETTINGS_DIR=%LOCALAPPDATA%\%APP_ID%"
set "START_MENU_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs\%APP_NAME%"
set "UNINSTALL_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\%APP_ID%"
set "PURGE_SETTINGS=0"

if /i "%~1"=="--purge" set "PURGE_SETTINGS=1"

tasklist.exe /FI "IMAGENAME eq ClaudeUsage.exe" /NH 2>nul | findstr.exe /I /C:"ClaudeUsage.exe" >nul
if not errorlevel 1 (
  echo ERROR: Claude Usage is still running.
  echo Close the app, then run this uninstaller again.
  exit /b 1
)

echo Uninstalling %APP_NAME% for the current user...

if exist "%START_MENU_DIR%\%APP_NAME%.lnk" del /q "%START_MENU_DIR%\%APP_NAME%.lnk" >nul 2>nul
if exist "%START_MENU_DIR%\Uninstall %APP_NAME%.lnk" del /q "%START_MENU_DIR%\Uninstall %APP_NAME%.lnk" >nul 2>nul
if exist "%START_MENU_DIR%" rmdir "%START_MENU_DIR%" >nul 2>nul

reg.exe delete "%UNINSTALL_KEY%" /f >nul 2>nul

if "%PURGE_SETTINGS%"=="1" (
  if exist "%SETTINGS_DIR%" rmdir /s /q "%SETTINGS_DIR%"
  echo Per-user settings were removed from "%SETTINGS_DIR%".
) else (
  echo Per-user settings, if any, were kept in "%SETTINGS_DIR%".
  echo Run uninstall.cmd --purge to remove them as well.
)

rem The uninstaller itself is running from INSTALL_DIR. A child process retries
rem cleanup for ten seconds after this script exits, which also tolerates brief
rem antivirus/indexer file holds.
set "CLAUDE_USAGE_REMOVE_DIR=%INSTALL_DIR%"
cd /d "%TEMP%" >nul 2>nul
start "" /b powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -Command "$deadline = (Get-Date).AddSeconds(10); do { Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath $env:CLAUDE_USAGE_REMOVE_DIR -Recurse -Force -ErrorAction SilentlyContinue } while ((Test-Path -LiteralPath $env:CLAUDE_USAGE_REMOVE_DIR) -and (Get-Date) -lt $deadline)"

echo %APP_NAME% shortcuts and registration were removed for the current user.
echo Program-file cleanup is running and will retry for up to ten seconds.
exit /b 0
