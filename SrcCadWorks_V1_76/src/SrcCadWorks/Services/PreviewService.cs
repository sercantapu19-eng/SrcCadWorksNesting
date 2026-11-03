using System;
using System.Drawing;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    // V1.9.3: Önizleme kayıtlı thumbnail yerine mümkün olduğunda gerçek SOLIDWORKS
    // model görünümünden alınır. SrcCadWorks'in önizleme için görünür hale getirdiği belge,
    // bitmap alındıktan hemen sonra CloseDoc ile kapatılır ve önceki aktif belge geri getirilir.
    // Böylece önceki canlı 3D önizleme davranışı korunurken sekme/pencere kaynağı birikmez.
    internal sealed class PreviewService
    {
        private readonly SldWorks _swApp;
        public string LastFailureReason { get; private set; } = "";
        public PreviewService(SldWorks swApp) => _swApp = swApp;

        public Image GetPreview(LaserPartRow row, int targetWidth, int targetHeight, bool allowSavedPreview = true)
        {
            LastFailureReason = "";
            if (row == null)
            {
                LastFailureReason = "Önizleme satırı boş.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(row.SourcePath))
            {
                LastFailureReason = "Bileşenin dosya yolu boş.";
                return null;
            }

            int width = Math.Max(420, Math.Min(1600, targetWidth * 2));
            int height = Math.Max(240, Math.Min(1000, targetHeight * 2));

            // First look for an already-open/in-memory model. This also permits virtual components
            // whose path is not a normal disk file. OpenDoc6 is attempted only if the file exists.
            Image live = TryGetLivePreview(row, width, height);
            if (live != null) return live;

            if (allowSavedPreview)
            {
                Image saved = GetSavedPreview(row);
                if (saved != null) return saved;
            }
            return null;
        }

        public Image GetPreview(LaserPartRow row) => GetPreview(row, 700, 320);

        public Image GetSavedPreview(LaserPartRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.SourcePath))
            {
                LastFailureReason = "Kayıtlı küçük resim için dosya yolu boş.";
                return null;
            }
            if (!File.Exists(row.SourcePath))
            {
                LastFailureReason = "Kayıtlı küçük resim bulunamadı; bileşenin dosyası diskte yok: " + row.SourcePath;
                return null;
            }
            Image saved = TryGetSavedPreview(row);
            if (saved != null) LastFailureReason = "";
            else if (string.IsNullOrWhiteSpace(LastFailureReason))
                LastFailureReason = "SOLIDWORKS kayıtlı küçük resim bitmap'i döndürmedi.";
            return saved;
        }

        private Image TryGetLivePreview(LaserPartRow row, int width, int height)
        {
            LastFailureReason = "";
            string temp = Path.Combine(Path.GetTempPath(), "SrcCadWorks_livepreview_" + Guid.NewGuid().ToString("N") + ".bmp");
            ModelDoc2 original = null;
            ModelDoc2 model = null;
            ModalDialogGuard dialogGuard = null;
            SolidWorksAutomationScope scope = null;
            bool openedByUs = false;
            bool wasVisibleBefore = false;
            bool switchedConfiguration = false;
            string originalConfiguration = "";
            int previousBackgroundAppearance = 0;
            int previousViewportBackground = 0;
            bool backgroundAppearanceCaptured = false;
            bool viewportBackgroundCaptured = false;
            using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
            try
            {
                // For Source Prep Excel thumbnails, render from the live model rather than
                // trusting a file thumbnail that may contain a saved red/green chroma background.
                // Force a flat white graphics background only for the capture, then restore
                // the user's exact SOLIDWORKS preferences in finally.
                try
                {
                    previousBackgroundAppearance = _swApp.GetUserPreferenceIntegerValue(
                        (int)swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance);
                    backgroundAppearanceCaptured = true;
                }
                catch { }
                try
                {
                    previousViewportBackground = _swApp.GetUserPreferenceIntegerValue(
                        (int)swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground);
                    viewportBackgroundCaptured = true;
                }
                catch { }
                try
                {
                    _swApp.SetUserPreferenceIntegerValue(
                        (int)swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance,
                        (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain);
                }
                catch (Exception ex)
                {
                    Logger.Info("Düz arka plan görünümü ayarlanamadı: " + ex.Message);
                }
                try
                {
                    _swApp.SetUserPreferenceIntegerValue(
                        (int)swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground,
                        ColorTranslator.ToOle(Color.White));
                }
                catch (Exception ex)
                {
                    Logger.Info("Beyaz görünüm arka plan rengi ayarlanamadı: " + ex.Message);
                }

                original = _swApp.ActiveDoc as ModelDoc2;
                dialogGuard = new ModalDialogGuard(null);
                scope = new SolidWorksAutomationScope(_swApp, false, false);

                model = _swApp.GetOpenDocumentByName(row.SourcePath) as ModelDoc2;
                if (model == null)
                {
                    if (!File.Exists(row.SourcePath))
                    {
                        LastFailureReason = "Model SOLIDWORKS'te açık bulunamadı ve diskte dosya yok: " + row.SourcePath;
                        Logger.Info("Canlı önizleme açılamadı. " + LastFailureReason);
                        return null;
                    }
                    int errors = 0, warnings = 0;
                    model = _swApp.OpenDoc6(row.SourcePath,
                        DocumentTypeFromPath(row.SourcePath),
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        row.Configuration ?? "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = model != null;
                    foregroundGuard.RestoreIfSolidWorksStoleFocus();
                    if (model == null)
                    {
                        LastFailureReason = "SOLIDWORKS modeli açamadı (hata=" + errors + ", uyarı=" + warnings + ").";
                        Logger.Info("Canlı önizleme için model açılamadı. " + LastFailureReason + " Yol=" + row.SourcePath);
                        return null;
                    }
                }

                // Belgenin SrcCadWorks önizlemesinden önce kullanıcıya açık bir sekme olup olmadığını kaydet.
                // Zaten kullanıcı tarafından açık olan belgeyi sonunda kapatmayız.
                wasVisibleBefore = ComUtil.SafeVisible(model) && !openedByUs;

                try { originalConfiguration = model.ConfigurationManager?.ActiveConfiguration?.Name ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(row.Configuration) &&
                    !string.Equals(originalConfiguration, row.Configuration, StringComparison.OrdinalIgnoreCase))
                {
                    try { switchedConfiguration = model.ShowConfiguration2(row.Configuration); }
                    catch (Exception ex)
                    {
                        LastFailureReason = "Konfigürasyon değiştirilemedi: " + row.Configuration + " | " + ex.Message;
                        Logger.Info("Canlı önizleme konfigürasyon hatası: " + LastFailureReason);
                        return null;
                    }
                    if (!switchedConfiguration)
                    {
                        LastFailureReason = "SOLIDWORKS konfigürasyonu etkinleştiremedi: " + row.Configuration;
                        Logger.Info("Canlı önizleme konfigürasyon hatası: " + LastFailureReason + " | Yol=" + row.SourcePath);
                        return null;
                    }
                }

                int activateError = 0;
                try { _swApp.ActivateDoc3(model.GetTitle(), true, 0, ref activateError); } catch { }
                foregroundGuard.RestoreIfSolidWorksStoleFocus();
                ComUtil.PumpMessages(35);

                // Kullanıcının zaten açık olan belgesinin görünümünü değiştirmemeye çalış.
                // SrcCadWorks'in açtığı/görünür hale getirdiği belgede ise standart izometrik görünüm üret.
                if (!wasVisibleBefore)
                {
                    try { model.ShowNamedView2("", (int)swStandardViews_e.swIsometricView); } catch { }
                    try { model.ViewZoomtofit2(); } catch { }
                    try { model.GraphicsRedraw2(); } catch { }
                    ComUtil.PumpMessages(35);
                }

                try { model.GraphicsRedraw2(); } catch { }
                ComUtil.PumpMessages(60);
                bool ok = false;
                string saveError = "";
                try { ok = model.SaveBMP(temp, width, height); }
                catch (Exception ex) { saveError = ex.Message; }
                if (!ok || !File.Exists(temp))
                {
                    LastFailureReason = "SOLIDWORKS SaveBMP başarısız (sonuç=" + ok + ", dosya=" + File.Exists(temp) +
                        (string.IsNullOrWhiteSpace(saveError) ? "" : ", hata=" + saveError) + ").";
                    Logger.Info("Canlı önizleme bitmap'i alınamadı. " + LastFailureReason + " | Kod=" + (row.PartCode ?? "") + " | Yol=" + row.SourcePath);
                    return null;
                }

                using (var img = Image.FromFile(temp))
                {
                    LastFailureReason = "";
                    return new Bitmap(img);
                }
            }
            catch (Exception ex)
            {
                LastFailureReason = ex.Message;
                Logger.Info("Canlı önizleme alınamadı: " + ex.Message + " | Yol=" + (row == null ? "" : row.SourcePath));
                return null;
            }
            finally
            {
                // Do not leave a user's system-wide SOLIDWORKS appearance changed after capture.
                try
                {
                    if (viewportBackgroundCaptured)
                        _swApp.SetUserPreferenceIntegerValue(
                            (int)swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground,
                            previousViewportBackground);
                }
                catch { }
                try
                {
                    if (backgroundAppearanceCaptured)
                        _swApp.SetUserPreferenceIntegerValue(
                            (int)swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance,
                            previousBackgroundAppearance);
                }
                catch { }

                // Konfigürasyonu yalnız kullanıcının zaten açık belgesinde değiştirdiysek geri al.
                if (!openedByUs && wasVisibleBefore && switchedConfiguration && model != null && !string.IsNullOrWhiteSpace(originalConfiguration))
                {
                    try { model.ShowConfiguration2(originalConfiguration); } catch { }
                }

                // Önizleme için açılan ya da montaj referansından görünür hale getirilen part sekmesini
                // bitmap alındığı anda kapat. Bu, önceki sürümlerdeki DOC/GDI/USER birikimini önler.
                if (model != null)
                {
                    try { ComUtil.CloseTransientDocument(_swApp, model, row.SourcePath, openedByUs, wasVisibleBefore); } catch { }
                }

                // Kullanıcının önceden aktif olan ana montajına/dokümanına dön.
                if (original != null)
                {
                    try
                    {
                        int e = 0;
                        string title = original.GetTitle();
                        if (!string.IsNullOrWhiteSpace(title)) _swApp.ActivateDoc3(title, false, 0, ref e);
                        foregroundGuard.RestoreIfSolidWorksStoleFocus();
                    }
                    catch { }
                }

                try { scope?.CleanupTransientDocuments(); } catch { }
                try { scope?.Dispose(); } catch { }
                try { dialogGuard?.Dispose(); } catch { }

                if (openedByUs || !wasVisibleBefore) ComUtil.Release(model);
                original = null;
                ComUtil.ManagedCleanup(false);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private Image TryGetSavedPreview(LaserPartRow row)
        {
            string temp = Path.Combine(Path.GetTempPath(), "SrcCadWorks_preview_" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                bool ok = _swApp.GetPreviewBitmapFile(row.SourcePath, row.Configuration ?? "", temp);
                if (!ok || !File.Exists(temp))
                {
                    LastFailureReason = "GetPreviewBitmapFile başarısız (sonuç=" + ok + ", dosya=" + File.Exists(temp) + ").";
                    Logger.Info("Kayıtlı önizleme bitmap'i alınamadı. " + LastFailureReason + " | " + row.SourcePath + " | " + (row.Configuration ?? ""));
                    return null;
                }
                using (var image = Image.FromFile(temp)) return new Bitmap(image);
            }
            catch (Exception ex)
            {
                LastFailureReason = "Kayıtlı küçük resim hatası: " + ex.Message;
                Logger.Info("Kayıtlı önizleme bitmap'i alınamadı: " + ex.Message);
                return null;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        private static int DocumentTypeFromPath(string path)
        {
            string ext = Path.GetExtension(path) ?? "";
            if (ext.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocASSEMBLY;
            if (ext.Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocDRAWING;
            return (int)swDocumentTypes_e.swDocPART;
        }

    }
}
