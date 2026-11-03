param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$errors = New-Object System.Collections.Generic.List[string]

$csFiles = Get-ChildItem -Path (Join-Path $Root "src") -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch "SrcCadWorks\.CadViewer" }

foreach ($file in $csFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw

    # SolidWorks.Interop.sldworks also exposes Environment.
    # In files importing that namespace, System.Environment must be explicit.
    if ($text -match 'using\s+SolidWorks\.Interop\.sldworks\s*;') {
        if ($text -match '(?<![\w\.])Environment\.') {
            $errors.Add("Belirsiz Environment kullanimi: $($file.FullName)")
        }
    }


    if ($text -match 'using\s+System\.Threading\s*;' -and
        $text -match 'using\s+System\.Windows\.Forms\s*;' -and
        $text -match '(?<![\w\.])Timer\s+[_A-Za-z]') {
        $errors.Add("Belirsiz Timer tipi: System.Threading ve System.Windows.Forms birlikte kullaniliyor: $($file.FullName)")
    }

    # Viewport3D lives in System.Windows.Controls, not Media3D.
    if ($text -match 'Wpf3D\.Viewport3D') {
        $errors.Add("Yanlis WPF Viewport3D namespace: $($file.FullName)")
    }

    # Common extension-method mistake: Average/Select/Where need System.Linq unless fully qualified.
    if (($text -match '\.Average\s*\(' -or $text -match '\.Select\s*\(' -or $text -match '\.Where\s*\(') -and
        $text -notmatch 'using\s+System\.Linq\s*;' -and
        $text -notmatch 'System\.Linq\.Enumerable\.') {
        $errors.Add("LINQ extension kullaniliyor ama System.Linq yok: $($file.FullName)")
    }

    if ($text -match '\b_selectNext\b') {
        $errors.Add("Eski _selectNext secim durumu kalmis: $($file.FullName)")
    }

    if ($text -match 'Face2\s+\w+\s*=\s*\w+\.GetFirstFace\s*\(\s*\)\s*;') {
        $errors.Add("SolidWorks COM GetFirstFace sonucu Face2 icin explicit/as cast gerektiriyor: $($file.FullName)")
    }
    if ($text -match '=\s*\w+\.GetNextFace\s*\(\s*\)\s*;') {
        $errors.Add("SolidWorks COM GetNextFace sonucu Face2 icin explicit/as cast gerektiriyor: $($file.FullName)")
    }


    if ($file.Name -eq "SolidWorksSceneLoader.cs") {
        if ($text -match 'triangleCount\s*/\s*\(double\)') {
            $errors.Add("Mesh ucgen atlama/sampling kodu geri gelmis: $($file.FullName)")
        }
        if ($text -match 'CacheVersion\s*=\s*1') {
            $errors.Add("Eski eksik mesh cache surumu kullaniliyor: $($file.FullName)")
        }
    }
}


$ownedWindowFile = Join-Path $Root "src\SrcCadWorks\Services\SolidWorksOwnedWindow.cs"
if (Test-Path $ownedWindowFile) {
    $ownedText = Get-Content -LiteralPath $ownedWindowFile -Raw
    if ($ownedText -match 'TopMost\s*=\s*true' -or
        $ownedText -match 'HWND_TOPMOST' -or
        $ownedText -match 'SetWinEventHook' -or
        $ownedText -match 'System\.Threading\.Timer') {
        $errors.Add("SolidWorksOwnedWindow yalnız Win32 owner kullanmalı; TopMost/hook/timer geri gelmemeli.")
    }
    if ($ownedText -notmatch 'GetHWndx64' -or
        $ownedText -notmatch 'GWLP_HWNDPARENT' -or
        $ownedText -match 'Process\.GetCurrentProcess\(\)\.MainWindowHandle') {
        $errors.Add("SolidWorksOwnedWindow gerçek SOLIDWORKS IFrame owner handle mantığı eksik.")
    }
}

$nativeDxfFile = Join-Path $Root "src\SrcCadWorks\Services\DwgConverter.cs"
if (Test-Path $nativeDxfFile) {
    $nativeDxfText = Get-Content -LiteralPath $nativeDxfFile -Raw
    if ($nativeDxfText -match 'File\.Copy\(sourceDxfPath,\s*finalDxfPath') {
        $errors.Add("TopluDXF final R12 staging dosyası doğrudan kopyalanmamalı; final DXF SOLIDWORKS tarafından yazılmalı.")
    }
    if ($nativeDxfText -notmatch 'DxfColorNormalizer\.NormalizeProductionColors') {
        $errors.Add("SOLIDWORKS final TopluDXF renk post-process adımı eksik.")
    }
}

# Repeated ActivateDoc3 calls in production loops are forbidden. DwgConverter is
# the one deliberate exception: older SOLIDWORKS builds may require the imported
# combined drawing to be active for the FINAL DXF/DWG SaveAs. It may contain one
# guarded fallback only; never more than one and never outside activateImportedFallback.
$weldFocusFile = Join-Path $Root "src\SrcCadWorks\UI\WeldPrepForm.cs"
if (Test-Path $weldFocusFile) {
    $weldFocusText = Get-Content -LiteralPath $weldFocusFile -Raw
    if ($weldFocusText -match '\.ActivateDoc3\s*\(') {
        $errors.Add("Üretim odak güvenliği: WeldPrepForm icinde ActivateDoc3 kalmis: $weldFocusFile")
    }
}

