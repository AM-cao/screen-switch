@echo off
setlocal
set DEST=%USERPROFILE%\ScreenSwitch
call "%~dp0build.cmd" || exit /b 1
if not exist "%DEST%" mkdir "%DEST%"
taskkill /IM ScreenSwitch.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul
copy /Y "%~dp0bin\ScreenSwitch.exe" "%DEST%\" >nul
if errorlevel 1 (
  echo ERROR: copy ScreenSwitch.exe failed, is it still running?
  exit /b 1
)
copy /Y "%~dp0bin\ScreenSwitchCli.exe" "%DEST%\" >nul
if errorlevel 1 (
  echo ERROR: copy ScreenSwitchCli.exe failed
  exit /b 1
)
for %%F in (setup-client.ps1 setup-sshd.ps1) do (
  copy /Y "%~dp0%%F" "%DEST%\" >nul
  if errorlevel 1 (
    echo ERROR: copy %%F failed
    exit /b 1
  )
)
if not exist "%DEST%\config.json" (
  copy /Y "%~dp0config.default.json" "%DEST%\config.json" >nul
  if errorlevel 1 (
    echo ERROR: copy config.json failed
    exit /b 1
  )
)
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup')+'\ScreenSwitch.lnk');$s.TargetPath='%DEST%\ScreenSwitch.exe';$s.WorkingDirectory='%DEST%';$s.Save()"
start "" "%DEST%\ScreenSwitch.exe"
echo installed to %DEST%
echo next: run "%DEST%\ScreenSwitchCli.exe" setup
echo       (it asks a few questions and writes config.json). See README-windows.md.
