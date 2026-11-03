@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "NOPAUSE="
if /I "%~1"=="/nopause" set "NOPAUSE=1"
cd /d "%~dp0\.."

set "GUID={7DA13345-7F3B-4FB4-B9A9-E3BA3867A64C}"
set "BIN=%CD%\src\SrcCadWorks\bin\Release"
set "DLL=%BIN%\SrcCadWorks.dll"
set "VIEWER=%BIN%\SrcCadWorks.Viewer.exe"
set "WATCHDOG=%BIN%\SrcCadWorks.Watchdog.exe"

set "FAIL=0"
if not exist "!DLL!" (
  echo [HATA] SrcCadWorks.dll yok.
  set "FAIL=1"
) else echo [OK] SrcCadWorks.dll

if not exist "!VIEWER!" (
  echo [HATA] SrcCadWorks.Viewer.exe yok.
  set "FAIL=1"
) else echo [OK] SrcCadWorks.Viewer.exe

if not exist "!WATCHDOG!" (
  echo [HATA] SrcCadWorks.Watchdog.exe yok.
  set "FAIL=1"
) else echo [OK] SrcCadWorks.Watchdog.exe

reg query "HKLM\SOFTWARE\SOLIDWORKS\Addins\!GUID!" >nul 2>&1
if errorlevel 1 (
  echo [HATA] SOLIDWORKS Addins HKLM kaydi yok.
  set "FAIL=1"
) else echo [OK] SOLIDWORKS Addins HKLM kaydi

reg query "HKCU\Software\SOLIDWORKS\AddInsStartup\!GUID!" >nul 2>&1
if errorlevel 1 (
  echo [HATA] SOLIDWORKS AddInsStartup HKCU kaydi yok.
  set "FAIL=1"
) else echo [OK] SOLIDWORKS AddInsStartup HKCU kaydi

if "!FAIL!"=="1" goto fail
echo.
echo [OK] Kurulum kontrolu basarili.
if not defined NOPAUSE pause
exit /b 0

:fail
echo.
if not defined NOPAUSE pause
exit /b 1
