@echo off
setlocal
echo Building MpdApp.exe...
set SRC=D:\BI\MPAIApps\HCIApps\MpdApp\src\MpdApp.csproj
set HERE=D:\BI\MPAIApps\HCIApps\MpdApp
set TMP=%HERE%\_build
taskkill /IM MpdApp.exe /F >nul 2>&1
dotnet publish "%SRC%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o "%TMP%"
if errorlevel 1 ( echo BUILD FAILED. & pause & exit /b 1 )
copy /Y "%TMP%\MpdApp.exe" "%HERE%\" >nul
if exist "%TMP%\WebView2Loader.dll" copy /Y "%TMP%\WebView2Loader.dll" "%HERE%\" >nul
rmdir /S /Q "%TMP%" 2>nul
echo DONE: %HERE%\MpdApp.exe