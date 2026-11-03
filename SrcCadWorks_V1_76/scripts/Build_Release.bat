@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "NOPAUSE="
if /I "%~1"=="/nopause" set "NOPAUSE=1"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "!VSWHERE!" goto vswhere_ok
echo [HATA] Visual Studio 2022 / vswhere bulunamadi.
goto fail

:vswhere_ok
set "MSBUILD="
for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if defined MSBUILD goto msbuild_ok
echo [HATA] MSBuild bulunamadi.
goto fail

:msbuild_ok
cd /d "%~dp0\.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Source_Preflight.ps1"
if errorlevel 1 goto fail
"!MSBUILD!" SrcCadWorks.sln /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m
if errorlevel 1 goto fail

echo.
echo [OK] Release x64 derleme tamamlandi.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify_Build.ps1"
if errorlevel 1 goto fail
if not defined NOPAUSE pause
exit /b 0

:fail
echo.
if not defined NOPAUSE pause
exit /b 1
