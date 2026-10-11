@echo off
title PureISP B/S Web Control Console
echo ========================================================
echo        PureISP B/S Minimal Web Control Console
echo             http://127.0.0.1:3600/console
echo ========================================================
echo.

cd /d "%~dp0server"

echo [INFO] Starting PureISP Service Engine...
start /b node src/server.js

timeout /t 1 /nobreak >nul

echo [INFO] Launching Web Control Dashboard in default browser...
start http://127.0.0.1:3600/console

echo [INFO] PureISP B/S is running. Keep this window minimized or press Ctrl+C to stop.
echo.
pause
