@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "VERSION=%~1"
if not "%VERSION%"=="" goto version_ready
for /f "delims=" %%i in ('git describe --tags --abbrev=0 2^>nul') do set "TAG=%%i"
if defined TAG set "VERSION=!TAG:v=!"
if not "%VERSION%"=="" goto version_ready
set /p VERSION=Enter version, e.g. 1.0.5: 
:version_ready

if not "%GITEE_TOKEN%"=="" goto token_ready
if not exist "%USERPROFILE%\.lor-gitee-token" goto ask_token
set /p GITEE_TOKEN=<"%USERPROFILE%\.lor-gitee-token"
if not "%GITEE_TOKEN%"=="" goto token_ready
:ask_token
echo Token will be saved to %USERPROFILE%\.lor-gitee-token
set /p GITEE_TOKEN=Enter Gitee token: 
if "%GITEE_TOKEN%"=="" goto token_missing
>"%USERPROFILE%\.lor-gitee-token" echo !GITEE_TOKEN!
:token_ready

echo.
echo Uploading v%VERSION% to Gitee release...
echo.

where pwsh >nul 2>nul
if errorlevel 1 goto use_powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\upload-gitee-release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
goto after_upload
:use_powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\upload-gitee-release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"

:after_upload
if errorlevel 1 goto failed
echo.
echo [DONE] Package uploaded to Gitee release.
goto end
:failed
echo.
echo [FAILED] Upload failed, see error above.
pause
exit /b 1
:token_missing
echo.
echo [FAILED] No Gitee token provided.
pause
exit /b 1
:end
pause
