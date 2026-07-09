@echo off
title ResHog Setup

:: Check admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo ============================================================
    echo ResHog Setup requires administrator privileges.
    echo Relaunching with admin rights (please click Yes on UAC)...
    echo ============================================================
    powershell -Command "Start-Process '%~s0' -Verb RunAs -Wait"
    exit /b %errorlevel%
)

:: Run setup.exe (same directory)
"%~dp0setup.exe"
pause
