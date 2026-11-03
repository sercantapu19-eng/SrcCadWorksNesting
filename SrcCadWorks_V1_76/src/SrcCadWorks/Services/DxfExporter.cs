using System;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class DxfExporter
    {
        private readonly SldWorks _swApp;
        public DxfExporter(SldWorks swApp) => _swApp = swApp;

        public void Export(LaserPartRow row, string outputFile, bool includeBendLines)
        {
            string outputDirectory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            // SOLIDWORKS ExportToDWG2 bazı sürümlerde "Parca.dxf.src_GUID.dxf" gibi
            // çift/uzun uzantılı geçici adları geçerli DXF hedefi olarak kabul etmiyor.
            // Güvenli kayıt için benzersiz bir TEMP klasörü kullanıyoruz ama exporter'a
            // her zaman normal "PARCAKODU.dxf" adı veriyoruz. Başarılı çıktı sonradan
            // atomik/yedekli şekilde gerçek hedefe taşınır; mevcut sağlam DXF export
            // başarısız olursa silinmez.
            string stageDirectory = Path.Combine(Path.GetTempPath(), "SrcCadWorks", "DxfExport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageDirectory);
            string stageFileName = Path.GetFileName(outputFile);
            if (string.IsNullOrWhiteSpace(stageFileName)) stageFileName = (row.PartCode ?? "Parca") + ".dxf";
            if (!string.Equals(Path.GetExtension(stageFileName), ".dxf", StringComparison.OrdinalIgnoreCase))
                stageFileName = Path.GetFileNameWithoutExtension(stageFileName) + ".dxf";
            string tempOutput = Path.Combine(stageDirectory, stageFileName);

            ModelDoc2 model = null;
            PartDoc part = null;
            bool openedByUs = false;
            bool wasVisibleBefore = false;
            bool switchedConfiguration = false;
            string originalConfiguration = "";
            int errors = 0, warnings = 0;
            using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
            try
            {
                model = _swApp.GetOpenDocumentByName(row.SourcePath) as ModelDoc2;
                if (model == null)
                {
                    model = _swApp.OpenDoc6(row.SourcePath, (int)swDocumentTypes_e.swDocPART,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        row.Configuration ?? "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = model != null;
                    foregroundGuard.RestoreIfSolidWorksStoleFocus();
                }
                if (model == null) throw new InvalidOperationException("Parça açılamadı: " + row.SourcePath);

                wasVisibleBefore = ComUtil.SafeVisible(model);
                try { originalConfiguration = model.ConfigurationManager?.ActiveConfiguration?.Name ?? ""; } catch { }
                if (!openedByUs && !string.IsNullOrWhiteSpace(row.Configuration) &&
                    !string.Equals(originalConfiguration, row.Configuration, StringComparison.OrdinalIgnoreCase))
                {
                    try { switchedConfiguration = model.ShowConfiguration2(row.Configuration); } catch { }
                }

                part = model as PartDoc;
                if (part == null) throw new InvalidOperationException("Belge PartDoc değil: " + row.SourcePath);

                // SheetMetalOptions: 1=flat pattern geometry, 4=bend lines.
                int options = 1 | (includeBendLines ? 4 : 0);

                // Ana yol: aktif belgeyi değiştirmeden PartDoc üzerinden doğrudan export.
                bool ok = false;
                try
                {
                    ok = part.ExportToDWG2(tempOutput, row.SourcePath,
                        (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal,
                        true, null, false, false, options, null);
                }
                catch { ok = false; }

                // Yalnız doğrudan export gerçekten başarısızsa son çare olarak gizli aktivasyon.
                if (!ok || !OutputFileValidator.IsUsable(tempOutput))
                {
                    try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                    Logger.Info("DXF arka plan export aktif belge istedi; gizli aktivasyon fallback: " + row.PartCode);

                    if (openedByUs || !wasVisibleBefore)
                    {
                        try { ((dynamic)model).Visible = false; } catch { }
                    }

                    int activateError = 0;
                    try
                    {
                        _swApp.ActivateDoc3(model.GetTitle(), false, 0, ref activateError);
                        foregroundGuard.RestoreIfSolidWorksStoleFocus();
                    }
                    catch { }

                    try
                    {
                        ok = part.ExportToDWG2(tempOutput, row.SourcePath,
                            (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal,
                            true, null, false, false, options, null);
                    }
                    catch { ok = false; }
                }

                if (!ok || !OutputFileValidator.IsUsable(tempOutput))
                    throw new InvalidOperationException("DXF export başarısız veya geçersiz geçici çıktı oluştu: " + row.PartCode);

                CommitOutput(tempOutput, outputFile);
            }
            finally
            {
                if (!openedByUs && switchedConfiguration && model != null && !string.IsNullOrWhiteSpace(originalConfiguration))
                {
                    try { model.ShowConfiguration2(originalConfiguration); } catch { }
                }

                if (model != null)
                    ComUtil.CloseTransientDocument(_swApp, model, row.SourcePath, openedByUs, wasVisibleBefore);

                // Geçici belge kapandığında SOLIDWORKS doğal olarak önceki belgeye döner.
                // Ana montajı ActivateDoc3 ile zorla öne çağırmıyoruz.
                if (openedByUs || !wasVisibleBefore) ComUtil.Release(model);
                part = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(stageDirectory) && Directory.Exists(stageDirectory))
                        Directory.Delete(stageDirectory, true);
                }
                catch { }
                ComUtil.ManagedCleanup(false);
            }
        }

        private static void CommitOutput(string tempPath, string outputPath)
        {
            if (!OutputFileValidator.IsUsable(tempPath))
                throw new InvalidOperationException("Geçici DXF doğrulanamadı.");

            if (!File.Exists(outputPath))
            {
                try
                {
                    File.Move(tempPath, outputPath);
                }
                catch (IOException)
                {
                    // TEMP ve çıktı farklı disk/UNC konumlarındaysa Move desteklenmeyebilir.
                    File.Copy(tempPath, outputPath, true);
                    if (!OutputFileValidator.IsUsable(outputPath))
                    {
                        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                        throw new InvalidOperationException("DXF son çıktı doğrulanamadı.");
                    }
                    File.Delete(tempPath);
                }
                return;
            }

            string backup = outputPath + ".src_backup";
            try
            {
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Replace(tempPath, outputPath, backup, true);
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
            catch
            {
                // File.Replace desteklenmeyen bir dosya sistemindeyse eski geçerli dosyayı
                // mümkün olduğunca koruyarak yedekli değişim uygula.
                try
                {
                    if (!File.Exists(backup) && File.Exists(outputPath)) File.Copy(outputPath, backup, true);
                    File.Copy(tempPath, outputPath, true);
                    File.Delete(tempPath);
                    if (!OutputFileValidator.IsUsable(outputPath))
                        throw new InvalidOperationException("DXF son çıktı doğrulanamadı.");
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(backup)) File.Copy(backup, outputPath, true);
                    }
                    catch { }
                    throw;
                }
            }
        }
    }
}
