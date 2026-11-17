@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "VERSION=%~1"
if "%VERSION%"=="" (
    for /f "delims=" %%i in ('git describe --tags --abbrev=0 2^>nul') do set "TAG=%%i"
    if defined TAG set "VERSION=!TAG:v=!"
)
if "%VERSION%"=="" set /p VERSION=Enter version (e.g. 1.0.4): 

if "%GITEE_TOKEN%"=="" set /p GITEE_TOKEN=Enter Gitee token: 

echo.
echo Uploading v%VERSION% to Gitee release...
echo.

where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\upload-gitee-release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\upload-gitee-release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
)

if errorlevel 1 (
    echo.
    echo [FAILED] Upload failed, see error above.
) else (
    echo.
    echo [DONE] Package uploaded to Gitee release.
)
pause
