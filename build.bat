@echo off
rem Builds FortItemTracker.exe into this folder. Needs the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10);
rem the exe it makes runs on any 64-bit Windows 10/11 PC with nothing else installed.
cd /d "%~dp0"
dotnet publish src\FortItemTracker.csproj -c Release -o src\bin\publish --nologo
if errorlevel 1 (
    pause
    exit /b 1
)
copy /y src\bin\publish\FortItemTracker.exe . >nul
echo.
echo Built FortItemTracker.exe
