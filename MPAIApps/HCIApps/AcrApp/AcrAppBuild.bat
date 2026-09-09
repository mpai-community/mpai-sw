@echo off
setlocal
set HERE=%~dp0
set SRC=%HERE%src\AcrApp.csproj
set TMP=%HERE%_build
echo ============================================================
echo   Building AcrApp.exe (Access Control Registration)
echo ============================================================
taskkill /IM AcrApp.exe /F >nul 2>&1
if exist "%TMP%" rd /s /q "%TMP%"
dotnet publish "%SRC%" -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%TMP%"
if errorlevel 1 ( echo BUILD FAILED. & pause & exit /b 1 )
if not exist "%TMP%\AcrApp.exe" ( echo AcrApp.exe not produced. & pause & exit /b 1 )
copy /Y "%TMP%\AcrApp.exe" "%HERE%" >nul
if exist "%TMP%\WebView2Loader.dll" copy /Y "%TMP%\WebView2Loader.dll" "%HERE%" >nul
rd /s /q "%TMP%"
echo.
echo DONE:  %HERE%AcrApp.exe
pause
