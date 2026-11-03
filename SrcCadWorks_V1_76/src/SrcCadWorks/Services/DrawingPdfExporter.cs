using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class DrawingPdfExporter
    {
        private readonly SldWorks _swApp;
        private readonly ProcessingSpeedMode _speed;

        public DrawingPdfExporter(SldWorks swApp, ProcessingSpeedMode speed = ProcessingSpeedMode.Normal)
        {
            _swApp = swApp;
            _speed = speed;
        }

        public void Export(string drawingPath, string outputPdf)
        {
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
                throw new FileNotFoundException("Kayıtlı teknik resim bulunamadı.", drawingPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPdf));

            Exception backgroundFailure = null;
            string backgroundTemp = BuildTempPdfPath(outputPdf, "bg");
            try
            {
                ExportCore(drawingPath, backgroundTemp, false);
                CommitValidatedPdf(backgroundTemp, outputPdf);
                Logger.Info("PDF sessiz/arka plan yöntemiyle oluşturuldu: " + outputPdf);
                return;
            }
            catch (Exception ex)
            {
                backgroundFailure = ex;
                Logger.Info("PDF sessiz/arka plan yöntemi başarısız; gizli aktif-belge yedek yöntemi denenecek: " + ex.Message);
                SafeDelete(backgroundTemp);
            }

            string visibleTemp = BuildTempPdfPath(outputPdf, "fallback");
            try
            {
                ExportCore(drawingPath, visibleTemp, true);
                CommitValidatedPdf(visibleTemp, outputPdf);
                Logger.Info("PDF gizli aktif-belge yedek yöntemiyle oluşturuldu: " + outputPdf);
            }
            catch (Exception fallbackEx)
            {
                SafeDelete(visibleTemp);
                string first = backgroundFailure == null ? "bilinmiyor" : backgroundFailure.Message;
                throw new InvalidOperationException(
                    "PDF oluşturulamadı. Sessiz yöntem: " + first + " | Gizli aktif-belge yedek yöntemi: " + fallbackEx.Message,
                    fallbackEx);
            }
        }

        private void ExportCore(string drawingPath, string tempPdf, bool visibleFallback)
        {
            ModelDoc2 drawing = null;
            bool openedByUs = false;
            bool wasVisibleBefore = false;
            bool forcedDrawingVisibility = false;
            int openErrors = 0, openWarnings = 0;

            try
            {
                drawing = GetOpenDocumentWithBusyRetry(drawingPath);
                if (drawing == null)
                {
                    // Her iki yöntemde de drawing tipi görünmez kalır.
                    TrySetDrawingDocumentVisibility(false);
                    forcedDrawingVisibility = true;

                    drawing = OpenDrawingWithBusyRetry(drawingPath, ref openErrors, ref openWarnings);
                    openedByUs = drawing != null;
                }
                if (drawing == null)
                    throw new InvalidOperationException("Teknik resim açılamadı. Hata=" + openErrors + " | " + drawingPath);

                wasVisibleBefore = ComUtil.SafeVisible(drawing);

                // Teknik resim üretim boyunca görünmez kalır.
                if (openedByUs || !wasVisibleBefore)
                    TrySetDocumentVisible(drawing, false);

                if (visibleFallback)
                {
                    // Son çare: bazı SaveAs yolları aktif drawing isteyebilir.
                    // Belge aktif edilse bile Visible=false kalır.
                    ActivateDocumentIfNeeded(drawing, drawingPath);
                    if (openedByUs || !wasVisibleBefore)
                        TrySetDocumentVisible(drawing, false);
                    Pace(PdfPacePoint.AfterOpenVisible);
                }
                else
                {
                    // Ana yöntem: ActivateDoc3 yok; doğrudan sessiz SaveAs.
                    Pace(PdfPacePoint.AfterOpenBackground);
                }

                try { drawing.ClearSelection2(true); } catch { }

                int saveErrors = 0, saveWarnings = 0;
                Pace(visibleFallback ? PdfPacePoint.BeforeSaveVisible : PdfPacePoint.BeforeSaveBackground);
                bool ok = SavePdfWithBusyRetry(drawing, tempPdf, ref saveErrors, ref saveWarnings);
                Pace(visibleFallback ? PdfPacePoint.AfterSaveVisible : PdfPacePoint.AfterSaveBackground);

                if (!ok || !OutputFileValidator.IsUsable(tempPdf))
                    throw new InvalidOperationException("PDF export başarısız veya geçersiz çıktı oluştu. Hata=" + saveErrors + " Uyarı=" + saveWarnings);
            }
            finally
            {
                if (drawing != null)
                {
                    try
                    {
                        ComUtil.CloseTransientDocument(_swApp, drawing, drawingPath, openedByUs, wasVisibleBefore);
                    }
                    catch { }
                    Pace(visibleFallback ? PdfPacePoint.AfterCloseVisible : PdfPacePoint.AfterCloseBackground);
                }

                // Üretim scope'unun drawing gizleme davranışını koru.
                if (forcedDrawingVisibility)
                    TrySetDrawingDocumentVisibility(false);

                // Geçici drawing kapandığında önceki belgeye doğal dönüşü kullan.
                if (openedByUs || !wasVisibleBefore) ComUtil.Release(drawing);
                ComUtil.ManagedCleanup(false);
            }
        }

        private void CommitValidatedPdf(string tempPdf, string outputPdf)
        {
            if (!OutputFileValidator.IsUsable(tempPdf))
                throw new InvalidOperationException("Geçici PDF doğrulanamadı.");

            if (!File.Exists(outputPdf))
            {
                File.Move(tempPdf, outputPdf);
                return;
            }

            string backup = outputPdf + ".src_backup";
            try
            {
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Replace(tempPdf, outputPdf, backup, true);
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
            catch
            {
                try
                {
                    if (!File.Exists(backup) && File.Exists(outputPdf)) File.Copy(outputPdf, backup, true);
                    File.Copy(tempPdf, outputPdf, true);
                    File.Delete(tempPdf);
                    if (!OutputFileValidator.IsUsable(outputPdf))
                        throw new InvalidOperationException("PDF son çıktı doğrulanamadı.");
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                catch
                {
                    try { if (File.Exists(backup)) File.Copy(backup, outputPdf, true); } catch { }
                    throw;
                }
            }
        }

        private static string BuildTempPdfPath(string outputPdf, string tag)
        {
            return outputPdf + ".src_" + tag + "_" + Guid.NewGuid().ToString("N") + ".pdf";
        }

        private static void SafeDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private ModelDoc2 GetOpenDocumentWithBusyRetry(string drawingPath)
        {
            COMException last = null;
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                try { return _swApp.GetOpenDocumentByName(drawingPath) as ModelDoc2; }
                catch (COMException ex) when (IsBusyComException(ex))
                {
                    last = ex;
                    BusyDelay("Teknik resim açık belge kontrolü", attempt, ex);
                }
            }
            throw new InvalidOperationException("Teknik resim açık belge kontrolü SOLIDWORKS meşgul olduğu için tamamlanamadı.", last);
        }

        private ModelDoc2 OpenDrawingWithBusyRetry(string drawingPath, ref int openErrors, ref int openWarnings)
        {
            COMException last = null;
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                try
                {
                    using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
                    {
                        var doc = _swApp.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                            "", ref openErrors, ref openWarnings) as ModelDoc2;
                        foregroundGuard.RestoreIfSolidWorksStoleFocus();
                        return doc;
                    }
                }
                catch (COMException ex) when (IsBusyComException(ex))
                {
                    last = ex;
                    BusyDelay("Teknik resim açma", attempt, ex);
                }
            }
            throw new InvalidOperationException("Teknik resim açma SOLIDWORKS meşgul olduğu için tamamlanamadı.", last);
        }

        private bool SavePdfWithBusyRetry(ModelDoc2 drawing, string tempPdf, ref int saveErrors, ref int saveWarnings)
        {
            COMException last = null;
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                object pdfData = null;
                try
                {
                    // PDF'e özel export datası kullanmak SOLIDWORKS'ün PDF görüntüleyicisini açmasını
                    // engeller ve SaveAs çağrısını drawing export yolunda tutar.
                    try
                    {
                        pdfData = ((dynamic)_swApp).GetExportFileData(1); // swExportPdfData
                        if (pdfData != null)
                        {
                            try { ((dynamic)pdfData).ViewPdfAfterSaving = false; } catch { }
                        }
                    }
                    catch { pdfData = null; }

                    return drawing.Extension.SaveAs(tempPdf,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        pdfData, ref saveErrors, ref saveWarnings);
                }
                catch (COMException ex) when (IsBusyComException(ex))
                {
                    last = ex;
                    BusyDelay("PDF kaydetme", attempt, ex);
                }
                finally
                {
                    ComUtil.Release(pdfData);
                }
            }
            throw new InvalidOperationException("PDF kaydetme SOLIDWORKS meşgul olduğu için tamamlanamadı.", last);
        }

        private void ActivateDocumentIfNeeded(ModelDoc2 doc, string path)
        {
            if (doc == null) return;
            ModelDoc2 active = null;
            try { active = _swApp.ActiveDoc as ModelDoc2; } catch { }
            if (SameDocument(active, doc, path)) return;

            string title = "";
            try { title = doc.GetTitle() ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(title)) return;
            int errors = 0;
            ActivateDocumentWithBusyRetry(title, ref errors);
        }

        private static bool SameDocument(ModelDoc2 a, ModelDoc2 b, string expectedPath)
        {
            if (a == null || b == null) return false;
            try
            {
                string ap = a.GetPathName() ?? "";
                string bp = b.GetPathName() ?? "";
                if (!string.IsNullOrWhiteSpace(ap) && !string.IsNullOrWhiteSpace(bp))
                    return string.Equals(Path.GetFullPath(ap), Path.GetFullPath(bp), StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(expectedPath) && !string.IsNullOrWhiteSpace(ap))
                    return string.Equals(Path.GetFullPath(ap), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
                return string.Equals(a.GetTitle() ?? "", b.GetTitle() ?? "", StringComparison.OrdinalIgnoreCase);
            }
            catch { return object.ReferenceEquals(a, b); }
        }

        private void ActivateDocumentWithBusyRetry(string title, ref int errors)
        {
            COMException last = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
                    {
                        _swApp.ActivateDoc3(title, false, 0, ref errors);
                        foregroundGuard.RestoreIfSolidWorksStoleFocus();
                    }
                    return;
                }
                catch (COMException ex) when (IsBusyComException(ex))
                {
                    last = ex;
                    BusyDelay("Belgeyi etkinleştirme", attempt, ex);
                }
            }
            if (last != null) throw last;
        }

        private void TrySetDrawingDocumentVisibility(bool visible)
        {
            try { ((dynamic)_swApp).DocumentVisible(visible, (int)swDocumentTypes_e.swDocDRAWING); } catch { }
        }

        private static void TrySetDocumentVisible(ModelDoc2 doc, bool visible)
        {
            if (doc == null) return;
            try { ((dynamic)doc).Visible = visible; } catch { }
        }

        private static bool IsBusyComException(COMException ex)
        {
            if (ex == null) return false;
            const int rpcCallRejected = unchecked((int)0x80010001);
            const int rpcRetryLater = unchecked((int)0x8001010A);
            return ex.ErrorCode == rpcCallRejected || ex.ErrorCode == rpcRetryLater;
        }

        private void BusyDelay(string operation, int attempt, COMException ex)
        {
            Logger.Info(operation + " sırasında SOLIDWORKS meşgul; tekrar denenecek (" + attempt + "/8, HRESULT=0x" + ex.ErrorCode.ToString("X8") + ", Hız=" + _speed + ").");
            int step = _speed == ProcessingSpeedMode.Safe ? 450 : (_speed == ProcessingSpeedMode.Fast ? 160 : 260);
            int cap = _speed == ProcessingSpeedMode.Safe ? 2500 : (_speed == ProcessingSpeedMode.Fast ? 1200 : 1600);
            Thread.Sleep(Math.Min(cap, step * attempt));
        }

        private enum PdfPacePoint
        {
            AfterOpenBackground,
            BeforeSaveBackground,
            AfterSaveBackground,
            AfterCloseBackground,
            AfterOpenVisible,
            BeforeSaveVisible,
            AfterSaveVisible,
            AfterCloseVisible
        }

        private void Pace(PdfPacePoint point)
        {
            int delay = 0;

            if (_speed == ProcessingSpeedMode.Fast)
            {
                switch (point)
                {
                    case PdfPacePoint.AfterOpenBackground: delay = 40; break;
                    case PdfPacePoint.BeforeSaveBackground: delay = 20; break;
                    case PdfPacePoint.AfterSaveBackground: delay = 40; break;
                    case PdfPacePoint.AfterCloseBackground: delay = 20; break;
                    case PdfPacePoint.AfterOpenVisible: delay = 120; break;
                    case PdfPacePoint.BeforeSaveVisible: delay = 80; break;
                    case PdfPacePoint.AfterSaveVisible: delay = 120; break;
                    case PdfPacePoint.AfterCloseVisible: delay = 80; break;
                }
            }
            else if (_speed == ProcessingSpeedMode.Normal)
            {
                switch (point)
                {
                    case PdfPacePoint.AfterOpenBackground: delay = 120; break;
                    case PdfPacePoint.BeforeSaveBackground: delay = 80; break;
                    case PdfPacePoint.AfterSaveBackground: delay = 120; break;
                    case PdfPacePoint.AfterCloseBackground: delay = 80; break;
                    case PdfPacePoint.AfterOpenVisible: delay = 300; break;
                    case PdfPacePoint.BeforeSaveVisible: delay = 220; break;
                    case PdfPacePoint.AfterSaveVisible: delay = 320; break;
                    case PdfPacePoint.AfterCloseVisible: delay = 220; break;
                }
            }
            else
            {
                switch (point)
                {
                    case PdfPacePoint.AfterOpenBackground: delay = 350; break;
                    case PdfPacePoint.BeforeSaveBackground: delay = 250; break;
                    case PdfPacePoint.AfterSaveBackground: delay = 400; break;
                    case PdfPacePoint.AfterCloseBackground: delay = 300; break;
                    case PdfPacePoint.AfterOpenVisible: delay = 700; break;
                    case PdfPacePoint.BeforeSaveVisible: delay = 450; break;
                    case PdfPacePoint.AfterSaveVisible: delay = 750; break;
                    case PdfPacePoint.AfterCloseVisible: delay = 550; break;
                }
            }

            if (delay > 0) ComUtil.PumpMessages(delay);
        }
    }
}
