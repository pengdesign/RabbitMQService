@echo off

set serviceName=BPM_RabbitMQService
set serviceFilePath=D:\git\JinHui_11\HM\RabbitMQWindowsService\bin\Debug\RabbitMQService.exe
set serviceDescription=BPM RabbitMQ ����м�������

sc create %serviceName%  BinPath=%serviceFilePath%
sc config %serviceName%  start=auto  
sc description %serviceName%  %serviceDescription%
sc start %serviceName%

pause
