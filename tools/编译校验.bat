@echo off
chcp 65001 >nul
setlocal
echo Unity compile and behavior checks. Default: chain. Arguments select checks.
echo Each invocation keeps independent logs and results.json under the project guard.
echo.
if "%~1"=="" goto default_checks
python.exe -B "%~dp0run_all_checks.py" %*
set "DRAGONBOAT_CHECK_EXIT=%ERRORLEVEL%"
goto checks_finished

:default_checks
python.exe -B "%~dp0run_all_checks.py" chain
set "DRAGONBOAT_CHECK_EXIT=%ERRORLEVEL%"

:checks_finished
echo.
echo Exit code: %DRAGONBOAT_CHECK_EXIT%
if not defined DRAGONBOAT_CHECK_NO_PAUSE pause
exit /b %DRAGONBOAT_CHECK_EXIT%
