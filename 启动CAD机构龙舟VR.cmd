@echo off
setlocal
set "boatExe=%~dp0Builds\DragonBoatVR\DragonBoatVR.exe"
if not exist "%boatExe%" (
  echo DragonBoatVR.exe is missing from Builds\DragonBoatVR.
  pause
  exit /b 1
)
start "" /D "%~dp0Builds\DragonBoatVR" "%boatExe%" -cad-tendon-rig
endlocal
