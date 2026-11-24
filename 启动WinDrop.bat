@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "dist\WinDrop\WinDrop.exe" (
    start "" "dist\WinDrop\WinDrop.exe"
) else (
    dotnet run --project src\WinDrop.App\WinDrop.App.csproj -c Release
)
