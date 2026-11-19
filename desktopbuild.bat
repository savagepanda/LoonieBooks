@echo off
setlocal

:: ============================================================
:: LoonieBooks desktop build
:: Publishes the launcher (LoonieBooks.exe) with the web host in web\ and the API in api\, all
:: self-contained for win-x64, so the folder runs on any 64-bit Windows PC without .NET installed.
::   artifacts\desktop\LoonieBooks\       double-click LoonieBooks.exe
::   artifacts\desktop\LoonieBooks.zip    the same folder, to copy to another PC
:: ============================================================

set OUT=%~dp0artifacts\desktop
set APP=%OUT%\LoonieBooks
:: Where builds went before the project was renamed LoonieBooks: a copy running from there is
:: stopped like one in APP, and the folder and zip are removed.
set LEGACY=%OUT%\MapleLedger
set PUBLISH=-c Release -r win-x64 --self-contained -p:DebugType=none -nologo
:: Single-file for the launcher and the API. Not for the web host: the property would also reach the
:: WebAssembly client it references, which can't be single-file.
set SINGLE=-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] dotnet CLI not found on PATH.
    pause
    exit /b 1
)

:: Stop the copy running from the output folder so its files can be replaced. Its stop signal
:: (RunningCopies.StopSignalName; older builds: closing its window) lets the launcher stop the web
:: host and API itself (and log why); anything left after 15 seconds is stopped by force, and the new
:: build is started when it's done. Copies installed elsewhere are left alone. Exit code: 0 nothing
:: was running, 2 stopped, 1 couldn't stop.
set RESTART=
powershell -NoProfile -Command "$folders = '%APP%\', '%LEGACY%\'; function Running { @(Get-Process | Where-Object { $path = $_.Path; $path -and ($folders | Where-Object { $path.StartsWith($_, 'OrdinalIgnoreCase') }) }) }; $p = Running; if ($p.Count -gt 0) { Write-Host 'Stopping the running LoonieBooks...'; $p | ForEach-Object { $stop = $null; if ([Threading.EventWaitHandle]::TryOpenExisting('Local\MapleLedger.Desktop.Stop.' + $_.Id, [ref]$stop)) { [void]$stop.Set(); $stop.Dispose() } else { [void]$_.CloseMainWindow() } }; $p | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue; Running | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }; if ((Running).Count -gt 0) { exit 1 }; if ($p.Count -gt 0) { exit 2 }"
if %errorlevel% equ 1 (
    echo [ERROR] Couldn't stop LoonieBooks. Close its window, then run this again.
    pause
    exit /b 1
)
if %errorlevel% equ 2 set RESTART=1

if exist "%APP%" rd /s /q "%APP%"
if exist "%OUT%\LoonieBooks.zip" del /q "%OUT%\LoonieBooks.zip"
if exist "%LEGACY%" rd /s /q "%LEGACY%"
if exist "%LEGACY%.zip" del /q "%LEGACY%.zip"

echo [1/4] Publishing the launcher...
dotnet publish "%~dp0MapleLedger.Desktop" %PUBLISH% %SINGLE% -o "%APP%" || goto :failed

echo [2/4] Publishing the web host (the WebAssembly build takes a few minutes)...
dotnet publish "%~dp0MapleLedger.Web" %PUBLISH% -o "%APP%\web" || goto :failed

echo [3/4] Publishing the API...
dotnet publish "%~dp0MapleLedger.ApiService" %PUBLISH% %SINGLE% -o "%APP%\api" || goto :failed

echo [4/4] Zipping...
powershell -NoProfile -Command "Compress-Archive -Path '%APP%' -DestinationPath '%OUT%\LoonieBooks.zip' -Force" || goto :failed

echo.
if defined RESTART (
    echo Done. Starting LoonieBooks again, since it was running before the build.
    :: Not "start": the app would inherit this script's handles, keeping a redirected log locked.
    powershell -NoProfile -Command "Start-Process -FilePath '%APP%\LoonieBooks.exe' -WorkingDirectory '%APP%'"
) else (
    echo Done. Start LoonieBooks with:
    echo   %APP%\LoonieBooks.exe
)
echo To install or upgrade on another PC, unzip %OUT%\LoonieBooks.zip anywhere and run LoonieBooks.exe.
echo An older copy that's open is closed and replaced; the ledger and settings are kept and updated.
exit /b 0

:failed
echo [ERROR] The build failed; see the output above.
pause
exit /b 1
