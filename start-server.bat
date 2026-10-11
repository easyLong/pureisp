@echo off
title PureISP Cloud Service Engine
echo ========================================================
echo        PureISP Cloud Service Engine v1.0.0
echo        Commercial Static Residential Proxy & Portal
echo ========================================================
echo.

cd /d "%~dp0server"
if not exist "node_modules" (
    echo [INFO] Installing required dependencies...
    npm install
)

echo [INFO] Starting PureISP service on port 3600...
echo [INFO] Commercial Landing Portal: http://127.0.0.1:3600/
echo [INFO] Press Ctrl+C to stop the service.
echo.
node src/server.js
pause
