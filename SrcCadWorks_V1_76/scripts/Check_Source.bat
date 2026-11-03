@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."
echo [BILGI] Kaynak: %CD%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Source_Preflight.ps1"
if errorlevel 1 goto fail
echo.
echo [OK] Kaynak on-kontrolu basarili.
pause
exit /b 0
:fail
echo.
echo [HATA] Kaynak on-kontrolu basarisiz.
pause
exit /b 1
