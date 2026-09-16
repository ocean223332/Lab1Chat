@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "src\Chat.Server\Chat.Server.csproj" -c Release -- %*
if errorlevel 1 pause
