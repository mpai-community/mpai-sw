@echo off
setlocal
set HERE=%~dp0
set SRC=%HERE%src\MacApp.csproj
echo ============================================================
echo   Building MacApp
echo ============================================================
taskkill /IM MacApp.exe /F >nul 2>&1
dotnet build "%SRC%" -c Release
if errorlevel 1 ( echo BUILD FAILED. & pause & exit /b 1 )
echo.
echo DONE:  %HERE%src\bin\Release\net10.0-windows10.0.19041.0\MacApp.exe
pause