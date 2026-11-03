using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;
using SrcCadWorks.UI;

namespace SrcCadWorks.Services
{
    internal sealed class ProductionPackageService
    {
        private readonly SldWorks _swApp;
        public ProductionPackageService(SldWorks swApp) => _swApp = swApp;

        public ProductionPackageResult Generate(IList<LaserPartRow> rows, LaserProjectInfo info, string templatePath,
            string laserFolder, ProductionPackageOptions options, Action<PackageProgress> progress, Func<bool> shouldCancel = null,
            Action<LaserPartRow> partCompleted = null, Action cooperativeYield = null, ISet<string> recoverySkipPartCodes = null,
            Action<string, string, string> outputCommitted = null, ResumeSession resume = null)
        {
            var totalWatch = Stopwatch.StartNew();
            var selected = LaserPartRow.GroupByPartCode(rows.Where(r => r.Selected)).OrderBy(r => r.PartCode, StringComparer.OrdinalIgnoreCase).ToList();
            if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Üretim işlemi iptal edildi.");
            if (selected.Count == 0) throw new InvalidOperationException("Seçili lazer parçası yok.");
            var blocking = selected.Where(r => r.HasBlockingError).ToList();
            if (blocking.Count > 0) throw new InvalidOperationException("Üretimi engelleyen parça hataları var. Önce kırmızı satırları düzeltin.");


            if (options == null || (!options.ExportDxf && !options.ExportPdf && !options.ExportExcel && !options.ExportCombinedDxf && !options.ExportCombinedDwg))
                throw new InvalidOperationException("En az bir çıktı türü seçin: DXF, PDF, Excel, Toplu DXF veya Toplu DWG.");
            if (options.ExportExcel && !File.Exists(templatePath))
                throw new FileNotFoundException("Lazer formu şablonu bulunamadı.", templatePath);

            Directory.CreateDirectory(laserFolder);
            cooperativeYield?.Invoke();
            var result = new ProductionPackageResult { SelectedPartCount = selected.Count };
            var recoverySkips = new HashSet<string>(recoverySkipPartCodes ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string skipped in recoverySkips)
                AddUnique(result.RecoverySkippedPartCodes, skipped);
            var monitor = new SolidWorksResourceMonitor(_swApp);
            int overallTotal = (options.ExportDxf ? selected.Count : 0) + (options.ExportPdf ? selected.Count : 0) + (options.ExportExcel ? 1 : 0)
                + (options.ExportCombinedDxf ? 1 : 0) + (options.ExportCombinedDwg ? 1 : 0);
            int overall = 0;

            var dxfReady = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var combinedSourceReady = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pdfReady = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completionNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Lazer Talep Formu Açıklama sütununa yalnız bu üretim koşusuna ait çıktı notları yazılır.
            // Kullanıcının Description alanı değiştirilmez; sonraki üretimde eski hata notu taşınmaz.
            var excelOutputNotes = new Dictionary<LaserPartRow, string>();
            if (resume != null)
            {
                result.Errors.AddRange(resume.PreviousErrors());
                var oldNotes = resume.PreviousNotes();
                foreach (var row in selected) { string note; if (oldNotes.TryGetValue(ResumeSession.Key(row), out note)) excelOutputNotes[row] = note; }
            }
            Func<Dictionary<string, string>> notes = () => excelOutputNotes.ToDictionary(x => ResumeSession.Key(x.Key), x => x.Value);
            Action checkpoint = () => resume?.SaveProgress(result.Errors, notes());
            Action<string, LaserPartRow> clear = (stage, row) =>
            {
                if (resume == null) return;
                var currentNotes = notes(); resume.ClearFailure(stage, row, result.Errors, currentNotes, ResumeSession.Key(row));
                if (row != null) { string note; if (currentNotes.TryGetValue(ResumeSession.Key(row), out note)) excelOutputNotes[row] = note; else excelOutputNotes.Remove(row); }
                checkpoint();
            };
            bool combinedDxfReady = !options.ExportCombinedDxf;
            bool combinedDwgReady = !options.ExportCombinedDwg;
            bool hasPartLevelOutput = options.ExportDxf || options.ExportPdf || options.ExportCombinedDxf || options.ExportCombinedDwg;
            bool excelOnlyOutput = options.ExportExcel && !hasPartLevelOutput;
            bool excelReady = !options.ExportExcel;

            Action<LaserPartRow> tryNotifyCompleted = row =>
            {
                if (partCompleted == null || row == null) return;
                if (!hasPartLevelOutput && !excelOnlyOutput) return;
                string key = RowKey(row);
                if (completionNotified.Contains(key)) return;

                if (excelOnlyOutput)
                {
                    if (!excelReady) return;
                }
                else
                {
                    if (options.ExportDxf && !dxfReady.Contains(key)) return;
                    if ((options.ExportCombinedDxf || options.ExportCombinedDwg) && !combinedSourceReady.Contains(key)) return;
                    if (options.ExportCombinedDxf && !combinedDxfReady) return;
                    if (options.ExportCombinedDwg && !combinedDwgReady) return;
                    if (options.ExportPdf && !pdfReady.Contains(key)) return;
                }

                completionNotified.Add(key);
                try { partCompleted(row); }
                catch (Exception ex) { Logger.Error("İşlenen parçayı gizleme geri çağrısı başarısız: " + row.PartCode, ex); }
            };

            if (options.ExportDxf)
            {
                var dxfWatch = Stopwatch.StartNew();
                var exporter = new DxfExporter(_swApp);
                for (int i = 0; i < selected.Count; i++)
                {
                    CheckCancel(shouldCancel);
                    var row = selected[i];
                    overall++;
                    var outFile = Path.Combine(laserFolder, row.PartCode + ".DXF");
                    bool recoverySkip = recoverySkips.Contains(OutputProvenanceStore.BuildRecoveryKey(row));
                    bool upToDate = resume != null && resume.Reuse("DXF", row, outFile) && !recoverySkip && IsDxfCurrent(outFile, row, options.IncludeBendLines);
                    clear("DXF", row);
                    Report(progress, recoverySkip ? "DXF_KURTARMA_ATLANDI" : (upToDate ? "DXF_ATLANDI" : "DXF"),
                        i + 1, selected.Count, row.PartCode,
                        recoverySkip
                            ? "DXF kurtarma nedeniyle atlandı: " + row.PartCode
                            : (upToDate ? "DXF güncel, tekrar açılmadan atlandı: " + row.PartCode
                                        : "DXF " + (i + 1) + "/" + selected.Count + ": " + row.PartCode),
                        overall, overallTotal, monitor.Snapshot().ShortText, totalWatch.Elapsed,
                        EstimateRemaining(totalWatch.Elapsed, overall, overallTotal), recoverySkip || upToDate);

                    if (recoverySkip)
                    {
                        AddUnique(result.DxfFailedPartCodes, row.PartCode);
                        result.Errors.Add("Kurtarma modu: " + row.PartCode + " DXF işlemi önceki kilitlenme şüphesi nedeniyle atlandı.");
                        checkpoint();
                    monitor.Checkpoint(overall);
                        cooperativeYield?.Invoke();
                        continue;
                    }
                    try
                    {
                        if (upToDate)
                        {
                            result.DxfSkipped++;
                            Logger.Info("DXF güncel, atlandı: " + outFile);
                        }
                        else
                        {
                            resume?.Start("DXF", row);
                            exporter.Export(row, outFile, options.IncludeBendLines);
                            OutputProvenanceStore.MarkCurrent(outFile, "DXF", row.SourcePath, row.Configuration,
                                row.PartCode, DxfOptionsSignature(options.IncludeBendLines));
                            result.DxfCreated++;
                            Logger.Info("DXF oluşturuldu: " + outFile);
                        }
                        resume?.Commit("DXF", row, outFile);
                        AddUnique(result.DxfSuccessfulPartCodes, row.PartCode);
                        dxfReady.Add(RowKey(row));
                        try { outputCommitted?.Invoke("DXF", row.PartCode, outFile); } catch (Exception cacheEx) { Logger.Info("DXF kurtarma cache kaydı başarısız: " + cacheEx.Message); }
                        tryNotifyCompleted(row);
                    }
                    catch (Exception ex)
                    {
                        AddUnique(result.DxfFailedPartCodes, row.PartCode);
                        result.Errors.Add("DXF " + row.PartCode + ": " + ex.Message);
                        AddExcelOutputNote(excelOutputNotes, row, "DXF oluşturulamadı: " + ex.Message);
                        Logger.Error("DXF oluşturulamadı: " + row.PartCode, ex);
                    }
                    checkpoint();
                    monitor.Checkpoint(overall);
                    cooperativeYield?.Invoke();
                }
                dxfWatch.Stop();
                result.DxfDuration = dxfWatch.Elapsed;
            }

            if (options.ExportCombinedDxf || options.ExportCombinedDwg)
            {
                clear("Toplu DXF/DWG", null);
                string tempPartDir = null;
                string combinedSourcePath = null;
                string combinedDxfPath = Path.Combine(laserFolder, "TopluDXF.dxf");
                string combinedDwgPath = Path.Combine(laserFolder, "TopluDWG.dwg");
                try
                {
                    var combinedWatch = Stopwatch.StartNew();
                    var dxfExporter = new DxfExporter(_swApp);
                    tempPartDir = Path.Combine(Path.GetTempPath(), "SrcCadWorks", "TopluDXF_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempPartDir);
                    combinedSourcePath = Path.Combine(tempPartDir, "SrcCadWorks_Combined_Source.dxf");
                    var partSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < selected.Count; i++)
                    {
                        CheckCancel(shouldCancel);
                        var row = selected[i];
                        bool recoverySkip = recoverySkips.Contains(OutputProvenanceStore.BuildRecoveryKey(row));
                        string normal = Path.Combine(laserFolder, row.PartCode + ".DXF");
                        bool reuseNormal = !recoverySkip && (dxfReady.Contains(RowKey(row)) || (resume != null && resume.Reuse("DXF", row, normal))) && IsDxfCurrent(normal, row, options.IncludeBendLines);
                        Report(progress,
                            recoverySkip ? "TOPLU_DXF_KAYNAK_KURTARMA_ATLANDI" : (reuseNormal ? "TOPLU_DXF_KAYNAK_ATLANDI" : "TOPLU_DXF_KAYNAK"),
                            i + 1, selected.Count, row.PartCode,
                            recoverySkip
                                ? "Toplu DXF/DWG kaynak kurtarma nedeniyle atlandı: " + row.PartCode
                                : (reuseNormal ? "Toplu kaynak mevcut DXF'ten alındı: " + row.PartCode
                                               : "Toplu DXF/DWG kaynak " + (i + 1) + "/" + selected.Count + ": " + row.PartCode),
                            overall, overallTotal, monitor.Snapshot().ShortText, totalWatch.Elapsed,
                            EstimateRemaining(totalWatch.Elapsed, Math.Max(1, overall), overallTotal), recoverySkip || reuseNormal);
                        if (recoverySkip)
                        {
                            cooperativeYield?.Invoke();
                            continue;
                        }

                        if (reuseNormal)
                        {
                            partSources[row.PartCode] = normal;
                            combinedSourceReady.Add(RowKey(row));
                            cooperativeYield?.Invoke();
                            continue;
                        }

                        string temp = resume == null ? Path.Combine(tempPartDir, row.PartCode + ".DXF") : Path.Combine(resume.WorkFolder, ResumeLedger.Hash(ResumeSession.Key(row)) + ".DXF");
                        if (resume == null || !resume.Reuse("TOPLU_KAYNAK", row, temp)) { resume?.Start("TOPLU_KAYNAK", row); dxfExporter.Export(row, temp, options.IncludeBendLines); resume?.Commit("TOPLU_KAYNAK", row, temp); }
                        partSources[row.PartCode] = temp;
                        combinedSourceReady.Add(RowKey(row));
                        cooperativeYield?.Invoke();
                    }

                    if (options.ExportCombinedDxf)
                    {
                        overall++;
                        Report(progress, "TOPLU_DXF", 1, 1, "", "TopluDXF.dxf hazırlanıyor...", overall, overallTotal,
                            monitor.Snapshot().ShortText, totalWatch.Elapsed, EstimateRemaining(totalWatch.Elapsed, overall, overallTotal));
                    }

                    cooperativeYield?.Invoke();
                    var combinedRows = selected.Where(r => !recoverySkips.Contains(r.PartCode ?? "")).ToList();
                    if (combinedRows.Count == 0)
                        throw new InvalidOperationException("Toplu DXF/DWG için kurtarma sonrası işlenecek parça kalmadı.");

                    // SrcCadWorks temiz R12 staging yerleşimini oluşturur; bu yalnız ara dosyadır.
                    // Final TopluDXF ve TopluDWG SOLIDWORKS tarafından yeniden kaydedilir.
                    // Final DXF'te yalnız SRC_* renk kodları sonradan normalize edilir.
                    new CombinedDxfBuilder().Build(combinedRows, r => partSources[r.PartCode], combinedSourcePath);

                    var convertWatch = Stopwatch.StartNew();
                    if (options.ExportCombinedDwg)
                    {
                        overall++;
                        Report(progress, "TOPLU_DWG", 1, 1, "", "TopluDWG.dwg hazırlanıyor...", overall, overallTotal,
                            monitor.Snapshot().ShortText, totalWatch.Elapsed, EstimateRemaining(totalWatch.Elapsed, overall, overallTotal));
                    }
                    cooperativeYield?.Invoke();

                    new DwgConverter(_swApp).ConvertOutputs(
                        combinedSourcePath,
                        options.ExportCombinedDxf ? combinedDxfPath : null,
                        options.ExportCombinedDwg ? combinedDwgPath : null,
                        options.ExportCombinedDxf,
                        options.ExportCombinedDwg);
                    convertWatch.Stop();
                    combinedWatch.Stop();

                    if (options.ExportCombinedDxf)
                    {
                        if (!File.Exists(combinedDxfPath) || new FileInfo(combinedDxfPath).Length < 32)
                            throw new InvalidOperationException("TopluDXF.dxf SOLIDWORKS yeniden kaydetme sonrası bulunamadı.");
                        result.CombinedDxfPath = combinedDxfPath;
                        result.CombinedDxfCreated = true;
                        result.CombinedDxfDuration = combinedWatch.Elapsed;
                        combinedDxfReady = true;
                        Logger.Info("Toplu DXF SOLIDWORKS tarafından oluşturuldu; beyaz/geometri renkleri sonradan normalize edildi: " + combinedDxfPath);
                        try { outputCommitted?.Invoke("TOPLU_DXF", "", combinedDxfPath); } catch (Exception cacheEx) { Logger.Info("Toplu DXF cache kaydı başarısız: " + cacheEx.Message); }
                    }
                    else
                    {
                        result.CombinedDxfDuration = combinedWatch.Elapsed - convertWatch.Elapsed;
                    }

                    if (options.ExportCombinedDwg)
                    {
                        if (!File.Exists(combinedDwgPath) || new FileInfo(combinedDwgPath).Length < 32)
                            throw new InvalidOperationException("TopluDWG.dwg SOLIDWORKS yeniden kaydetme sonrası bulunamadı.");
                        result.CombinedDwgDuration = convertWatch.Elapsed;
                        result.CombinedDwgPath = combinedDwgPath;
                        result.CombinedDwgCreated = true;
                        combinedDwgReady = true;
                        Logger.Info("Toplu DWG oluşturuldu: " + combinedDwgPath);
                        try { outputCommitted?.Invoke("TOPLU_DWG", "", combinedDwgPath); } catch (Exception cacheEx) { Logger.Info("Toplu DWG cache kaydı başarısız: " + cacheEx.Message); }
                    }

                    try
                    {
                        string oldDebug = Path.Combine(laserFolder, "TopluDWG_Kaynak_HATA.dxf");
                        if (File.Exists(oldDebug)) File.Delete(oldDebug);
                    }
                    catch { }

                    foreach (var completedRow in selected)
                        tryNotifyCompleted(completedRow);
                }
                catch (Exception ex)
                {
                    string debugSource = "";
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(combinedSourcePath) && File.Exists(combinedSourcePath))
                        {
                            debugSource = Path.Combine(laserFolder, "TopluDWG_Kaynak_HATA.dxf");
                            File.Copy(combinedSourcePath, debugSource, true);
                            Logger.Info("Toplu DXF/DWG hata ayıklama kaynak DXF'i korundu: " + debugSource);
                        }
                    }
                    catch (Exception copyEx)
                    {
                        Logger.Info("Toplu DXF/DWG hata kaynağı kopyalanamadı: " + copyEx.Message);
                        debugSource = "";
                    }

                    string errorText = "Toplu DXF/DWG: " + ex.Message;
                    if (!string.IsNullOrWhiteSpace(debugSource))
                        errorText += " | Kaynak DXF: " + Path.GetFileName(debugSource);
                    result.Errors.Add(errorText);
                    Logger.Error("Toplu DXF/DWG oluşturulamadı", ex);
                }
                finally
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(tempPartDir) && Directory.Exists(tempPartDir)) Directory.Delete(tempPartDir, true);
                    }
                    catch { }
                }
            }

            if (options.ExportPdf)
            {
                var pdfWatch = Stopwatch.StartNew();
                var pdfExporter = new DrawingPdfExporter(_swApp, options.ProcessingSpeed);
                Logger.Info("PDF işlem hızı: " + options.ProcessingSpeed);
                for (int i = 0; i < selected.Count; i++)
                {
                    CheckCancel(shouldCancel);
                    var row = selected[i];
                    overall++;
                    bool recoverySkip = recoverySkips.Contains(OutputProvenanceStore.BuildRecoveryKey(row));
                    bool missingDrawing = !row.HasDrawing || string.IsNullOrWhiteSpace(row.DrawingPath) || !File.Exists(row.DrawingPath);
                    var outFile = Path.Combine(laserFolder, row.PartCode + ".PDF");
                    bool upToDate = resume != null && resume.Reuse("PDF", row, outFile) && !recoverySkip && !missingDrawing && IsPdfCurrent(outFile, row);
                    clear("PDF", row);
                    Report(progress,
                        recoverySkip ? "PDF_KURTARMA_ATLANDI" : (missingDrawing ? "PDF_EKSIK" : (upToDate ? "PDF_ATLANDI" : "PDF")),
                        i + 1, selected.Count, row.PartCode,
                        recoverySkip
                            ? "PDF kurtarma nedeniyle atlandı: " + row.PartCode
                            : (missingDrawing ? "PDF atlandı; teknik resim yok: " + row.PartCode
                                : (upToDate ? "PDF güncel, tekrar açılmadan atlandı: " + row.PartCode
                                            : "PDF " + (i + 1) + "/" + selected.Count + ": " + row.PartCode)),
                        overall, overallTotal, monitor.Snapshot().ShortText, totalWatch.Elapsed,
                        EstimateRemaining(totalWatch.Elapsed, overall, overallTotal), recoverySkip || missingDrawing || upToDate);

                    if (recoverySkip)
                    {
                        AddUnique(result.PdfFailedPartCodes, row.PartCode);
                        AddExcelOutputNote(excelOutputNotes, row, "Teknik resim PDF'i önceki SOLIDWORKS kilitlenmesi sonrası kurtarma modunda atlandı.");
                        result.Errors.Add("Kurtarma modu: " + row.PartCode + " PDF işlemi önceki kilitlenme şüphesi nedeniyle atlandı.");
                        checkpoint();
                    monitor.Checkpoint(overall);
                        cooperativeYield?.Invoke();
                        continue;
                    }
                    if (missingDrawing)
                    {
                        result.PdfMissingDrawing++;
                        AddUnique(result.PdfFailedPartCodes, row.PartCode);
                        AddUnique(result.PdfMissingDrawingPartCodes, row.PartCode);
                        AddExcelOutputNote(excelOutputNotes, row, "Teknik resim PDF'i eksik (teknik resim dosyası bulunamadı).");
                        result.Errors.Add("PDF " + row.PartCode + ": Teknik resim bulunamadı");
                        Logger.Info("PDF atlandı; teknik resim dosyası bulunamadı: " + row.PartCode);
                        checkpoint();
                    monitor.Checkpoint(overall);
                        cooperativeYield?.Invoke();
                        continue;
                    }
                    bool pdfTouchedSolidWorks = false;
                    try
                    {
                        if (upToDate)
                        {
                            result.PdfSkipped++;
                            Logger.Info("PDF güncel, atlandı: " + outFile);
                        }
                        else
                        {
                            pdfTouchedSolidWorks = true;
                            resume?.Start("PDF", row);
                            pdfExporter.Export(row.DrawingPath, outFile);
                            OutputProvenanceStore.MarkCurrent(outFile, "PDF", row.DrawingPath, row.Configuration,
                                row.PartCode, "pdf-v1");
                            result.PdfCreated++;
                            Logger.Info("PDF oluşturuldu: " + outFile);
                        }
                        resume?.Commit("PDF", row, outFile);
                        AddUnique(result.PdfSuccessfulPartCodes, row.PartCode);
                        pdfReady.Add(RowKey(row));
                        try { outputCommitted?.Invoke("PDF", row.PartCode, outFile); } catch (Exception cacheEx) { Logger.Info("PDF kurtarma cache kaydı başarısız: " + cacheEx.Message); }
                        tryNotifyCompleted(row);
                    }
                    catch (FileNotFoundException ex)
                    {
                        result.PdfMissingDrawing++;
                        AddUnique(result.PdfFailedPartCodes, row.PartCode);
                        AddUnique(result.PdfMissingDrawingPartCodes, row.PartCode);
                        AddExcelOutputNote(excelOutputNotes, row, "Teknik resim PDF'i eksik (teknik resim dosyası bulunamadı).");
                        result.Errors.Add("PDF " + row.PartCode + ": " + ex.Message);
                        Logger.Error("PDF oluşturulamadı; teknik resim dosyası bulunamadı: " + row.PartCode, ex);
                    }
                    catch (Exception ex)
                    {
                        AddUnique(result.PdfFailedPartCodes, row.PartCode);
                        AddExcelOutputNote(excelOutputNotes, row, "Teknik resim PDF'i oluşturulamadı.");
                        result.Errors.Add("PDF " + row.PartCode + ": " + ex.Message);
                        Logger.Error("PDF oluşturulamadı: " + row.PartCode, ex);
                    }
                    checkpoint();
                    monitor.Checkpoint(overall);
                    cooperativeYield?.Invoke();
                    if (pdfTouchedSolidWorks) ApplyPdfInterPartDelay(options.ProcessingSpeed, i + 1);
                }
                pdfWatch.Stop();
                result.PdfDuration = pdfWatch.Elapsed;
            }

            if (options.ExportExcel)
            {
                clear("Excel", null);
                var excelWatch = Stopwatch.StartNew();
                CheckCancel(shouldCancel);
                overall++;
                Report(progress, "EXCEL", 1, 1, "", "Lazer Talep Formu dolduruluyor...", overall, overallTotal, monitor.Snapshot().ShortText, totalWatch.Elapsed, EstimateRemaining(totalWatch.Elapsed, overall, overallTotal));
                cooperativeYield?.Invoke();
                try
                {
                    string fileBase = SanitizeFileName(string.IsNullOrWhiteSpace(info.FileCode)
                        ? "LAZER_TALEP_" + DateTime.Today.ToString("dd.MM.yyyy")
                        : info.FileCode.Trim());
                    string excelExtension = Path.GetExtension(templatePath);
                    if (string.IsNullOrWhiteSpace(excelExtension)) excelExtension = ".xlsx";
                    result.ExcelPath = Path.Combine(laserFolder, fileBase + excelExtension);
                    var excelWriter = new ExcelLaserFormWriter();
                    IOException lastExcelIo = null;
                    for (int excelAttempt = 1; excelAttempt <= 5; excelAttempt++)
                    {
                        try
                        {
                            excelWriter.CreateFromTemplate(templatePath, result.ExcelPath, info, selected, excelOutputNotes);
                            lastExcelIo = null;
                            break;
                        }
                        catch (IOException ioEx)
                        {
                            lastExcelIo = ioEx;
                            if (excelAttempt >= 5) throw;
                            Logger.Info("Excel dosyası kullanımda; yeniden denenecek (" + excelAttempt + "/5): " + ioEx.Message);
                            cooperativeYield?.Invoke();
                            Thread.Sleep(1000);
                        }
                    }
                    if (lastExcelIo != null) throw lastExcelIo;
                    excelReady = true;
                    result.ExcelCreated = true;
                    Logger.Info("Lazer formu oluşturuldu: " + result.ExcelPath);
                    try { outputCommitted?.Invoke("EXCEL", "", result.ExcelPath); } catch (Exception cacheEx) { Logger.Info("Excel cache kaydı başarısız: " + cacheEx.Message); }

                    // V1.11.19: Excel-only gizleme artık burada parça parça yapılmaz.
                    // Montajı her satır için tekrar taramak büyük montajlarda SOLIDWORKS'ü kilitleyebiliyordu.
                    // LaserPrepForm, Excel başarıyla tamamlandıktan sonra tüm seçili satırları tek geçişte toplu gizler.
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Excel: " + ex.Message);
                    Logger.Error("Lazer formu oluşturulamadı", ex);
                }
                excelWatch.Stop();
                result.ExcelDuration = excelWatch.Elapsed;
                cooperativeYield?.Invoke();
            }

            checkpoint();
            try
            {
                string report = Path.Combine(laserFolder, "SrcCadWorks_Uretim_Hata_Raporu.txt");
                if (result.Errors.Count > 0)
                {
                    File.WriteAllLines(report, new[]
                    {
                        "SrcCadWorks " + AppVersion.Display + " - Üretim Hata Raporu",
                        "Tarih: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), ""
                    }.Concat(result.Errors).ToArray());
                    result.ErrorReportPath = report;
                }
                else if (File.Exists(report)) File.Delete(report);
            }
            catch (Exception ex) { Logger.Error("Hata raporu yazılamadı", ex); }

            foreach (var completedRow in selected)
                tryNotifyCompleted(completedRow);

            cooperativeYield?.Invoke();
            totalWatch.Stop();
            result.ProductionDuration = totalWatch.Elapsed;
            Report(progress, "TAMAM", 1, 1, "", "Tamamlandı.", Math.Max(overallTotal, overall), Math.Max(1, overallTotal), monitor.Snapshot().ShortText, totalWatch.Elapsed, TimeSpan.Zero);
            return result;
        }

        private static void ApplyPdfInterPartDelay(ProcessingSpeedMode speed, int completedCount)
        {
            int delay = speed == ProcessingSpeedMode.Safe ? 400 : (speed == ProcessingSpeedMode.Fast ? 1 : 200);
            if (delay > 0) Thread.Sleep(delay);

            // Büyük PDF serilerinde SOLIDWORKS'e düzenli nefes payı ver.
            if (completedCount > 0 && completedCount % 10 == 0)
            {
                int rest = speed == ProcessingSpeedMode.Safe ? 1500 : (speed == ProcessingSpeedMode.Normal ? 750 : 0);
                if (rest > 0) Thread.Sleep(rest);
            }
        }

        private static void CheckCancel(Func<bool> shouldCancel)
        {
            if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Üretim işlemi iptal edildi.");
        }

        private static void Report(Action<PackageProgress> progress, string stage, int index, int total, string partCode,
            string message, int overallIndex, int overallTotal, string resourceText, TimeSpan elapsed, TimeSpan? estimatedRemaining,
            bool reusedOutput = false)
        {
            progress?.Invoke(new PackageProgress
            {
                Stage = stage, Index = index, Total = total, PartCode = partCode, Message = message,
                OverallIndex = overallIndex, OverallTotal = overallTotal, ResourceText = resourceText,
                Elapsed = elapsed, EstimatedRemaining = estimatedRemaining, ReusedOutput = reusedOutput
            });
        }

        private static TimeSpan? EstimateRemaining(TimeSpan elapsed, int index, int total)
        {
            if (index <= 1 || total <= 1 || index > total) return null;
            double secondsPerStep = elapsed.TotalSeconds / Math.Max(1, index - 1);
            if (secondsPerStep <= 0 || double.IsNaN(secondsPerStep) || double.IsInfinity(secondsPerStep)) return null;
            return TimeSpan.FromSeconds(Math.Max(0, secondsPerStep * Math.Max(0, total - index + 1)));
        }

        private static string DxfOptionsSignature(bool includeBendLines)
        {
            return "dxf-v2|bend=" + (includeBendLines ? "1" : "0");
        }

        private static bool IsDxfCurrent(string outputPath, LaserPartRow row, bool includeBendLines)
        {
            if (row == null) return false;
            return OutputProvenanceStore.IsCurrent(outputPath, "DXF", row.SourcePath, row.Configuration,
                row.PartCode, DxfOptionsSignature(includeBendLines));
        }

        private static bool IsPdfCurrent(string outputPath, LaserPartRow row)
        {
            if (row == null) return false;
            return OutputProvenanceStore.IsCurrent(outputPath, "PDF", row.DrawingPath, row.Configuration,
                row.PartCode, "pdf-v1");
        }

        private static string RowKey(LaserPartRow row)
        {
            if (row == null) return "";
            return (row.SourcePath ?? "") + "|" + (row.Configuration ?? "") + "|" + (row.PartCode ?? "");
        }


        private static void AddUnique(IList<string> items, string value)
        {
            if (items == null || string.IsNullOrWhiteSpace(value)) return;
            string clean = value.Trim();
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i], clean, StringComparison.OrdinalIgnoreCase)) return;
            items.Add(clean);
        }

        private static void AddExcelOutputNote(IDictionary<LaserPartRow, string> notes, LaserPartRow row, string note)
        {
            if (notes == null || row == null || string.IsNullOrWhiteSpace(note)) return;
            string clean = note.Trim();
            string current;
            if (!notes.TryGetValue(row, out current) || string.IsNullOrWhiteSpace(current))
            {
                notes[row] = clean;
                return;
            }
            if (current.IndexOf(clean, StringComparison.OrdinalIgnoreCase) >= 0) return;
            notes[row] = current.TrimEnd() + " | " + clean;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
