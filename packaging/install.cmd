@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "APP_NAME=Claude Usage"
set "APP_ID=ClaudeUsage"
set "EXE_NAME=ClaudeUsage.exe"
set "SOURCE_DIR=%~dp0"

if not defined LOCALAPPDATA (
  echo ERROR: LOCALAPPDATA is not defined for this Windows user.
  exit /b 1
)

if not defined APPDATA (
  echo ERROR: APPDATA is not defined for this Windows user.
  exit /b 1
)

set "INSTALL_DIR=%LOCALAPPDATA%\Programs\%APP_ID%"
set "START_MENU_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs\%APP_NAME%"
set "TARGET_EXE=%INSTALL_DIR%\%EXE_NAME%"
set "UNINSTALL_CMD=%INSTALL_DIR%\uninstall.cmd"
set "APP_VERSION=unknown"

if not exist "%SOURCE_DIR%%EXE_NAME%" (
  echo ERROR: %EXE_NAME% was not found next to install.cmd.
  echo Extract the complete release ZIP, then run install.cmd again.
  exit /b 1
)

if exist "%SOURCE_DIR%VERSION" (
  set /p APP_VERSION=<"%SOURCE_DIR%VERSION"
)

echo Installing %APP_NAME% for the current user...
echo Destination: "%INSTALL_DIR%"

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if errorlevel 1 (
  echo ERROR: Could not create the per-user installation directory.
  exit /b 1
)

copy /b /y "%SOURCE_DIR%%EXE_NAME%" "%TARGET_EXE%" >nul
if errorlevel 1 (
  echo ERROR: Could not copy %EXE_NAME%. Close the app if it is running, then retry.
  exit /b 1
)

for %%F in (
  "%SOURCE_DIR%*.dll"
  "%SOURCE_DIR%*.deps.json"
  "%SOURCE_DIR%*.runtimeconfig.json"
  "%SOURCE_DIR%%EXE_NAME%.config"
  "%SOURCE_DIR%README.txt"
  "%SOURCE_DIR%LICENSE.txt"
  "%SOURCE_DIR%VERSION"
) do (
  if exist "%%~fF" copy /b /y "%%~fF" "%INSTALL_DIR%\%%~nxF" >nul
)

copy /b /y "%SOURCE_DIR%uninstall.cmd" "%UNINSTALL_CMD%" >nul
if errorlevel 1 (
  echo ERROR: Could not install uninstall.cmd.
  exit /b 1
)

if not exist "%START_MENU_DIR%" mkdir "%START_MENU_DIR%"
if errorlevel 1 (
  echo ERROR: Could not create the current user's Start menu folder.
  exit /b 1
)

set "CLAUDE_USAGE_TARGET=%TARGET_EXE%"
set "CLAUDE_USAGE_WORKDIR=%INSTALL_DIR%"
set "CLAUDE_USAGE_APP_SHORTCUT=%START_MENU_DIR%\%APP_NAME%.lnk"
set "CLAUDE_USAGE_UNINSTALL_SHORTCUT=%START_MENU_DIR%\Uninstall %APP_NAME%.lnk"
set "CLAUDE_USAGE_UNINSTALL_CMD=%UNINSTALL_CMD%"
set "CLAUDE_USAGE_VERSION=%APP_VERSION%"
set "CLAUDE_USAGE_INSTALL_KEY=HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\%APP_ID%"

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference = 'Stop'; $shell = New-Object -ComObject WScript.Shell; $app = $shell.CreateShortcut($env:CLAUDE_USAGE_APP_SHORTCUT); $app.TargetPath = $env:CLAUDE_USAGE_TARGET; $app.WorkingDirectory = $env:CLAUDE_USAGE_WORKDIR; $app.Description = 'View local Claude Code activity'; $app.IconLocation = $env:CLAUDE_USAGE_TARGET + ',0'; $app.Save(); $remove = $shell.CreateShortcut($env:CLAUDE_USAGE_UNINSTALL_SHORTCUT); $remove.TargetPath = $env:CLAUDE_USAGE_UNINSTALL_CMD; $remove.WorkingDirectory = $env:CLAUDE_USAGE_WORKDIR; $remove.Description = 'Uninstall Claude Usage for this user'; $remove.Save()"
if errorlevel 1 (
  echo WARNING: The app was installed, but Start menu shortcuts could not be created.
)

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference = 'Stop'; $key = $env:CLAUDE_USAGE_INSTALL_KEY; $quote = [char]34; $uninstall = $quote + $env:ComSpec + $quote + ' /d /c ' + $quote + $quote + $env:CLAUDE_USAGE_UNINSTALL_CMD + $quote + $quote; New-Item -Path $key -Force | Out-Null; New-ItemProperty -Path $key -Name DisplayName -PropertyType String -Value 'Claude Usage' -Force | Out-Null; New-ItemProperty -Path $key -Name DisplayVersion -PropertyType String -Value $env:CLAUDE_USAGE_VERSION -Force | Out-Null; New-ItemProperty -Path $key -Name Publisher -PropertyType String -Value 'Claude Usage contributors' -Force | Out-Null; New-ItemProperty -Path $key -Name InstallLocation -PropertyType String -Value $env:CLAUDE_USAGE_WORKDIR -Force | Out-Null; New-ItemProperty -Path $key -Name DisplayIcon -PropertyType String -Value ($env:CLAUDE_USAGE_TARGET + ',0') -Force | Out-Null; New-ItemProperty -Path $key -Name UninstallString -PropertyType String -Value $uninstall -Force | Out-Null; New-ItemProperty -Path $key -Name NoModify -PropertyType DWord -Value 1 -Force | Out-Null; New-ItemProperty -Path $key -Name NoRepair -PropertyType DWord -Value 1 -Force | Out-Null"
if errorlevel 1 (
  echo WARNING: The app was installed, but its Apps ^& Features entry could not be created.
)

echo.
echo %APP_NAME% is installed for this user only. No administrator rights were used.
echo You can launch it from the Start menu or from:
echo "%TARGET_EXE%"
exit /b 0
