@echo off
setlocal
cd /d "%~dp0"
start "" http://localhost:5128
dotnet run --project PowerTools.csproj --urls http://localhost:5128
