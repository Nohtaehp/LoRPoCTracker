@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem Version: argument > auto-increment latest tag
set "VERSION=%~1"

rem Token: env var > local file
if "%GITEE_TOKEN%"=="" (
    if exist "%USERPROFILE%\.lor-gitee-token" (
        set /p GITEE_TOKEN=<"%USERPROFILE%\.lor-gitee-token"
    )
)
if "%GITEE_TOKEN%"=="" (
    set /p GITEE_TOKEN=Enter Gitee token (saved to %USERPROFILE%\.lor-gitee-token): 
    >"%USERPROFILE%\.lor-gitee-token" echo !GITEE_TOKEN!
)

echo.
echo === One-click release ===
if "%VERSION%"=="" (
    echo Version: auto-increment from latest tag
) else (
    echo Version: %VERSION%
)
echo.
echo Steps: commit -^> tag -^> push -^> wait GitHub Actions -^> upload Gitee -^> verify
echo.

where pwsh >nul 2>nul
if %errorlevel%==0 (
    if "%VERSION%"=="" (
        pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Token "%GITEE_TOKEN%"
    ) else (
        pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
    )
) else (
    if "%VERSION%"=="" (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Token "%GITEE_TOKEN%"
    ) else (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" -Version "%VERSION%" -Token "%GITEE_TOKEN%"
    )
)

if errorlevel 1 (
    echo.
    echo [FAILED] Release failed, see error above.
) else (
    echo.
    echo [DONE] Release finished.
)
pause
