@echo off
rem ---------------------------------------------------------------------------
rem  Builds StoreApp.exe in this folder.
rem
rem  MPAI Store: submit an L3 to the Store Service and read its verdict.
rem
rem  Run this file again whenever the StoreApp code changes. Afterwards, just
rem  double-click StoreApp.exe - no need to touch VS Code or dotnet directly.
rem
rem  NOTE: publishing straight into this folder (-o "%~dp0") triggers a .NET
rem  SDK bug (CS5001, "no static Main") when the output folder also contains
rem  the .cs source files. So this publishes to a throwaway subfolder first,
rem  then copies just the finished .exe out here and removes the subfolder.
rem ---------------------------------------------------------------------------

rem  A running application cannot be overwritten, so close it first.
tasklist /fi "imagename eq StoreApp.exe" | find /i "StoreApp.exe" >nul
if not errorlevel 1 (
    echo StoreApp.exe is running - closing it.
    taskkill /im StoreApp.exe /f >nul 2>&1
    timeout /t 2 /nobreak >nul
)

set "PUBLISH_DIR=%~dp0_publish_tmp"

if exist "%PUBLISH_DIR%" rd /s /q "%PUBLISH_DIR%"

echo Building StoreApp.exe ...
echo.

dotnet publish "%~dp0StoreApp.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%PUBLISH_DIR%"

if errorlevel 1 (
    echo.
    echo Build FAILED.
    echo.
    echo If the error mentions "Access to the path ... is denied", the
    echo application was still running: close it and run this file again.
    if exist "%PUBLISH_DIR%" rd /s /q "%PUBLISH_DIR%"
    pause
    exit /b 1
)

copy /y "%PUBLISH_DIR%\StoreApp.exe" "%~dp0StoreApp.exe" >nul
rd /s /q "%PUBLISH_DIR%"

echo.
echo Built: %~dp0StoreApp.exe
echo.
dir /b "%~dp0"
echo.
pause
