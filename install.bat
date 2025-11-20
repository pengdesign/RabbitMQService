@echo off

set serviceName=tw_mq_bpm
set serviceFilePath=D:\git\JinHui_11\HM\RabbitMQWindowsService\bin\Debug\RabbitMQService.exe
set serviceDescription=BPM RabbitMQ 多队列监听服务

sc create %serviceName%  BinPath=%serviceFilePath%
sc config %serviceName%  start=auto  
sc description %serviceName%  %serviceDescription%
sc start %serviceName%

pause
