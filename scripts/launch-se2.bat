@echo off
setlocal

rem Launch SE2 with the LCD Cursor plugin, WITHOUT going through Steam's launch options.
rem
rem   scripts\launch-se2.bat          cursor plugin only  (best for testing this mod)
rem   scripts\launch-se2.bat both     cursor + RTT plugin
rem
rem ---------------------------------------------------------------------------------
rem ONE-TIME SETUP: clear Steam's launch options for Space Engineers 2.
rem   Steam -> Space Engineers 2 -> Properties -> General -> Launch Options -> empty it.
rem
rem WHY. Steam appends its launch options to the game even when the exe is started
rem directly, and having any options set is what triggers the "launch with parameters"
rem confirmation popup every time. With them cleared, this script is the only thing
rem passing arguments: no popup, and no plugin you did not ask for. Leaving them set
rem means the RTT plugin loads alongside whatever this script requests.
rem ---------------------------------------------------------------------------------

set "GAME_DIR=E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2"
set "CURSOR_DLL=D:\SE2LcdCursor\LcdCursorApi.dll"
set "RTT_DLL=D:\SE2Rtt\RttProbe.dll"

if not exist "%GAME_DIR%\SpaceEngineers2.exe" (
  echo Game not found: %GAME_DIR%\SpaceEngineers2.exe
  pause
  exit /b 1
)

if not exist "%CURSOR_DLL%" (
  echo Plugin not found: %CURSOR_DLL%
  echo Run scripts\build.bat first.
  pause
  exit /b 1
)

set "ARGS=-plugins:%CURSOR_DLL%"
if /i "%~1"=="both" (
  if exist "%RTT_DLL%" (
    set "ARGS=-plugins:%CURSOR_DLL% -plugins:%RTT_DLL%"
    echo Loading BOTH plugins. Note they both patch the LCD render path, so a fault
    echo is harder to attribute. Prefer the default cursor-only run when testing.
  ) else (
    echo RTT plugin not found at %RTT_DLL% - loading the cursor plugin alone.
  )
)

rem Steam must be running: the game needs it, but we are not launching THROUGH it.
tasklist /fi "imagename eq steam.exe" 2>nul | find /i "steam.exe" >nul
if errorlevel 1 echo WARNING: Steam does not appear to be running. The game may refuse to start.

echo Launching with: %ARGS%
cd /d "%GAME_DIR%"
start "" "SpaceEngineers2.exe" %ARGS%

echo.
echo Watch D:\SE2LcdCursor\lcdcursor.log - a fresh "=== LcdCursorApi bootstrap ===" header
echo should appear within a few seconds. If it does not, the plugin did not load: check
echo that Steam's launch options are empty (see the note at the top of this file).
endlocal
