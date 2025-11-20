@echo off
set serviceName=tw_mq_bpm

sc stop %serviceName% 
sc delete %serviceName% 

pause