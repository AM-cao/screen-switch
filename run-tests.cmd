@echo off
call "%~dp0build.cmd" || exit /b 1
"%~dp0bin\ScreenSwitch.Tests.exe"
