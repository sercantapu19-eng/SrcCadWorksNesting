@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0\.."

set "GUID={7DA13345-7F3B-4FB4-B9A9-E3BA3867A64C}"
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

net session >nul 2>&1
if errorlevel 1 (
  echo [BILGI] Yonetici yetkisi isteniyor...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

tasklist /FI "IMAGENAME eq SLDWORKS.exe" 2>nul | find /I "SLDWORKS.exe" >nul
if not errorlevel 1 (
  echo [HATA] SOLIDWORKS acik.
  echo Tum SOLIDWORKS pencerelerini kapatin, sonra Clean_Reinstall.bat dosyasini tekrar calistirin.
  pause
  exit /b 1
)

echo [1/6] Eski COM/SOLIDWORKS kayitlari temizleniyor...
if exist "%CD%\src\SrcCadWorks\bin\Release\SrcCadWorks.dll" (
  "%REGASM%" "%CD%\src\SrcCadWorks\bin\Release\SrcCadWorks.dll" /unregister >nul 2>&1
)
reg delete "HKLM\SOFTWARE\SOLIDWORKS\Addins\%GUID%" /f >nul 2>&1
reg delete "HKCU\Software\SOLIDWORKS\AddInsStartup\%GUID%" /f >nul 2>&1
reg delete "HKCR\CLSID\%GUID%" /f >nul 2>&1

echo [2/6] Eski bin/obj dosyalari temizleniyor...
for %%P in (SrcCadWorks SrcCadWorks.Viewer SrcCadWorks.Watchdog) do (
  if exist "%CD%\src\%%P\bin" rmdir /s /q "%CD%\src\%%P\bin"
  if exist "%CD%\src\%%P\obj" rmdir /s /q "%CD%\src\%%P\obj"
)

echo [3/6] Release x64 sifirdan derleniyor...
call "%~dp0Build_Release.bat" /nopause
if errorlevel 1 goto fail

echo [4/6] Add-in kaydediliyor...
call "%~dp0Register_Addin.bat" /nopause
if errorlevel 1 goto fail

echo [5/6] Kurulum dogrulaniyor...
call "%~dp0Verify_Install.bat" /nopause
if errorlevel 1 goto fail

echo [6/6] Tamam.
echo.
echo [OK] Temiz kurulum tamamlandi.
echo Simdi SOLIDWORKS'u acin. SrcCadWorks otomatik yuklenmelidir.
pause
exit /b 0

:fail
echo.
echo [HATA] Temiz kurulum tamamlanamadi.
pause
exit /b 1
