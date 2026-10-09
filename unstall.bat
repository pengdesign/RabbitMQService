@echo off
rem Uninstall BPM_RabbitMQService. Run as Administrator.
set serviceName=BPM_RabbitMQService

sc stop %serviceName%
sc delete %serviceName%

pause
