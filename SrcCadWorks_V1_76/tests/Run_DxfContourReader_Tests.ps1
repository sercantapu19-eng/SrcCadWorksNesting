$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Visual Studio / Build Tools required.' }
$csc = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (!$csc -or !(Test-Path $csc)) { throw 'Roslyn C# compiler not found.' }
$root = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $env:TEMP ('SrcCadWorksDxfContourTests_' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $csc /nologo /target:exe "/out:$testOutput" /reference:System.Core.dll `
        (Join-Path $root 'src\SrcCadWorks\Services\SmartNestEngine.cs') `
        (Join-Path $root 'src\SrcCadWorks\Services\DxfContourReader.cs') `
        (Join-Path $PSScriptRoot 'DxfContourReaderTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'DXF contour test compilation failed.' }
    & $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'DXF contour tests failed.' }
} finally { Remove-Item $testOutput -ErrorAction SilentlyContinue }
