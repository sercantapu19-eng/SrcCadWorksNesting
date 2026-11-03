using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class ExcelWeldFormWriter
    {
        private readonly SldWorks _swApp;
        private int _pictureCount;
        public string ImageLayoutSummary => "Satıra bağlı görsel: " + _pictureCount;
        private readonly Dictionary<string, string> _previewFileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ExcelWeldFormWriter(SldWorks swApp) => _swApp = swApp;

        public void CreateFromTemplate(string templatePath, string outputPath, WeldProjectInfo info, IList<WeldFormRow> rows, IDictionary<string,string> processFileCodes, Action<int,int,string> progress = null, IDictionary<string, string> outputNotes = null, Action<WeldFormRow> rowStarting = null, int assemblyMultiplier = 1, Func<bool> shouldCancel = null, bool assemblyMode = false, string sharedPreviewCacheDirectory = null)
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Kaynak formu şablonu bulunamadı.", templatePath);
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(outputDirectory);
            CleanupAbandonedStagingFiles(outputDirectory);
            if (string.Equals(Path.GetFullPath(templatePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Çıktı dosyası şablon dosyasıyla aynı olamaz.");
            _pictureCount = 0;
            Type excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null) throw new InvalidOperationException("Microsoft Excel masaüstü uygulaması bulunamadı.");

            dynamic excel = null, workbook = null, sheet = null;
            var tempImages = new List<string>();
            int montagePreviewCacheHits = 0;
            int montageLivePreviewAttempts = 0;
            int montageSlowPreviewRows = 0;
            long montagePreviewElapsedTicks = 0;
            string stagingOutputPath = Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(outputPath) + ".building_" + Guid.NewGuid().ToString("N") + Path.GetExtension(outputPath));
            try
            {
                ThrowIfCancellationRequested(shouldCancel);
                excel = Activator.CreateInstance(excelType);
                excel.Visible = false;
                excel.DisplayAlerts = false;
                // Open the template read-only, then create a modern-format output.
                // In-cell pictures must not be saved back into the legacy .xls format.
                workbook = excel.Workbooks.Open(templatePath, 0, true);
                int format = string.Equals(Path.GetExtension(outputPath), ".xlsm", StringComparison.OrdinalIgnoreCase) ? 52 : 51;
                // Build to a staging file: if ESC is pressed, the previous finished Excel
                // remains untouched and the unfinished workbook is discarded.
                workbook.SaveAs(stagingOutputPath, format);
                // Keep an interrupted build artifact out of the user's production folder view.
                TrySetHidden(stagingOutputPath, true);
                sheet = workbook.Worksheets[1];
                if (assemblyMode) ReplaceExactText(sheet, "KAYNAK PROSES FORMU", "MONTAJ PARÇA LİSTESİ");

                WriteNamedField(sheet, "PROJE ADI", info.ProjectName);
                WriteNamedField(sheet, "DOSYA ADI", info.FileName);
                WriteNamedField(sheet, "DOSYA KODU", info.FileCode);
                WriteNamedField(sheet, "HAZIRLAYAN", info.Preparer);
                WriteNamedField(sheet, "ONAYLAYAN", info.Approver);
                WriteDate(sheet);

                int headerRow = FindHeaderRow(sheet);
                int firstRow = headerRow + 1;
                assemblyMultiplier = Math.Max(1, assemblyMultiplier);
                // Montaj formu kullanıcı örneğindeki A:J düzenini korur; AÇIKLAMA/NOTLAR
                // sütunu Montaj çıktısında kalır. Kaynak formunun mevcut A:I davranışı değişmez.
                try { sheet.AutoFilterMode = false; } catch { }
                if (!assemblyMode)
                {
                    try { sheet.Cells[headerRow, 10].ClearContents(); } catch { }
                }
                else
                {
                    try { sheet.Columns[10].ColumnWidth = 32; sheet.Columns[10].WrapText = true; } catch { }
                }
                ClearOldDataAndPictures(sheet, firstRow);

                if (rows != null && rows.Count > 0)
                {
                    int neededLast = firstRow + rows.Count - 1;
                    CopyRowFormat(sheet, firstRow, neededLast, assemblyMode);

                    // V1.66: export identifiers and hierarchical sequence labels as TEXT before
                    // bulk Value2 assignment. Without this, Excel may coerce values such as
                    // "1.1" to a regional date (displayed as "1.Oca" in Turkish Excel) and
                    // numeric-looking part codes such as "46023" to numbers. Quantity and mass
                    // stay numeric in columns E/F.
                    SetRangeNumberFormat(sheet, "B" + firstRow + ":D" + neededLast, "@");
                    SetRangeNumberFormat(sheet, "G" + firstRow + ":" + (assemblyMode ? "J" : "I") + neededLast, "@");

                    var preview = new PreviewService(_swApp);

                    // Excel COM calls for individual cell writes are expensive for long BOMs.
                    // Prepare the worksheet body in memory and transfer it in one range operation;
                    // pictures and progress remain row-by-row because their positioning is individual.
                    int columnCount = assemblyMode ? 10 : 9;
                    object[,] cellValues = new object[rows.Count, columnCount];
                    for (int i = 0; i < rows.Count; i++)
                    {
                        ThrowIfCancellationRequested(shouldCancel);
                        var row = rows[i];
                        string op = WeldAssemblyScanner.NormalizeOperation(row.OperationType);
                        string fileCode = "";
                        if (processFileCodes != null) processFileCodes.TryGetValue(op, out fileCode);
                        row.FileCode = fileCode ?? "";

                        cellValues[i, 0] = null; // BELGE ÖNİZLEME picture is inserted below
                        cellValues[i, 1] = row.Sequence ?? "";
                        cellValues[i, 2] = row.PartCode ?? "";
                        cellValues[i, 3] = row.ProductDescription ?? "";
                        cellValues[i, 4] = Math.Max(0, row.Quantity);
                        cellValues[i, 5] = ConvertMassForExcel(row.MassText);
                        cellValues[i, 6] = row.ColorCode ?? "";
                        cellValues[i, 7] = op;
                        cellValues[i, 8] = row.FileCode ?? "";
                        if (assemblyMode)
                        {
                            string rowNote = "";
                            if (outputNotes != null) outputNotes.TryGetValue(row.PartCode ?? "", out rowNote);
                            cellValues[i, 9] = rowNote ?? "";
                        }
                    }

                    string lastDataColumn = assemblyMode ? "J" : "I";
                    dynamic dataRange = null;
                    dynamic massRange = null;
                    try
                    {
                        dataRange = sheet.Range["A" + firstRow + ":" + lastDataColumn + neededLast];
                        dataRange.Value2 = cellValues;
                        dataRange.RowHeight = 72;
                    }
                    finally { FinalRelease(dataRange); }
                    try
                    {
                        massRange = sheet.Range["F" + firstRow + ":F" + neededLast];
                        massRange.NumberFormatLocal = assemblyMode ? "0,0" : "0,00 \"g\"";
                    }
                    catch { }
                    finally { FinalRelease(massRange); }

                    for (int i = 0; i < rows.Count; i++)
                    {
                        ThrowIfCancellationRequested(shouldCancel);
                        int r = firstRow + i;
                        var row = rows[i];
                        try { rowStarting?.Invoke(row); } catch { }

                        string previewFailureReason;
                        bool previewCacheHit;
                        bool livePreviewAttempted;
                        var previewRowWatch = Stopwatch.StartNew();
                        string imageFile = TryCreatePreview(preview, row, out previewFailureReason,
                            assemblyMode, sharedPreviewCacheDirectory, out previewCacheHit, out livePreviewAttempted);
                        previewRowWatch.Stop();
                        if (assemblyMode)
                        {
                            montagePreviewElapsedTicks += previewRowWatch.Elapsed.Ticks;
                            if (previewCacheHit) montagePreviewCacheHits++;
                            if (livePreviewAttempted) montageLivePreviewAttempts++;
                            if (previewRowWatch.Elapsed.TotalSeconds >= 10)
                            {
                                montageSlowPreviewRows++;
                                Logger.Info("Montaj Excel önizleme yavaş satır: " + (row.PartCode ?? "") +
                                    " | Süre=" + previewRowWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " sn" +
                                    " | Cache=" + previewCacheHit + " | Canlı deneme=" + livePreviewAttempted);
                            }
                        }
                        ThrowIfCancellationRequested(shouldCancel);
                        if (!string.IsNullOrWhiteSpace(imageFile))
                        {
                            // Montaj Hazırla uses a persistent preview cache across output workbooks
                            // and later runs. Per-workbook scratch images remain local and are deleted
                            // by this writer.
                            if (!IsPathUnderDirectory(imageFile, sharedPreviewCacheDirectory)) tempImages.Add(imageFile);
                            InsertPicture(sheet, r, imageFile);
                        }
                        else
                        {
                            const string detail = "Excel görseli alınamadı veya kalite kontrolünden geçmedi";
                            string reasonText = string.IsNullOrWhiteSpace(previewFailureReason) ? "Neden ayrıntısı log dosyasına yazıldı." : previewFailureReason.Trim();
                            string imageError = "HATA: " + detail + " — " + reasonText;
                            if (outputNotes != null)
                            {
                                string key = row.PartCode ?? "";
                                string previous;
                                outputNotes.TryGetValue(key, out previous);
                                if (string.IsNullOrWhiteSpace(previous)) outputNotes[key] = imageError;
                                else if (!previous.Contains(detail)) outputNotes[key] = previous + " • " + imageError;
                            }
                            Logger.Info("Montaj/Kaynak Excel görseli üretilemedi. Sıra=" + (row.Sequence ?? "") +
                                " | Kod=" + (row.PartCode ?? "") + " | Yol=" + (row.SourcePath ?? "") +
                                " | Konfigürasyon=" + (row.Configuration ?? "") + " | Neden=" + reasonText);
                            if (assemblyMode)
                            {
                                string rowNote = "";
                                if (outputNotes != null) outputNotes.TryGetValue(row.PartCode ?? "", out rowNote);
                                try { sheet.Cells[r, 10].Value2 = rowNote ?? ""; } catch { }
                            }
                        }
                        try { progress?.Invoke(i + 1, rows.Count, row.PartCode ?? ""); } catch { }
                        if ((i % 5) == 0) System.Windows.Forms.Application.DoEvents();
                        ThrowIfCancellationRequested(shouldCancel);
                    }

                    try
                    {
                        dynamic filterRange = sheet.Range["A" + headerRow + (assemblyMode ? ":J" : ":I") + neededLast];
                        filterRange.AutoFilter();
                        FinalRelease(filterRange);
                    }
                    catch { }
                }

                // İkinci bir çalışma sayfası oluşturulmaz; veri mevcut şablon sayfasında kalır.
                ThrowIfCancellationRequested(shouldCancel);
                // Tanılama ayrıntıları işlem raporu/günlüğünde tutulur. Montaj modunda görsel
                // hatası varsa şablonun mevcut AÇIKLAMA/NOTLAR sütunu da bu bilgiyi gösterebilir.
                try { sheet.Activate(); } catch { }
                ThrowIfCancellationRequested(shouldCancel);
                workbook.Save();
                TrySetHidden(stagingOutputPath, true);
                ThrowIfCancellationRequested(shouldCancel);
                workbook.Close(true);
                workbook = null;
                excel.Quit();
                excel = null;
                // The staging file becomes the final output only at commit time; clear Hidden
                // first so the final workbook has ordinary file attributes.
                TrySetHidden(stagingOutputPath, false);
                CommitStagingOutput(stagingOutputPath, Path.GetFullPath(outputPath));
                stagingOutputPath = null;
            }
            finally
            {
                if (assemblyMode)
                {
                    Logger.Info("Montaj Excel önizleme performansı | Satır=" + (rows == null ? 0 : rows.Count) +
                        " | Cache isabeti=" + montagePreviewCacheHits +
                        " | Canlı önizleme denemesi=" + montageLivePreviewAttempts +
                        " | 10 sn üzeri satır=" + montageSlowPreviewRows +
                        " | Önizleme toplamı=" + TimeSpan.FromTicks(montagePreviewElapsedTicks).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " sn");
                }
                // Cancellation/failure must close the staging workbook without saving it.
                if (workbook != null) { try { workbook.Close(false); } catch { } }
                FinalRelease(sheet);
                FinalRelease(workbook);
                if (excel != null) { try { excel.Quit(); } catch { } }
                FinalRelease(excel);
                foreach (string file in tempImages) { try { File.Delete(file); } catch { } }
                if (!string.IsNullOrWhiteSpace(stagingOutputPath))
                {
                    try
                    {
                        if (File.Exists(stagingOutputPath))
                        {
                            TrySetHidden(stagingOutputPath, false);
                            File.Delete(stagingOutputPath);
                        }
                    }
                    catch { }
                }
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
        }

        private static void ThrowIfCancellationRequested(Func<bool> shouldCancel)
        {
            if (shouldCancel != null && shouldCancel())
                throw new OperationCanceledException("Kaynak Excel üretimi ESC ile iptal edildi.");
        }

        private static void TrySetHidden(string path, bool hidden)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                FileAttributes attributes = File.GetAttributes(path);
                if (hidden) attributes |= FileAttributes.Hidden;
                else attributes &= ~FileAttributes.Hidden;
                File.SetAttributes(path, attributes);
            }
            catch { }
        }

        private static void CleanupAbandonedStagingFiles(string outputDirectory)
        {
            // Clean orphaned SrcCadWorks workbooks from prior interrupted attempts.
            // An exclusive open check prevents deleting a workbook another process is still writing.
            try
            {
                foreach (string candidate in Directory.GetFiles(outputDirectory, "*.building_*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        using (var stream = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            // The exclusive handle proves the workbook is not currently open for writing.
                        }
                        TrySetHidden(candidate, false);
                        File.Delete(candidate);
                        Logger.Info("Önceki tamamlanmamış kaynak Excel geçici dosyası temizlendi: " + candidate);
                    }
                    catch (IOException) { /* Still in use; leave it alone. */ }
                    catch (UnauthorizedAccessException) { /* Locked/protected; leave it alone. */ }
                    catch (Exception ex) { Logger.Info("Geçici kaynak Excel dosyası temizlenemedi: " + candidate + " | " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Kaynak Excel geçici dosyaları taranamadı: " + ex.Message);
            }
        }

        private static void CommitStagingOutput(string stagingPath, string outputPath)
        {
            if (!File.Exists(stagingPath)) throw new FileNotFoundException("Kaynak Excel geçici çıktısı bulunamadı.", stagingPath);
            if (!File.Exists(outputPath))
            {
                File.Move(stagingPath, outputPath);
                return;
            }

            try
            {
                File.Replace(stagingPath, outputPath, null, true);
                return;
            }
            catch (Exception replaceError)
            {
                // File.Replace is not available on every filesystem. Roll back to a copy
                // of the prior output if the move-based replacement fails.
                string backupPath = outputPath + ".scwbackup_" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(outputPath, backupPath, false);
                    File.Delete(outputPath);
                    File.Move(stagingPath, outputPath);
                    try { File.Delete(backupPath); } catch { }
                }
                catch
                {
                    try
                    {
                        if (!File.Exists(outputPath) && File.Exists(backupPath)) File.Move(backupPath, outputPath);
                        else if (File.Exists(outputPath) && File.Exists(backupPath)) File.Delete(backupPath);
                    }
                    catch { }
                    throw new IOException("Kaynak Excel çıktısı son konumuna taşınamadı. Önceki dosya korunmaya çalışıldı. " + replaceError.Message, replaceError);
                }
            }
        }

        private static object ConvertMassForExcel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            double grams;
            if (MassValueFormatter.TryParseGrams(text, true, out grams)) return grams;
            return text;
        }

        private string TryCreatePreview(PreviewService preview, WeldFormRow row, out string failureReason,
            bool assemblyMode, string sharedPreviewCacheDirectory, out bool cacheHit, out bool livePreviewAttempted)
        {
            failureReason = "";
            cacheHit = false;
            livePreviewAttempted = false;
            if (row == null)
            {
                failureReason = "Montaj satırı boş.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(row.SourcePath))
            {
                failureReason = "Bileşenin SOLIDWORKS dosya yolu boş; bu satır için model açılamıyor.";
                Logger.Info("Kaynak Excel görseli atlandı. Sıra=" + (row.Sequence ?? "") + " | Kod=" + (row.PartCode ?? "") + " | " + failureReason);
                return null;
            }

            // Montaj önizleme işleme biçimi değişti: eski sürümlerin kırmızı/yeşil zeminli
            // PNG önbelleklerini tekrar kullanma. Kaynak belge aynı olsa da yeni beyaz-zemin
            // yakalama yoluyla bir kez daha görsel üret.
            string cacheKey = (assemblyMode ? "MontajBeyazZeminV2|" : "KaynakOnizlemeV1|") + BuildPreviewCacheKey(row);
            string cachedFile;
            if (_previewFileCache.TryGetValue(cacheKey, out cachedFile) && !string.IsNullOrWhiteSpace(cachedFile) && File.Exists(cachedFile))
            {
                cacheHit = true;
                return cachedFile;
            }

            // If a model is open with unsaved edits, do not trust a disk-cache image or write
            // a persistent image for it. A fresh preview is safer until the document is saved.
            bool allowSharedCache = !string.IsNullOrWhiteSpace(sharedPreviewCacheDirectory) &&
                !IsSourceDocumentDirty(row.SourcePath);
            string sharedCacheFile = allowSharedCache
                ? GetSharedPreviewCacheFile(sharedPreviewCacheDirectory, cacheKey)
                : null;
            if (!string.IsNullOrWhiteSpace(sharedCacheFile) && File.Exists(sharedCacheFile))
            {
                try
                {
                    var fi = new FileInfo(sharedCacheFile);
                    if (fi.Length > 100 && IsValidCachedPreview(sharedCacheFile))
                    {
                        _previewFileCache[cacheKey] = sharedCacheFile;
                        cacheHit = true;
                        try { File.SetLastWriteTimeUtc(sharedCacheFile, DateTime.UtcNow); } catch { }
                        return sharedCacheFile;
                    }
                    try { File.Delete(sharedCacheFile); } catch { }
                }
                catch
                {
                    try { File.Delete(sharedCacheFile); } catch { }
                }
            }

            string scratchDir = Path.Combine(Path.GetTempPath(), "SrcCadWorks", "WeldPreview");
            Directory.CreateDirectory(scratchDir);
            if (!string.IsNullOrWhiteSpace(sharedCacheFile))
                Directory.CreateDirectory(sharedPreviewCacheDirectory);

            var laserRow = new LaserPartRow
            {
                PartCode = row.PartCode,
                SourcePath = row.SourcePath,
                Configuration = row.Configuration,
                DrawingPath = row.DrawingPath
            };

            // Kaynak Hazırla ile aynı davranış: önce SOLIDWORKS'ten geçici olarak beyaz
            // arka planla canlı görüntü al. Bu yol başarısız olursa kayıtlı thumbnail denenir
            // ve kırmızı/yeşil, kenara bağlı chroma zemin beyaza temizlenir.
            // Montaj cache anahtarına işleme sürümü eklenmesi önceki kırmızı PNG'lerin
            // cache hit olarak geri gelmesini engeller; yeni görseller sonraki çalışmalarda
            // yine kalıcı cache'ten hızlıca kullanılabilir.
            bool[] attempts = new[] { false, true };
            var failures = new List<string>();
            foreach (bool useSavedPreview in attempts)
            {
                Image captured = null;
                if (!useSavedPreview) livePreviewAttempted = true;
                try
                {
                    captured = useSavedPreview
                        ? preview.GetSavedPreview(laserRow)
                        : preview.GetPreview(laserRow, 760, 380, allowSavedPreview: false);

                    if (captured == null)
                    {
                        string why = preview.LastFailureReason;
                        failures.Add((useSavedPreview ? "Kayıtlı küçük resim" : "Canlı önizleme") + ": " +
                            (string.IsNullOrWhiteSpace(why) ? "görüntü alınamadı" : why));
                        continue;
                    }

                    using (captured)
                    {
                        using (Bitmap prepared = useSavedPreview
                            ? PrepareSavedExcelPreview(captured, 640, 320)
                            : PrepareExcelPreview(captured, 640, 320))
                        {
                            if (!LooksLikeShadedPreview(prepared))
                            {
                                failures.Add((useSavedPreview ? "Kayıtlı küçük resim" : "Canlı önizleme") + ": kalite kontrolü görüntüyü boş/yetersiz buldu");
                                continue;
                            }

                            string file = !string.IsNullOrWhiteSpace(sharedCacheFile)
                                ? sharedCacheFile
                                : Path.Combine(scratchDir, Guid.NewGuid().ToString("N") + ".png");
                            try
                            {
                                prepared.Save(file, ImageFormat.Png);
                                if (!File.Exists(file) || new FileInfo(file).Length <= 100)
                                    throw new IOException("Önizleme PNG dosyası kaydedilemedi veya boş.");
                                _previewFileCache[cacheKey] = file;
                                return file;
                            }
                            catch
                            {
                                try { if (File.Exists(file)) File.Delete(file); } catch { }
                                throw;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((useSavedPreview ? "Kayıtlı küçük resim" : "Canlı önizleme") + ": " + ex.Message);
                }
                finally
                {
                    // using(captured) disposes successful images; failed attempts are disposed here.
                    // Image.Dispose is idempotent for System.Drawing.Image.
                    if (captured != null) { try { captured.Dispose(); } catch { } }
                }
            }

            failureReason = string.Join("; ", failures.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(failureReason)) failureReason = "Kayıtlı ve canlı önizleme kalite kontrolünü geçemedi.";
            Logger.Info("Montaj Excel görseli alınamadı. Sıra=" + (row.Sequence ?? "") + " | Kod=" + (row.PartCode ?? "") +
                " | Yol=" + row.SourcePath + " | Konfigürasyon=" + (row.Configuration ?? "") + " | " + failureReason);
            return null;
        }

        private static string BuildPreviewCacheKey(WeldFormRow row)
        {
            string path = row == null ? "" : (row.SourcePath ?? "");
            string configuration = row == null ? "" : (row.Configuration ?? "");
            try
            {
                string fullPath = Path.GetFullPath(path);
                var info = new FileInfo(fullPath);
                if (info.Exists)
                    return fullPath + "|" + configuration + "|" + info.Length.ToString(CultureInfo.InvariantCulture) +
                        "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                return fullPath + "|" + configuration + "|MISSING";
            }
            catch { return path + "|" + configuration; }
        }

        private bool IsSourceDocumentDirty(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return false;
            try
            {
                var model = _swApp.GetOpenDocumentByName(sourcePath) as ModelDoc2;
                return model != null && model.GetSaveFlag();
            }
            catch { return false; }
        }

        private static bool IsValidCachedPreview(string path)
        {
            try
            {
                using (var bitmap = new Bitmap(path))
                    return bitmap.Width >= 100 && bitmap.Height >= 50 && LooksLikeShadedPreview(bitmap);
            }
            catch { return false; }
        }

        private static string GetSharedPreviewCacheFile(string cacheDirectory, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory)) return null;
            try
            {
                byte[] hash;
                using (MD5 md5 = MD5.Create()) hash = md5.ComputeHash(Encoding.UTF8.GetBytes(cacheKey ?? ""));
                var name = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) name.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return Path.Combine(cacheDirectory, name.ToString() + ".png");
            }
            catch { return null; }
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static Bitmap PrepareSavedExcelPreview(Image source, int canvasWidth, int canvasHeight)
        {
            using (Bitmap cleaned = RemoveEdgeConnectedChromaBackground(source))
                return PrepareExcelPreview(cleaned, canvasWidth, canvasHeight);
        }

        private static Bitmap RemoveEdgeConnectedChromaBackground(Image source)
        {
            var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }

            int width = bitmap.Width;
            int height = bitmap.Height;
            if (width < 4 || height < 4) return bitmap;

            Color[] corners =
            {
                bitmap.GetPixel(1, 1),
                bitmap.GetPixel(width - 2, 1),
                bitmap.GetPixel(1, height - 2),
                bitmap.GetPixel(width - 2, height - 2)
            };
            int r = (corners[0].R + corners[1].R + corners[2].R + corners[3].R) / 4;
            int g = (corners[0].G + corners[1].G + corners[2].G + corners[3].G) / 4;
            int b = (corners[0].B + corners[1].B + corners[2].B + corners[3].B) / 4;
            Color background = Color.FromArgb(r, g, b);

            // Only clean an unmistakable, edge-connected chromatic matte. If the corners do not
            // agree, the image may be tightly cropped around the model; leave all colors intact.
            for (int i = 0; i < corners.Length; i++)
                if (MaxChannelDistance(corners[i], background) > 42) return bitmap;
            int maximum = Math.Max(r, Math.Max(g, b));
            int minimum = Math.Min(r, Math.Min(g, b));
            if (maximum - minimum < 48 || maximum < 105 || (r > 235 && g > 235 && b > 235))
                return bitmap;

            // Flood-fill through a locked pixel buffer instead of repeated Bitmap.GetPixel /
            // SetPixel calls. This matters when a large assembly BOM has hundreds of thumbnails.
            BitmapData data = null;
            try
            {
                data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite,
                    PixelFormat.Format24bppRgb);
                int stride = Math.Abs(data.Stride);
                byte[] pixels = new byte[stride * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                bool[] visited = new bool[width * height];
                var queue = new Queue<int>();
                for (int x = 0; x < width; x++)
                {
                    QueueBackgroundPixel(pixels, stride, width, height, visited, queue, x, 0, background);
                    QueueBackgroundPixel(pixels, stride, width, height, visited, queue, x, height - 1, background);
                }
                for (int y = 1; y < height - 1; y++)
                {
                    QueueBackgroundPixel(pixels, stride, width, height, visited, queue, 0, y, background);
                    QueueBackgroundPixel(pixels, stride, width, height, visited, queue, width - 1, y, background);
                }

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    int x = index % width;
                    int y = index / width;
                    int offset = y * stride + x * 3;
                    pixels[offset] = 255;     // B
                    pixels[offset + 1] = 255; // G
                    pixels[offset + 2] = 255; // R
                    if (x > 0) QueueBackgroundPixel(pixels, stride, width, height, visited, queue, x - 1, y, background);
                    if (x + 1 < width) QueueBackgroundPixel(pixels, stride, width, height, visited, queue, x + 1, y, background);
                    if (y > 0) QueueBackgroundPixel(pixels, stride, width, height, visited, queue, x, y - 1, background);
                    if (y + 1 < height) QueueBackgroundPixel(pixels, stride, width, height, visited, queue, x, y + 1, background);
                }
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                if (data != null) bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private static void QueueBackgroundPixel(byte[] pixels, int stride, int width, int height,
            bool[] visited, Queue<int> queue, int x, int y, Color background)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int index = y * width + x;
            if (visited[index]) return;
            visited[index] = true;
            int offset = y * stride + x * 3;
            int distance = Math.Max(Math.Abs(pixels[offset + 2] - background.R),
                Math.Max(Math.Abs(pixels[offset + 1] - background.G), Math.Abs(pixels[offset] - background.B)));
            if (distance <= 58) queue.Enqueue(index);
        }

        private static int MaxChannelDistance(Color a, Color b)
        {
            return Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        }

        private static Bitmap PrepareExcelPreview(Image source, int canvasWidth, int canvasHeight)
        {
            canvasWidth = Math.Max(180, canvasWidth);
            canvasHeight = Math.Max(90, canvasHeight);
            var result = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                // The source is a live SaveBMP capture with SOLIDWORKS temporarily set to a
                // plain white background. Preserve every source pixel: do not chroma-key or
                // recolor the model, because genuine red/green/blue faces must remain unchanged.
                g.Clear(Color.White);
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                const int margin = 8;
                double scale = Math.Min(
                    (canvasWidth - margin * 2.0) / Math.Max(1, source.Width),
                    (canvasHeight - margin * 2.0) / Math.Max(1, source.Height));
                int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                int h = Math.Max(1, (int)Math.Round(source.Height * scale));
                int x = (canvasWidth - w) / 2;
                int y = (canvasHeight - h) / 2;
                g.DrawImage(source, new Rectangle(x, y, w, h));
            }
            return result;
        }

        private static bool LooksLikeShadedPreview(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return false;

            int sampled = 0;
            int nonWhite = 0;
            int step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 140);

            for (int y = 0; y < bitmap.Height; y += step)
            {
                for (int x = 0; x < bitmap.Width; x += step)
                {
                    Color c = bitmap.GetPixel(x, y);
                    sampled++;
                    bool whiteish = c.A <= 8 || (c.R >= 245 && c.G >= 245 && c.B >= 245);
                    if (!whiteish) nonWhite++;
                }
            }

            if (sampled <= 0 || nonWhite < 3) return false;
            double fillRatio = nonWhite / (double)sampled;

            // The previous 1.2% threshold rejected many small valid parts and thin profiles.
            // Only reject images that are genuinely almost blank; a small silhouette is valid.
            if (fillRatio < 0.0005) return false;
            return true;
        }

        private void InsertPicture(dynamic sheet, int row, string imageFile)
        {
            dynamic cell = null, shape = null, anchor = null;
            try
            {
                cell = sheet.Cells[row, 1];
                double cellLeft = Convert.ToDouble(cell.Left);
                double cellTop = Convert.ToDouble(cell.Top);
                double cellWidth = Convert.ToDouble(cell.Width);
                double cellHeight = Convert.ToDouble(cell.Height);
                double cw = Math.Max(1, cellWidth - 4);
                double ch = Math.Max(1, cellHeight - 4);
                using (var img = Image.FromFile(imageFile))
                {
                    double scale = Math.Min(cw / Math.Max(1, img.Width), ch / Math.Max(1, img.Height));
                    double width = Math.Max(1, img.Width * scale);
                    double height = Math.Max(1, img.Height * scale);
                    double left = cellLeft + (cellWidth - width) / 2;
                    double top = cellTop + (cellHeight - height) / 2;

                    shape = sheet.Shapes.AddPicture(imageFile, false, true, left, top, width, height);
                    shape.LockAspectRatio = -1; // msoTrue
                    // Floating image is deliberately kept. PlacePictureInCell() relies on Excel's
                    // active-cell selection and had moved images to neighboring rows in some builds.
                    // xlMoveAndSize anchors the picture to the exact cell area and follows row sorting/filtering.
                    shape.Placement = 1; // xlMoveAndSize
                    shape.Left = left;
                    shape.Top = top;
                    shape.Width = width;
                    shape.Height = height;

                    try
                    {
                        anchor = shape.TopLeftCell;
                        int anchorRow = Convert.ToInt32(anchor.Row);
                        if (anchorRow != row)
                        {
                            // Avoid cell-edge rounding placing the image on the previous row.
                            shape.Top = cellTop + 1.5;
                            shape.Left = cellLeft + 1.5;
                            FinalRelease(anchor);
                            anchor = shape.TopLeftCell;
                            anchorRow = Convert.ToInt32(anchor.Row);
                        }
                        if (anchorRow != row)
                            Logger.Info("Excel görseli hücre satırı doğrulaması uymadı. Hedef=" + row + " | Çapa=" + anchorRow + " | Dosya=" + imageFile);
                    }
                    catch { }
                    _pictureCount++;
                }
            }
            finally
            {
                FinalRelease(anchor);
                FinalRelease(shape);
                FinalRelease(cell);
            }
        }

        private static void CopyCellFormat(dynamic sheet, int row, int sourceColumn, int targetColumn)
        {
            dynamic source = null, target = null;
            try
            {
                source = sheet.Cells[row, sourceColumn];
                target = sheet.Cells[row, targetColumn];
                source.Copy();
                target.PasteSpecial(-4122); // xlPasteFormats
                try { sheet.Application.CutCopyMode = false; } catch { }
            }
            catch { }
            finally
            {
                FinalRelease(target);
                FinalRelease(source);
            }
        }

        private static void ClearOldDataAndPictures(dynamic sheet, int firstRow)
        {
            try { sheet.Range["A" + firstRow + ":J5000"].ClearContents(); } catch { }
            try
            {
                int count = sheet.Shapes.Count;
                for (int i = count; i >= 1; i--)
                {
                    dynamic shape = sheet.Shapes[i];
                    try
                    {
                        dynamic cell = shape.TopLeftCell;
                        int r = Convert.ToInt32(cell.Row);
                        FinalRelease(cell);
                        if (r >= firstRow) shape.Delete();
                    }
                    catch { }
                    finally { FinalRelease(shape); }
                }
            }
            catch { }
        }

        private static void CopyRowFormat(dynamic sheet, int firstRow, int neededLast, bool includeNotesColumn)
        {
            if (neededLast <= firstRow) return;
            try
            {
                dynamic source = sheet.Rows[firstRow];
                source.Copy();
                dynamic target = sheet.Range["A" + (firstRow + 1) + (includeNotesColumn ? ":J" : ":I") + neededLast];
                target.PasteSpecial(-4122); // xlPasteFormats only; never duplicate embedded pictures/shapes
                target.ClearContents();
                try { sheet.Application.CutCopyMode = false; } catch { }
                FinalRelease(target); FinalRelease(source);
            }
            catch { }
        }

        private static int FindHeaderRow(dynamic sheet)
        {
            try
            {
                for (int r = 1; r <= 40; r++)
                {
                    string a = Convert.ToString(sheet.Cells[r, 1].Value2) ?? "";
                    string c = Convert.ToString(sheet.Cells[r, 3].Value2) ?? "";
                    if (a.IndexOf("Önizleme", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("Parça/Montaj", StringComparison.OrdinalIgnoreCase) >= 0)
                        return r;
                }
            }
            catch { }
            return 13;
        }

        private static void ReplaceExactText(dynamic sheet, string oldText, string newText)
        {
            // Montaj formu aynı dokuz sütunlu düzeni kullanır; sadece sayfa başlığını değiştir.
            try
            {
                for (int r = 1; r <= 20; r++)
                {
                    for (int c = 1; c <= 20; c++)
                    {
                        dynamic cell = sheet.Cells[r, c];
                        string text = Convert.ToString(cell.Value2) ?? "";
                        if (string.Equals(Normalize(text), Normalize(oldText), StringComparison.OrdinalIgnoreCase))
                        {
                            cell.Value2 = newText;
                            FinalRelease(cell);
                            return;
                        }
                        FinalRelease(cell);
                    }
                }
            }
            catch (Exception ex) { Logger.Info("Form başlığı değiştirilemedi: " + ex.Message); }
        }

        private static void WriteNamedField(dynamic sheet, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                for (int r = 1; r <= 20; r++)
                {
                    for (int c = 1; c <= 5; c++)
                    {
                        dynamic cell = sheet.Cells[r, c];
                        string text = Convert.ToString(cell.Value2) ?? "";
                        if (string.Equals(Normalize(text), Normalize(label), StringComparison.OrdinalIgnoreCase))
                        {
                            int targetCol = c + 1;
                            try
                            {
                                dynamic ma = cell.MergeArea;
                                targetCol = Convert.ToInt32(ma.Column) + Convert.ToInt32(ma.Columns.Count);
                                FinalRelease(ma);
                            }
                            catch { }
                            sheet.Cells[r, targetCol].Value2 = value;
                            FinalRelease(cell);
                            return;
                        }
                        FinalRelease(cell);
                    }
                }
            }
            catch { }
        }

        private static void WriteDate(dynamic sheet)
        {
            // Tarihi Excel/Windows bölgesel ayarından bağımsız olarak kullanıcının istediği
            // görünümde yaz: 02.10.2026. Şablonda TARİH hücresi taşınsa bile ilk 20x12 alanda bulunur.
            try
            {
                string dateText = DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"));
                for (int r = 1; r <= 20; r++)
                {
                    for (int c = 1; c <= 12; c++)
                    {
                        dynamic labelCell = null;
                        try
                        {
                            labelCell = sheet.Cells[r, c];
                            string text = Convert.ToString(labelCell.Value2) ?? "";
                            if (!Normalize(text).StartsWith("TARIH")) continue;

                            int targetCol = c + 1;
                            try
                            {
                                dynamic ma = labelCell.MergeArea;
                                targetCol = Convert.ToInt32(ma.Column) + Convert.ToInt32(ma.Columns.Count);
                                FinalRelease(ma);
                            }
                            catch { }
                            dynamic target = sheet.Cells[r, targetCol];
                            try
                            {
                                target.NumberFormat = "@";
                                target.Value2 = dateText;
                            }
                            finally { FinalRelease(target); }
                            return;
                        }
                        finally { FinalRelease(labelCell); }
                    }
                }
            }
            catch { }
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant().Replace("İ", "I").Replace("Ş", "S").Replace("Ğ", "G").Replace("Ü", "U").Replace("Ö", "O").Replace("Ç", "C").Replace(" ", "").Replace(":", "");
        }

        private static void SetRangeNumberFormat(dynamic sheet, string address, string format)
        {
            dynamic range = null;
            try
            {
                range = sheet.Range[address];
                range.NumberFormat = format;
            }
            catch (Exception ex)
            {
                Logger.Info("Excel hücre biçimi uygulanamadı (" + address + "): " + ex.Message);
            }
            finally { FinalRelease(range); }
        }

        private static void FinalRelease(object obj)
        {
            if (obj == null) return;
            try { if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); } catch { }
        }
    }
}