$dwgFocusFile = Join-Path $Root "src\SrcCadWorks\Services\DwgConverter.cs"
if (Test-Path $dwgFocusFile) {
    $dwgFocusText = Get-Content -LiteralPath $dwgFocusFile -Raw
    $dwgActivateCount = [regex]::Matches($dwgFocusText, '\.ActivateDoc3\s*\(').Count
    if ($dwgActivateCount -gt 1) {
        $errors.Add("Toplu DXF/DWG odak güvenliği: DwgConverter icinde birden fazla ActivateDoc3 var: $dwgActivateCount")
    }
    if ($dwgActivateCount -eq 1 -and
        ($dwgFocusText -notmatch 'if\s*\(\s*activateImportedFallback\s*\)' -or
         $dwgFocusText -notmatch 'Visible\s*=\s*false')) {
        $errors.Add("Toplu DXF/DWG odak güvenliği: tek ActivateDoc3 yalnız gizli activateImportedFallback icinde olabilir.")
    }
}

$pdfFocusFile = Join-Path $Root "src\SrcCadWorks\Services\DrawingPdfExporter.cs"
if (Test-Path $pdfFocusFile) {
    $pdfFocusText = Get-Content -LiteralPath $pdfFocusFile -Raw
    if ($pdfFocusText -match 'TrySetDrawingDocumentVisibility\s*\(\s*true\s*\)' -or
        $pdfFocusText -match 'TrySetDocumentVisible\s*\(\s*drawing\s*,\s*true\s*\)') {
        $errors.Add("PDF üretim odak güvenliği: teknik resim görünür fallback'e geçmemeli.")
    }
}



$autoDrawingFile = Join-Path $Root "src\SrcCadWorks\Services\AutomaticDrawingService.cs"
if (Test-Path $autoDrawingFile) {
    $autoDrawingText = Get-Content -LiteralPath $autoDrawingFile -Raw
    if ($autoDrawingText -notmatch 'Create1stAngleViews2') {
        $errors.Add("Otomatik Teknik Resim: ilk-açı 3-view üretimi eksik.")
    }
    if ($autoDrawingText -notmatch 'FlipView\s*=\s*false') {
        $errors.Add("Otomatik Teknik Resim: Flat Pattern FlipView=false yön koruması eksik.")
    }
    if ($autoDrawingText -notmatch 'ShowSheetMetalBendNotes\s*=\s*true') {
        $errors.Add("Otomatik Teknik Resim: sac büküm notları etkin değil.")
    }
    if ($autoDrawingText -notmatch 'ValidateRequiredViews' -or $autoDrawingText -notmatch 'ValidatePlacement') {
        $errors.Add("Otomatik Teknik Resim: görünüş sayısı/yerleşim doğrulaması eksik.")
    }
    if ($autoDrawingText -match 'CreateFlatPatternViewFromModelView3\([^\)]*true\s*\)') {
        $errors.Add("Otomatik Teknik Resim: Flat Pattern görünüşü Flip=true ile oluşturulmamalı.")
    }
}

$newModuleFiles = @(
    (Join-Path $Root "src\SrcCadWorks\UI\ManufacturingPrepForm.cs"),
    (Join-Path $Root "src\SrcCadWorks\UI\MachiningPrepForm.cs"),
    (Join-Path $Root "src\SrcCadWorks\Services\ManufacturingAssemblyScanner.cs"),
    (Join-Path $Root "src\SrcCadWorks\Services\MachiningAssemblyScanner.cs"),
    (Join-Path $Root "src\SrcCadWorks\Services\WeldmentProfileReader.cs"),
    (Join-Path $Root "src\SrcCadWorks\Services\StepExporter.cs"),
    (Join-Path $Root "templates\IMALAT_KONTROL_FORMU.xlsx"),
    (Join-Path $Root "templates\TALASLI_IMALAT_FORMU.xlsx")
)
foreach ($newModuleFile in $newModuleFiles) {
    if (-not (Test-Path $newModuleFile)) { $errors.Add("Yeni imalat modülü dosyası eksik: $newModuleFile") }
}

$machiningUiFile = Join-Path $Root "src\SrcCadWorks\UI\MachiningPrepForm.cs"
$machiningScannerFile = Join-Path $Root "src\SrcCadWorks\Services\MachiningAssemblyScanner.cs"
if (Test-Path $machiningUiFile) {
    $machiningUiText = Get-Content -LiteralPath $machiningUiFile -Raw
    if ($machiningUiText -notmatch 'DataPropertyName\s*=\s*"StepSelected"') {
        $errors.Add("Talaşlı İmalat STEP checkbox sütunu eksik.")
    }
}
if (Test-Path $machiningScannerFile) {
    $machiningScannerText = Get-Content -LiteralPath $machiningScannerFile -Raw
    if ($machiningScannerText -notmatch 'StepSelected\s*=\s*false') {
        $errors.Add("Talaşlı İmalat STEP varsayılan false davranışı eksik.")
    }
}

if ($errors.Count -gt 0) {
    Write-Host "[HATA] Kaynak on-kontrolu basarisiz:" -ForegroundColor Red
    foreach ($e in $errors) { Write-Host " - $e" -ForegroundColor Red }
    exit 1
}

Write-Host "[OK] Kaynak on-kontrolu gecti." -ForegroundColor Green
exit 0
