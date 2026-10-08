@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll
if not exist bin mkdir bin
%CSC% /nologo /target:winexe /out:bin\ScreenSwitch.exe /main:ScreenSwitch.Program /win32icon:app.ico %REFS% src\*.cs
if errorlevel 1 exit /b 1
%CSC% /nologo /target:exe /out:bin\ScreenSwitchCli.exe /main:ScreenSwitch.Program /win32icon:app.ico %REFS% src\*.cs
if errorlevel 1 exit /b 1
%CSC% /nologo /target:exe /out:bin\ScreenSwitch.Tests.exe /main:ScreenSwitch.Tests.TestRunner %REFS% src\*.cs tests\*.cs
if errorlevel 1 exit /b 1
echo BUILD OK
