using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.Services
{
    internal sealed class DwgConverter
    {
        private readonly SldWorks _swApp;
        public DwgConverter(SldWorks swApp) => _swApp = swApp;

        public void Convert(string dxfPath, string dwgPath)
        {
            ConvertOutputs(dxfPath, null, dwgPath, false, true);
        }

        /// <summary>
        /// Imports the SrcCadWorks staging DXF into SOLIDWORKS once, then lets SOLIDWORKS
        /// re-save the final TopluDXF and/or TopluDWG. The hand-written R12 file is therefore
        /// only an internal interchange file; the user's final DXF is a SOLIDWORKS-authored DXF.
        /// </summary>
        public void ConvertOutputs(string sourceDxfPath, string finalDxfPath, string finalDwgPath, bool exportDxf, bool exportDwg)
        {
            if (!exportDxf && !exportDwg)
                throw new InvalidOperationException("Toplu DXF/DWG için en az bir çıktı seçilmelidir.");
            if (string.IsNullOrWhiteSpace(sourceDxfPath) || !File.Exists(sourceDxfPath))
                throw new FileNotFoundException("Toplu DXF kaynak dosyası bulunamadı.", sourceDxfPath);
            if (exportDxf && string.IsNullOrWhiteSpace(finalDxfPath))
                throw new ArgumentException("Toplu DXF çıktı yolu boş.", nameof(finalDxfPath));
            if (exportDwg && string.IsNullOrWhiteSpace(finalDwgPath))
                throw new ArgumentException("Toplu DWG çıktı yolu boş.", nameof(finalDwgPath));

            CombinedDxfBuilder.ValidateForImport(sourceDxfPath);

            if (exportDxf)
            {
                string dir = Path.GetDirectoryName(finalDxfPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }
            if (exportDwg)
            {
                string dir = Path.GetDirectoryName(finalDwgPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }

            bool oldDontShowMap = false;
            bool prefCaptured = false;
            int oldMultiSheetOption = 0;
            bool multiSheetCaptured = false;
            bool oldPaperSpace = false;
            bool paperSpaceCaptured = false;
            string stageDir = null;
            var attempts = new List<string>();

            using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
            try
            {
                try
                {
                    oldDontShowMap = _swApp.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap);
                    prefCaptured = true;
                    _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap, true);
                }
                catch { }
                try
                {
                    oldMultiSheetOption = _swApp.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swDxfMultiSheetOption);
                    multiSheetCaptured = true;
                    _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swDxfMultiSheetOption, 0);
                }
                catch { }
                try
                {
                    oldPaperSpace = _swApp.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDxfExportAllSheetsToPaperSpace);
                    paperSpaceCaptured = true;
                    _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDxfExportAllSheetsToPaperSpace, false);
                }
                catch { }

                stageDir = Path.Combine(Path.GetTempPath(), "SrcCadWorks", "CombinedConvert_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageDir);
                string stagedInput = Path.Combine(stageDir, "input.dxf");
                string stagedDxf = Path.Combine(stageDir, "TopluDXF.dxf");
                string stagedDwg = Path.Combine(stageDir, "TopluDWG.dwg");
                File.Copy(sourceDxfPath, stagedInput, true);
                CombinedDxfBuilder.ValidateForImport(stagedInput);

                bool converted = false;
                string detail;
                if (TryConvertOnce(stagedInput, stagedDxf, stagedDwg, exportDxf, exportDwg, true, "", false, out detail))
                {
                    attempts.Add("A1 başarılı: " + detail);
                    converted = true;
                }
                else attempts.Add("A1: " + detail);

                if (!converted)
                {
                    if (TryConvertOnce(stagedInput, stagedDxf, stagedDwg, exportDxf, exportDwg, false, "", false, out detail))
                    {
                        attempts.Add("A2 başarılı: " + detail);
                        converted = true;
                    }
                    else attempts.Add("A2: " + detail);
                }

                if (!converted)
                {
                    if (TryConvertOnce(stagedInput, stagedDxf, stagedDwg, exportDxf, exportDwg, true, "r", true, out detail))
                    {
                        attempts.Add("A3 başarılı: " + detail);
                        converted = true;
                    }
                    else attempts.Add("A3: " + detail);
                }

                if (!converted)
                    throw new InvalidOperationException("Toplu DXF/DWG dönüşümü başarısız. " + string.Join(" | ", attempts));

                if (exportDxf)
                {
                    if (!File.Exists(stagedDxf) || new FileInfo(stagedDxf).Length < 128)
                        throw new InvalidOperationException("SOLIDWORKS TopluDXF.dxf oluşturmadı.");

                    // Critical design rule:
                    // keep the SOLIDWORKS-authored DXF structure and touch ONLY colors.
                    DxfColorNormalizer.NormalizeProductionColors(stagedDxf);
                    DxfColorNormalizer.ValidateNativeDxf(stagedDxf);

                    if (File.Exists(finalDxfPath)) File.Delete(finalDxfPath);
                    File.Copy(stagedDxf, finalDxfPath, true);
                    DxfColorNormalizer.ValidateNativeDxf(finalDxfPath);
                }

                if (exportDwg)
                {
                    if (!File.Exists(stagedDwg) || new FileInfo(stagedDwg).Length < 32)
                        throw new InvalidOperationException("SOLIDWORKS TopluDWG.dwg oluşturmadı.");
                    if (File.Exists(finalDwgPath)) File.Delete(finalDwgPath);
                    File.Copy(stagedDwg, finalDwgPath, true);
                    if (!File.Exists(finalDwgPath) || new FileInfo(finalDwgPath).Length < 32)
                        throw new InvalidOperationException("TopluDWG.dwg hedef klasöre kopyalanamadı.");
                }

                Logger.Info("Toplu DXF/DWG SOLIDWORKS tarafından oluşturuldu; DXF yalnız renk kodları sonradan normalize edildi. " + string.Join(" | ", attempts));
            }
            finally
            {
                if (prefCaptured)
                {
                    try { _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap, oldDontShowMap); } catch { }
                }
                if (multiSheetCaptured)
                {
                    try { _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swDxfMultiSheetOption, oldMultiSheetOption); } catch { }
                }
                if (paperSpaceCaptured)
                {
                    try { _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDxfExportAllSheetsToPaperSpace, oldPaperSpace); } catch { }
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(stageDir) && Directory.Exists(stageDir))
                        Directory.Delete(stageDir, true);
                }
                catch { }
                ComUtil.ManagedCleanup(false);
            }
        }

        private bool TryConvertOnce(string dxfPath, string normalizedDxfPath, string dwgPath,
            bool exportDxf, bool exportDwg, bool setAllImportOptions, string argString,
            bool activateImportedFallback, out string detail)
        {
            ModelDoc2 imported = null;
            ImportDxfDwgData importData = null;
            int loadErrors = 0;
            try
            {
                try { if (exportDxf && File.Exists(normalizedDxfPath)) File.Delete(normalizedDxfPath); } catch { }
                try { if (exportDwg && File.Exists(dwgPath)) File.Delete(dwgPath); } catch { }

                importData = _swApp.GetImportFileData(dxfPath) as ImportDxfDwgData;
                if (importData == null)
                {
                    detail = "SOLIDWORKS DXF/DWG import ayar nesnesi vermedi.";
                    return false;
                }

                importData.set_ImportMethod("", (int)swImportDxfDwg_ImportMethod_e.swImportDxfDwg_ImportToDrawing);
                if (setAllImportOptions)
                {
                    try { importData.set_LengthUnit("", (int)swLengthUnit_e.swMM); } catch { }
                    try { importData.SetSheetScale("", 1.0, 1.0); } catch { }
                    try { importData.SetPosition("", (int)swDwgImportEntitiesPositioning_e.swDwgEntitiesCentered, 0.0, 0.0); } catch { }
                }

                using (var loadFocus = ForegroundWindowGuard.CaptureExternal())
                {
                    imported = _swApp.LoadFile4(dxfPath, argString ?? "", importData, ref loadErrors) as ModelDoc2;
                    loadFocus.RestoreIfSolidWorksStoleFocus();
                }
                if (imported == null)
                {
                    detail = "LoadFile4 belge oluşturmadı. Hata=" + loadErrors + ", Arg='" + (argString ?? "") + "'.";
                    return false;
                }

                int docType = 0;
                try { docType = imported.GetType(); } catch { }
                if (docType != (int)swDocumentTypes_e.swDocDRAWING)
                {
                    detail = "DXF drawing yerine belge türü " + docType + " olarak açıldı. Hata=" + loadErrors + ".";
                    return false;
                }

                // Prefer hidden/no-activation conversion. The third and final attempt may
                // activate the imported drawing because older SOLIDWORKS builds sometimes
                // require an active drawing for DXF/DWG SaveAs. This happens once for the
                // combined file, not once per laser part.
                try { ((dynamic)imported).Visible = false; } catch { }

                // INTENTIONAL SINGLE FALLBACK: This is the only ActivateDoc3 allowed in
                // DwgConverter. It runs at most once for the final combined drawing, never
                // once per laser part. The document remains Visible=false before/after it.
                if (activateImportedFallback)
                {
                    try
                    {
                        int activateError = 0;
                        _swApp.ActivateDoc3(imported.GetTitle(), false, 0, ref activateError);
                        try { ((dynamic)imported).Visible = false; } catch { }
                    }
                    catch { }
                }

                try { imported.ClearSelection2(true); } catch { }
                try { imported.ForceRebuild3(false); } catch { }
                try { _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swDxfMultiSheetOption, 0); } catch { }
                try { _swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDxfExportAllSheetsToPaperSpace, false); } catch { }
                ComUtil.PumpMessages(25);

                var saveDetails = new List<string>();
                if (exportDwg)
                {
                    string saveDetail;
                    if (!SaveOutput(imported, dwgPath, out saveDetail))
                    {
                        detail = "DWG kaydedilemedi: " + saveDetail + " | Load=" + loadErrors;
                        return false;
                    }
                    saveDetails.Add("DWG " + saveDetail);
                }

                if (exportDxf)
                {
                    string saveDetail;
                    if (!SaveOutput(imported, normalizedDxfPath, out saveDetail))
                    {
                        detail = "DXF yeniden kaydedilemedi: " + saveDetail + " | Load=" + loadErrors;
                        return false;
                    }
                    saveDetails.Add("DXF " + saveDetail);
                }

                detail = "Load=" + loadErrors + " | " + string.Join(" | ", saveDetails);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message + " (Load=" + loadErrors + ")";
                return false;
            }
            finally
            {
                if (imported != null)
                {
                    try { _swApp.CloseDoc(imported.GetTitle()); } catch { }
                    ComUtil.Release(imported);
                }
                ComUtil.Release(importData);
                ComUtil.PumpMessages(15);
            }
        }

        private static bool SaveOutput(ModelDoc2 imported, string path, out string detail)
        {
            int extErrors = 0, extWarnings = 0;
            bool ok = false;
            try
            {
                ok = imported.Extension.SaveAs(path,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, ref extErrors, ref extWarnings);
            }
            catch (Exception ex)
            {
                detail = "Extension.SaveAs istisna: " + ex.Message;
                ok = false;
            }

            if (ok && File.Exists(path) && new FileInfo(path).Length >= 32)
            {
                detail = "Extension.SaveAs " + extErrors + "/" + extWarnings;
                return true;
            }

            try { if (File.Exists(path)) File.Delete(path); } catch { }
            int saveErrors = 0, saveWarnings = 0;
            bool legacyOk = false;
            try
            {
                legacyOk = imported.SaveAs4(path,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    ref saveErrors, ref saveWarnings);
            }
            catch (Exception ex)
            {
                detail = "SaveAs4 istisna: " + ex.Message + " | Extension=" + extErrors + "/" + extWarnings;
                return false;
            }

            if (legacyOk && File.Exists(path) && new FileInfo(path).Length >= 32)
            {
                detail = "SaveAs4 " + saveErrors + "/" + saveWarnings;
                return true;
            }

            detail = "Extension=" + extErrors + "/" + extWarnings + ", SaveAs4=" + saveErrors + "/" + saveWarnings;
            return false;
        }
    }
}
