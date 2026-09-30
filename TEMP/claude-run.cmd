@echo off
rem Runs whatever claude-job.cmd contains, logging to claude-job.log. Written by Claude.
cd /d D:\Development\kotor-coop
echo START %date% %time% > TEMP\claude-job.log
call TEMP\claude-job.cmd >> TEMP\claude-job.log 2>&1
echo EXIT %errorlevel% %date% %time% >> TEMP\claude-job.log
