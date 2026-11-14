@echo off
rem Entry point for the VR viewer pipelines: vr build | run | deploy | test | help
rem Example: vr build -Stereo All
rem The implementation and full help are in tools\vr.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\vr.ps1" %*
exit /b %ERRORLEVEL%
