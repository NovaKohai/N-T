@echo off
setlocal
cd /d "%~dp0"
title N^&T Release Publisher

echo.
echo ====================================================================
echo                 N^&T All-in-One Release Publisher
echo ====================================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\publish-update.ps1" -SkipTests %*
if errorlevel 1 (
    echo.
    echo ====================================================================
    echo  [ERROR] Release publishing failed or was cancelled.
    echo ====================================================================
    echo.
    pause
    exit /b 1
)

echo.
echo ====================================================================
echo  [DONE] All steps completed successfully!
echo ====================================================================
echo.
pause
