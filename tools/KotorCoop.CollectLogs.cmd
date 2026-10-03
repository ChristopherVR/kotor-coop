@echo off
setlocal
echo kotor-coop support bundle
echo Nothing is uploaded. Review the generated zip before sharing it.
"%~dp0KotorCoop.Agent.exe" support
set "supportExit=%errorlevel%"
echo.
pause
exit /b %supportExit%
