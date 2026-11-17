@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "VERSION=%~1"

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
echo === One-click release ===
if "%VERSION%"=="" goto auto_version
echo Version: %VERSION%
goto run
:auto_version
echo Version: auto-increment from latest tag
:run
echo Steps: commit -^> tag -^> push -^> wait GitHub Actions -^> upload Gitee -^> verify
echo.

where pwsh >nul 2>nul
if errorlevel 1 goto use_powershell
if "%VERSION%"=="" goto pwsh_auto
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
goto after_release
:pwsh_auto
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Token "%GITEE_TOKEN%"
goto after_release
:use_powershell
if "%VERSION%"=="" goto ps_auto
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
goto after_release
:ps_auto
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Token "%GITEE_TOKEN%"

:after_release
if errorlevel 1 goto failed
echo.
echo [DONE] Release finished.
goto end
:failed
echo.
echo [FAILED] Release failed, see error above.
pause
exit /b 1
:token_missing
echo.
echo [FAILED] No Gitee token provided.
pause
exit /b 1
:end
pause
