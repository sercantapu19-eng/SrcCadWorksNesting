$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repoRoot 'src\SrcCadWorks\bin\Release'
$dll = Join-Path $bin 'SrcCadWorks.dll'
$watchdog = Join-Path $bin 'SrcCadWorks.Watchdog.exe'
$viewer = Join-Path $bin 'SrcCadWorks.Viewer.exe'
$expected = '1.76.0.0'

if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    Write-Host "[HATA] Ana eklenti DLL bulunamadi: $dll" -ForegroundColor Red
    exit 1
}
$actual = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
if ($actual -ne $expected) {
    Write-Host "[HATA] Ana eklenti DLL surumu uyusmuyor. Beklenen: $expected, Bulunan: $actual" -ForegroundColor Red
    exit 1
}
foreach ($required in @($watchdog, $viewer)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Write-Host "[HATA] Gerekli yardimci dosya bulunamadi: $required" -ForegroundColor Red
        exit 1
    }
}
Write-Host "[OK] Ana eklenti DLL surumu: $actual"
Write-Host '[OK] Watchdog ve Viewer ciktilari mevcut.'
Write-Host '[OK] Release x64 cikti dogrulamasi tamamlandi.'
exit 0
