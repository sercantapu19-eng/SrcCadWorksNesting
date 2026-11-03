@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "NOPAUSE="
if /I "%~1"=="/nopause" set "NOPAUSE=1"
cd /d "%~dp0\.."

set "BIN=%CD%\src\SrcCadWorks\bin\Release"
set "DLL=%CD%\src\SrcCadWorks\bin\Release\SrcCadWorks.dll"
set "WATCHDOG=%CD%\src\SrcCadWorks\bin\Release\SrcCadWorks.Watchdog.exe"
set "VIEWER=%CD%\src\SrcCadWorks\bin\Release\SrcCadWorks.Viewer.exe"
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
set "SWAPI=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"

set "TARGET_VERSION=1.76.0.0"
set "CURRENT_VERSION="
if exist "!DLL!" (
  for /f "usebackq delims=" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Item -LiteralPath '!DLL!').VersionInfo.FileVersion"`) do set "CURRENT_VERSION=%%V"
)
if /I "!CURRENT_VERSION!"=="!TARGET_VERSION!" goto dll_ok
echo [BILGI] SrcCadWorks DLL surumu hedef surumle ayni degil. Kaynaklar Release x64 olarak yeniden derleniyor...
call "%~dp0Build_Release.bat" /nopause
if errorlevel 1 goto build_error
if not exist "!DLL!" goto build_error
for /f "usebackq delims=" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Item -LiteralPath '!DLL!').VersionInfo.FileVersion"`) do set "CURRENT_VERSION=%%V"
if /I not "!CURRENT_VERSION!"=="!TARGET_VERSION!" (
  echo [HATA] SrcCadWorks ana eklenti DLL surumu beklenen degil. Beklenen: !TARGET_VERSION!, Bulunan: !CURRENT_VERSION!
  goto build_error
)
goto dll_ok

:build_error
echo [HATA] DLL derleme sonrasinda da bulunamadi:
echo !DLL!
echo Visual Studio Hata Listesi / Cikti penceresindeki ilk derleme hatasini kontrol edin.
goto fail

:dll_ok
if exist "!WATCHDOG!" goto watchdog_ok
echo [HATA] Harici watchdog bulunamadi:
echo !WATCHDOG!
echo SrcCadWorks.sln icin Release x64 - Cozumu Yeniden Derle calistirin.
goto fail

:watchdog_ok
if exist "!VIEWER!" goto viewer_ok
echo [HATA] Montaj goruntuleyici bulunamadi:
echo !VIEWER!
echo SrcCadWorks.sln icin Release x64 - Cozumu Yeniden Derle calistirin.
goto fail

:viewer_ok
if exist "!REGASM!" goto regasm_ok
echo [HATA] 64-bit RegAsm bulunamadi:
echo !REGASM!
goto fail

:regasm_ok
for %%F in (SolidWorks.Interop.sldworks.dll SolidWorks.Interop.swconst.dll SolidWorks.Interop.swpublished.dll) do call :ensure_interop "%%F"
if errorlevel 1 goto fail

echo [BILGI] SrcCadWorks COM kaydi yapiliyor...
"!REGASM!" "!DLL!" /codebase /tlb
if errorlevel 1 goto reg_error

set "TPL=%LOCALAPPDATA%\SrcCadWorks\templates"
if not exist "!TPL!" mkdir "!TPL!"
copy /Y "%CD%\templates\LAZER FORMU.xlsx" "!TPL!\LAZER FORMU.xlsx" >nul
copy /Y "%CD%\templates\KAYNAK FORMU.xls" "!TPL!\KAYNAK FORMU.xls" >nul
copy /Y "%CD%\templates\MONTAJ FORMU.xls" "!TPL!\MONTAJ FORMU.xls" >nul

set "ASSET=%LOCALAPPDATA%\SrcCadWorks\assets"
if not exist "!ASSET!" mkdir "!ASSET!"
copy /Y "%CD%\assets\SrcCadWorks_Logo.png" "!ASSET!\SrcCadWorks_Logo.png" >nul
if exist "%CD%\assets\SrcCadWorks_Logo_Transparent.png" copy /Y "%CD%\assets\SrcCadWorks_Logo_Transparent.png" "!ASSET!\SrcCadWorks_Logo_Transparent.png" >nul
if exist "%CD%\assets\SrcCadWorks_Logo_Main.png" copy /Y "%CD%\assets\SrcCadWorks_Logo_Main.png" "!ASSET!\SrcCadWorks_Logo_Main.png" >nul
if exist "%CD%\assets\SrcCadWorks_Logo_Small.png" copy /Y "%CD%\assets\SrcCadWorks_Logo_Small.png" "!ASSET!\SrcCadWorks_Logo_Small.png" >nul

echo.
echo [OK] SrcCadWorks kaydedildi.
echo SOLIDWORKS'u tamamen kapatip yeniden acin.
echo Ardindan: Araclar ^> Eklentiler ^> SrcCadWorks.
if not defined NOPAUSE pause
exit /b 0

:ensure_interop
set "INTFILE=%~1"
if exist "!BIN!\!INTFILE!" exit /b 0
if exist "!SWAPI!\!INTFILE!" goto copy_interop
echo [HATA] Gerekli SolidWorks kutuphanesi bulunamadi: !INTFILE!
echo Beklenen klasor: !SWAPI!
echo SolidWorks API redist yolunu kontrol edin.
exit /b 1

:copy_interop
echo [BILGI] !INTFILE! bin\Release klasorune kopyalaniyor...
copy /Y "!SWAPI!\!INTFILE!" "!BIN!\!INTFILE!" >nul
if errorlevel 1 exit /b 1
exit /b 0

:reg_error
echo.
echo [HATA] Kayit basarisiz.
echo Bu BAT dosyasini Yonetici olarak calistirdiginizdan emin olun.
echo bin\Release altinda SolidWorks.Interop DLL'lerini kontrol edin.
goto fail

:fail
echo.
if not defined NOPAUSE pause
exit /b 1
