@echo off
set serviceName=BPM_RabbitMQService

sc stop %serviceName% 
sc delete %serviceName% 

pause