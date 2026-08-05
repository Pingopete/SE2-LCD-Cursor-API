@echo off
rem Build all three assemblies and deploy them to DeployDir (see Directory.Build.props).
rem
rem The logic dll hot-reloads into a running game within ~2s. The bootstrap and the
rem contract assembly do NOT -- changing either needs a game restart.
rem
rem CLOSE THE GAME FIRST unless you are only changing the logic dll. A build is a hot
rem reload, and in this engine that has repeatedly cost a device removal.
cd /d "%~dp0.."
dotnet build LcdCursorApi.sln -c Release
pause
