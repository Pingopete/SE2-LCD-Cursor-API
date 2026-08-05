@echo off
rem Launch SE2 with the bootstrap plugin via Keen's own -plugins: argument.
rem Steam must be running. Build first: scripts\build.bat
rem
rem NOTE the plugin is loaded from the DEPLOY directory, not from bin\Release. The
rem bootstrap finds its logic dll beside itself, so loading it from bin would make it
rem hot-reload the bin copy and ignore every deploy.

set DEPLOY_DIR=D:\SE2LcdCursor
set PLUGIN_DLL=%DEPLOY_DIR%\LcdCursorApi.dll
set GAME_DIR=E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2

if not exist "%PLUGIN_DLL%" (
  echo Plugin not found: %PLUGIN_DLL%
  echo Run scripts\build.bat first.
  pause
  exit /b 1
)

cd /d "%GAME_DIR%"
start "" SpaceEngineers2.exe "-plugins:%PLUGIN_DLL%"
echo Launched. Watch %DEPLOY_DIR%\lcdcursor.log for activity.
rem If the log never appears, Steam may have relaunched the exe without arguments:
rem put -plugins:%PLUGIN_DLL% in Steam -> SE2 -> Properties -> Launch Options and
rem start from Steam instead.
