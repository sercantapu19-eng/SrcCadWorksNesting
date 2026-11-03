$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Visual Studio / Build Tools required, as for the main project.' }
$csc = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (!$csc -or !(Test-Path $csc)) { throw 'Roslyn C# compiler not found.' }
$testOutput = Join-Path $env:TEMP ('SrcCadWorksResumeTests_' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $csc /nologo /target:exe "/out:$testOutput" /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot '..\src\SrcCadWorks\Services\ResumeLedger.cs') (Join-Path $PSScriptRoot 'ResumeLedgerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Resume tests failed.' }
} finally { Remove-Item $testOutput -ErrorAction SilentlyContinue }
