@echo off
rem Builds the Iris installer with Velopack. Double-click this, or run it from a terminal:
rem   build-installer.cmd                     x64 setup in artifacts\releases
rem   build-installer.cmd -Platform ARM64     ARM64 setup
rem   build-installer.cmd -Publish            also upload the release to the R2 bucket
rem   build-installer.cmd -UploadOnly         upload the last build without rebuilding
rem   build-installer.cmd -FromGitHub v1.3.1  upload a release GitHub Actions built
rem It runs build-installer.ps1 with the execution policy bypassed for this one run only, so it works
rem even where Windows PowerShell only allows signed scripts. Nothing on the system is changed.
setlocal

where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
)
set "RESULT=%errorlevel%"

rem Keep the window open when double-clicked so the result can be read.
echo %cmdcmdline% | find /i "/c" >nul && pause
exit /b %RESULT%
