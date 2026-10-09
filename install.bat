@echo off
rem Install BPM_RabbitMQService. Run as Administrator.
rem This script must sit in the same folder as RabbitMQService.exe
set serviceName=BPM_RabbitMQService
set serviceFilePath=%~dp0RabbitMQService.exe
set serviceDescription=BPM RabbitMQ multi-queue listener service

if not exist "%serviceFilePath%" (
  echo Cannot find %serviceFilePath%
  echo Put this script next to RabbitMQService.exe and retry.
  pause
  exit /b 1
)

sc stop %serviceName% >nul 2>&1
sc delete %serviceName% >nul 2>&1
sc create %serviceName% BinPath= "%serviceFilePath%" start= auto
sc config %serviceName% start= auto
sc description %serviceName% "%serviceDescription%"
rem Auto-restart 3 times on failure (60s apart) to survive network hiccups
sc failure %serviceName% reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc start %serviceName%

pause
