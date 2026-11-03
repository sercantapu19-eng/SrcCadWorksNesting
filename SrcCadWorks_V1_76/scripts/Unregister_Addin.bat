@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "NOPAUSE="
if /I "%~1"=="/nopause" set "NOPAUSE=1"
cd /d "%~dp0\.."

set "GUID={7DA13345-7F3B-4FB4-B9A9-E3BA3867A64C}"
set "DLL=%CD%\src\SrcCadWorks\bin\Release\SrcCadWorks.dll"
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

if exist "!REGASM!" if exist "!DLL!" "!REGASM!" "!DLL!" /unregister >nul 2>&1
reg delete "HKLM\SOFTWARE\SOLIDWORKS\Addins\!GUID!" /f >nul 2>&1
reg delete "HKCU\Software\SOLIDWORKS\AddInsStartup\!GUID!" /f >nul 2>&1
reg delete "HKCR\CLSID\!GUID!" /f >nul 2>&1

echo [OK] SrcCadWorks kayitlari temizlendi.
if not defined NOPAUSE pause
exit /b 0
