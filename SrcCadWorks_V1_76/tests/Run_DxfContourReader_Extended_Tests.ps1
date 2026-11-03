$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Visual Studio / Build Tools required.' }
$csc = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (!$csc -or !(Test-Path $csc)) { throw 'Roslyn C# compiler not found.' }
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $env:TEMP ('SrcCadWorksExtendedDxfTests_' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $csc /nologo /target:exe "/out:$out" /reference:System.Core.dll (Join-Path $root 'src\SrcCadWorks\Services\SmartNestEngine.cs') (Join-Path $root 'src\SrcCadWorks\Services\DxfContourReader.cs') (Join-Path $PSScriptRoot 'DxfContourReaderExtendedTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Extended DXF test compilation failed.' }
    & $out
    if ($LASTEXITCODE -ne 0) { throw 'Extended DXF regression failed.' }
} finally { Remove-Item $out -ErrorAction SilentlyContinue }
