@echo off
setlocal
cd /d "%~dp0"
echo Building Azeroth Survivors Save Editor Builder...
echo.

rem ---- Application icon: copied once from the game into app.ico ----
rem      (drag AzerothSurvivors.exe onto build.bat, or delete app.ico to pick it again)
if exist app.ico goto build
set "GAMEEXE=%~1"
if not defined GAMEEXE for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "try{(Get-Content \"$env:APPDATA\AzsEditorBuilder\settings.json\" -Raw | ConvertFrom-Json).ExePath}catch{}"`) do set "GAMEEXE=%%P"
if not defined GAMEEXE set /p "GAMEEXE=Drag AzerothSurvivors.exe here for the app icon (or just press Enter to skip): "
if not defined GAMEEXE goto noicon
set "GAMEEXE=%GAMEEXE:"=%"
if not exist "%GAMEEXE%" goto noicon
dotnet run --project tools\IconTool\IconTool.vbproj -c Release -- "%GAMEEXE%" app.ico
if exist app.ico goto build
:noicon
echo No app.ico - building with the default icon (the window will still show the game icon once you pick the game exe).
echo.

:build
dotnet publish AzsEditorBuilder.vbproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo Build failed. Is the .NET 10 SDK installed?  https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
echo.
echo Done: publish\AzsEditorBuilder.exe
start "" "publish"
pause
