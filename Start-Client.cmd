@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "src\Chat.Client\Chat.Client.csproj" -c Release
if errorlevel 1 pause
