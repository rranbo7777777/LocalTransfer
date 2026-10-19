@echo off
rem ---------------------------------------------------------------------------
rem  Collects diagnostics from a USB-connected Android phone:
rem    1. the app's own error log (Android/data/com.localtransfer.mobile/files)
rem    2. a live logcat capture while you reproduce the problem
rem
rem  Before running: enable Developer options -> USB debugging on the phone,
rem  connect the cable, and accept the "Allow USB debugging" prompt.
rem  Output lands next to this file: phone-errorlog.txt and phone-logcat.txt
rem ---------------------------------------------------------------------------
setlocal
set ADB=C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe
if not exist "%ADB%" set ADB=adb

set APPDIR=/sdcard/Android/data/com.localtransfer.mobile/files
set ERRFILE=%~dp0phone-errorlog.txt
set LOGFILE=%~dp0phone-logcat.txt

echo ============================================================
echo  LocalTransfer diagnostics
echo ============================================================
echo.

"%ADB%" devices
echo.
echo If the list above does not show your phone as "device",
echo fix the USB connection first and run this again.
echo.

echo [1/3] Pulling the app error log...
"%ADB%" pull "%APPDIR%/localtransfer-error.log" "%ERRFILE%"
if errorlevel 1 echo   (no app error log yet - that is fine, continue)

echo.
echo [2/3] Clearing the device log buffer...
"%ADB%" logcat -c

echo.
echo [3/3] Now reproduce the problem on the phone
echo       (open the app, pair, send the picture ...).
echo       Press Ctrl+C in this window when the error has appeared.
echo.
echo Writing to: %LOGFILE%
echo.
"%ADB%" logcat -v time > "%LOGFILE%"

echo.
echo Done. Send these two files back:
echo   %ERRFILE%
echo   %LOGFILE%
pause
