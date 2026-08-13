@echo off
setlocal
set "REGISTER_SCRIPT=%~dp0register-external-tool.ps1"
if not exist "%REGISTER_SCRIPT%" (
  echo register-external-tool.ps1 was not found.
  pause
  exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""%REGISTER_SCRIPT%""'"
if errorlevel 1 (
  echo Registration was cancelled or failed.
  pause
  exit /b 1
)
echo PowerTools registration completed. Restart Power BI Desktop.
pause
