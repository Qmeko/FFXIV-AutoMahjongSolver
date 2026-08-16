@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"

set "LOG=%~dp0BUILD_DEBUG_PLUGIN.log"
set "SCRIPT=%~dp0build-tools\Build-DebugPlugin.ps1"
set "RUNNER=%~dp0build-tools\Run-OneClick.ps1"

echo Doman Mahjong Solver Debug + Mortal AI
echo Build, runtime installation, and smoke test
echo.
echo Full log:
echo %LOG%
echo.

if not exist "%SCRIPT%" (
  echo [FAILED] Build script not found:
  echo %SCRIPT%
  pause
  exit /b 1
)

if not exist "%RUNNER%" (
  echo [FAILED] PowerShell runner not found:
  echo %RUNNER%
  pause
  exit /b 1
)

set "ROOT=%~dp0"
if not "%ROOT:~120,1%"=="" (
  echo [FAILED] Source path is too long for Akochan runtime files.
  echo Move this folder to a short path, for example: J:\DMS053\
  echo Current path:
  echo %ROOT%
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%RUNNER%" -ScriptPath "%SCRIPT%" -LogPath "%LOG%"
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo [FAILED] Build or Mortal setup failed. See:
  echo %LOG%
  echo.
  pause
  exit /b %EXITCODE%
)

echo [OK] Plugin build and Mortal smoke test completed.
echo.
echo Developer plugin DLL:
if exist "%~dp0OUTPUT\DEV_PLUGIN_DLL_PATH.txt" (
  for /f "usebackq delims=" %%P in ("%~dp0OUTPUT\DEV_PLUGIN_DLL_PATH.txt") do echo %%P
) else (
  echo [WARN] DEV_PLUGIN_DLL_PATH.txt was not generated.
)
echo.
echo Mortal status:
if exist "%~dp0OUTPUT\MORTAL_READY.txt" (
  type "%~dp0OUTPUT\MORTAL_READY.txt"
) else (
  echo [WARN] MORTAL_READY.txt was not generated.
)
echo.
echo In Dalamud: /xlsettings ^> Experimental ^> Dev Plugin Locations
pause
exit /b 0
