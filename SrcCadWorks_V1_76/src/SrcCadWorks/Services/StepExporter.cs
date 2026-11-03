using System;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// Seçili satır için önce teknik resmin gerçek model referansını çözer.
    /// Ardından yalnız o modeli açar ve Farklı Kaydet -> STEP AP214 uygular.
    /// Seçili olmayan satır için bu sınıf hiç çağrılmaz.
    /// </summary>
    internal sealed class StepExporter
    {
        private readonly SldWorks _swApp;
        public StepExporter(SldWorks swApp) { _swApp = swApp; }

        public void Export(string sourcePath, string configuration, string drawingPath, string expectedPartCode, string outputStep)
        {
            if (string.IsNullOrWhiteSpace(outputStep)) throw new ArgumentException("STEP hedefi boş.");

            string resolvedSource = ResolveSourcePath(sourcePath, drawingPath, expectedPartCode);
            if (string.IsNullOrWhiteSpace(resolvedSource) || !File.Exists(resolvedSource))
                throw new FileNotFoundException("STEP için doğru kaynak parça bulunamadı. Kod=" + (expectedPartCode ?? ""), resolvedSource ?? sourcePath);

            string outputDir = Path.GetDirectoryName(outputStep);
            Directory.CreateDirectory(string.IsNullOrWhiteSpace(outputDir) ? System.Environment.CurrentDirectory : outputDir);

            string workDir = Path.Combine(Path.GetTempPath(), "SrcCadWorks", "step_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            string tempStep = Path.Combine(workDir, "selected.STEP");

            ModelDoc2 model = null;
            bool openedByUs = false;
            bool wasVisible = false;
            string originalConfig = "";
            int oldStepAp = 214;
            int oldStepGeometry = 0;
            bool prefsRead = false;
            int? separateComponentsPreference = null;
            bool oldSeparateComponents = false;
            bool separatePrefRead = false;

            try
            {
                model = GetOrOpen(resolvedSource, configuration, out openedByUs);
                if (model == null) throw new InvalidOperationException("STEP için model açılamadı: " + resolvedSource);

                wasVisible = ComUtil.SafeVisible(model);
                try { originalConfig = model.ConfigurationManager.ActiveConfiguration.Name ?? ""; } catch { }
                ActivateConfiguration(model, configuration);

                try
                {
                    oldStepAp = _swApp.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP);
                    oldStepGeometry = _swApp.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepExportPreference);
                    prefsRead = true;
                }
                catch { }

                try { _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, 214); } catch { }
                try { _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepExportPreference, (int)swAcisOutputGeometryPreference_e.swAcisOutputAsSolidAndSurface); } catch { }

                // SW2023+ sürümlerinde yeni STEP atomic/separate-components seçeneğinin enum adı
                // sürüme göre değişebildiğinden sabit bir enum üyesine compile-time bağımlılık kurmuyoruz.
                // Runtime enum adlarından STEP + component/separate/atomic eşleşmesini bulup kapatıyoruz.
                if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    separateComponentsPreference = FindSeparateStepComponentsPreference();
                    if (separateComponentsPreference.HasValue)
                    {
                        try
                        {
                            oldSeparateComponents = _swApp.GetUserPreferenceToggle(separateComponentsPreference.Value);
                            separatePrefRead = true;
                            _swApp.SetUserPreferenceToggle(separateComponentsPreference.Value, false);
                        }
                        catch { separatePrefRead = false; }
                    }
                }

                // Farklı Kaydet çağrısından önce doğru modelin gerçekten aktif belge olduğunu doğrula.
                // Bazı SOLIDWORKS oturumlarında arka planda açık ana montaj aktif kalırsa, STEP adı
                // seçilen parçaya ait olsa bile geometri yanlış belgeden çıkabiliyordu.
                int activateError = 0;
                try { _swApp.ActivateDoc3(model.GetTitle(), false, 0, ref activateError); }
                catch (Exception ex) { throw new InvalidOperationException("STEP kaynak belge etkinleştirilemedi: " + resolvedSource + " | " + ex.Message); }

                ModelDoc2 activeForStep = _swApp.ActiveDoc as ModelDoc2;
                string activePath = activeForStep == null ? "" : (activeForStep.GetPathName() ?? "");
                if (!string.Equals(NormalizePath(activePath), NormalizePath(resolvedSource), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("STEP için yanlış belge aktif. Beklenen=" + resolvedSource + " | Aktif=" + activePath);

                // Kullanıcının istediği sade yol: doğrulanmış aktif modeli doğrudan STEP olarak Farklı Kaydet.
                SaveModelAsStep(model, tempStep);
                if (!OutputFileValidator.IsUsable(tempStep))
                    throw new InvalidOperationException("STEP export tamamlandı ancak ana STEP dosyası doğrulanamadı: " + Path.GetFileName(outputStep));

                if (File.Exists(outputStep)) File.Delete(outputStep);
                File.Copy(tempStep, outputStep, true);
                if (!OutputFileValidator.IsUsable(outputStep))
                    throw new InvalidOperationException("STEP hedef dosyası doğrulanamadı: " + outputStep);

                // Temp exportta yan STEP dosyaları oluşmuşsa hedefe taşımıyoruz. Bu durum loga yazılır;
                // kullanıcının çıktı klasöründe yalnız seçili satırın tek dosyası kalır.
                try
                {
                    int extraCount = Directory.GetFiles(workDir, "*.STEP", SearchOption.TopDirectoryOnly)
                        .Count(f => !string.Equals(f, tempStep, StringComparison.OrdinalIgnoreCase));
                    if (extraCount > 0)
                        Logger.Info("STEP export sırasında " + extraCount + " yan bileşen STEP'i temp klasörde üretildi ve çıktı klasörüne alınmadı: " + resolvedSource);
                }
                catch { }
            }
            finally
            {
                if (separatePrefRead && separateComponentsPreference.HasValue)
                {
                    try { _swApp.SetUserPreferenceToggle(separateComponentsPreference.Value, oldSeparateComponents); } catch { }
                }
                if (prefsRead)
                {
                    try { _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, oldStepAp); } catch { }
                    try { _swApp.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepExportPreference, oldStepGeometry); } catch { }
                }

                if (model != null)
                {
                    try { if (!string.IsNullOrWhiteSpace(originalConfig)) model.ShowConfiguration2(originalConfig); } catch { }
                    try { ComUtil.CloseTransientDocument(_swApp, model, resolvedSource, openedByUs, wasVisible); } catch { }
                    if (openedByUs || !wasVisible) ComUtil.Release(model);
                }

                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.Trim(); }
        }

        private string ResolveSourcePath(string sourcePath, string drawingPath, string expectedPartCode)
        {
            // En güvenilir kaynak: PDF'i üreten SLDDRW'nin gerçek model referansı.
            // Böylece montaj taramasındaki SourcePath yanlış/üst montaj olsa bile STEP,
            // teknik resimde görülen gerçek parçadan üretilir.
            string drawingReferenced = ResolveReferencedModelFromDrawing(drawingPath, expectedPartCode);
            if (!string.IsNullOrWhiteSpace(drawingReferenced) && File.Exists(drawingReferenced))
                return drawingReferenced;

            var candidates = new System.Collections.Generic.List<string>();
            Action<string> add = p =>
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try { p = Path.GetFullPath(p); } catch { }
                if (!candidates.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) candidates.Add(p);
            };

            string code = (expectedPartCode ?? "").Trim();
            string drawingDir = "";
            string sourceDir = "";
            try { drawingDir = Path.GetDirectoryName(drawingPath) ?? ""; } catch { }
            try { sourceDir = Path.GetDirectoryName(sourcePath) ?? ""; } catch { }

            // En güvenilir aday: teknik resim ile aynı isimdeki SLDPRT.
            if (!string.IsNullOrWhiteSpace(drawingPath))
            {
                try { add(Path.ChangeExtension(drawingPath, ".SLDPRT")); } catch { }
            }
            if (!string.IsNullOrWhiteSpace(code))
            {
                if (!string.IsNullOrWhiteSpace(drawingDir)) add(Path.Combine(drawingDir, code + ".SLDPRT"));
                if (!string.IsNullOrWhiteSpace(sourceDir)) add(Path.Combine(sourceDir, code + ".SLDPRT"));
            }

            // Taramanın verdiği kaynak gerçekten bir parça ise onu da aday yap.
            if (!string.IsNullOrWhiteSpace(sourcePath) &&
                string.Equals(Path.GetExtension(sourcePath), ".SLDPRT", StringComparison.OrdinalIgnoreCase))
                add(sourcePath);

            // Aynı klasörde parça koduyla başlayan SLDPRT varsa kullan.
            foreach (string dir in new[] { drawingDir, sourceDir }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir) || string.IsNullOrWhiteSpace(code)) continue;
                    foreach (string file in Directory.GetFiles(dir, code + "*.SLDPRT", SearchOption.TopDirectoryOnly)) add(file);
                }
                catch { }
            }

            string exact = candidates.FirstOrDefault(p => File.Exists(p) &&
                string.Equals(Path.GetExtension(p), ".SLDPRT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(p), code, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact)) return exact;

            string anyPart = candidates.FirstOrDefault(p => File.Exists(p) &&
                string.Equals(Path.GetExtension(p), ".SLDPRT", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(anyPart)) return anyPart;

            // Gerçekten bir montaj satırı seçildiyse montajı son çare olarak kullan.
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath) &&
                string.Equals(Path.GetExtension(sourcePath), ".SLDASM", StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            return candidates.FirstOrDefault(File.Exists) ?? sourcePath;
        }

        private string ResolveReferencedModelFromDrawing(string drawingPath, string expectedPartCode)
        {
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
                return "";

            ModelDoc2 drawingModel = null;
            bool openedByUs = false;
            bool wasVisible = false;
            try
            {
                try { drawingModel = _swApp.GetOpenDocumentByName(drawingPath) as ModelDoc2; } catch { }
                if (drawingModel == null)
                {
                    int errors = 0, warnings = 0;
                    drawingModel = _swApp.OpenDoc6(
                        drawingPath,
                        (int)swDocumentTypes_e.swDocDRAWING,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = drawingModel != null;
                }
                if (drawingModel == null) return "";

                wasVisible = ComUtil.SafeVisible(drawingModel);
                var drawing = drawingModel as DrawingDoc;
                if (drawing == null) return "";

                var refs = new System.Collections.Generic.List<string>();
                View view = null;
                try { view = drawing.GetFirstView() as View; } catch { }
                try { if (view != null) view = view.GetNextView() as View; } catch { view = null; }

                while (view != null)
                {
                    string modelName = "";
                    try { modelName = view.GetReferencedModelName() ?? ""; } catch { }

                    if (!string.IsNullOrWhiteSpace(modelName))
                    {
                        string candidate = modelName;
                        if (!Path.IsPathRooted(candidate))
                        {
                            try { candidate = Path.Combine(Path.GetDirectoryName(drawingPath) ?? "", candidate); } catch { }
                        }
                        try { candidate = Path.GetFullPath(candidate); } catch { }
                        if (File.Exists(candidate) && !refs.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase)))
                            refs.Add(candidate);
                    }

                    try
                    {
                        ModelDoc2 referenced = view.ReferencedDocument as ModelDoc2;
                        if (referenced != null)
                        {
                            string candidate = referenced.GetPathName() ?? "";
                            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate) &&
                                !refs.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase)))
                                refs.Add(candidate);
                        }
                    }
                    catch { }

                    try { view = view.GetNextView() as View; } catch { view = null; }
                }

                string code = (expectedPartCode ?? "").Trim();
                string exact = refs.FirstOrDefault(p =>
                    string.Equals(Path.GetFileNameWithoutExtension(p), code, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(exact))
                {
                    Logger.Info("STEP kaynak modeli teknik resimden çözüldü: " + code + " -> " + exact);
                    return exact;
                }

                string firstPart = refs.FirstOrDefault(p =>
                    string.Equals(Path.GetExtension(p), ".SLDPRT", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(firstPart))
                {
                    Logger.Info("STEP kaynak modeli teknik resim görünüşünden alındı: " + code + " -> " + firstPart);
                    return firstPart;
                }

                string first = refs.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    Logger.Info("STEP kaynak modeli teknik resim referansından alındı: " + code + " -> " + first);
                    return first;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("STEP teknik resim referansı okunamadı: " + ex.Message);
            }
            finally
            {
                if (drawingModel != null)
                {
                    try { ComUtil.CloseTransientDocument(_swApp, drawingModel, drawingPath, openedByUs, wasVisible); } catch { }
                    if (openedByUs || !wasVisible) ComUtil.Release(drawingModel);
                }
            }
            return "";
        }

        private int? FindSeparateStepComponentsPreference()
        {
            try
            {
                string[] names = Enum.GetNames(typeof(swUserPreferenceToggle_e));
                // Önce en olası açık isimler.
                string[] preferred =
                {
                    "swStepExportSeparateComponents",
                    "swStepExportAssemblyComponentsAsSeparateFiles",
                    "swStepExportComponentsAsSeparateFiles",
                    "swStepExportAtomicAssembly"
                };
                foreach (string candidate in preferred)
                {
                    string actual = names.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(actual))
                        return Convert.ToInt32(Enum.Parse(typeof(swUserPreferenceToggle_e), actual));
                }

                foreach (string name in names)
                {
                    string upper = (name ?? "").ToUpperInvariant();
                    if (!upper.Contains("STEP")) continue;
                    if (!(upper.Contains("SEPARAT") || upper.Contains("ATOMIC") || (upper.Contains("COMPONENT") && upper.Contains("EXPORT")))) continue;
                    try { return Convert.ToInt32(Enum.Parse(typeof(swUserPreferenceToggle_e), name)); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static void SaveModelAsStep(ModelDoc2 model, string stepPath)
        {
            int saveErrors = 0, saveWarnings = 0;
            bool ok = model.Extension.SaveAs(stepPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null, ref saveErrors, ref saveWarnings);
            if (!ok || !OutputFileValidator.IsUsable(stepPath))
                throw new InvalidOperationException("STEP export başarısız. Hata=" + saveErrors + " Uyarı=" + saveWarnings);
        }

        private ModelDoc2 GetOrOpen(string sourcePath, string configuration, out bool openedByUs)
        {
            openedByUs = false;
            ModelDoc2 model = null;
            try { model = _swApp.GetOpenDocumentByName(sourcePath) as ModelDoc2; } catch { }
            if (model != null) return model;

            int errors = 0, warnings = 0;
            model = _swApp.OpenDoc6(sourcePath, DocumentType(sourcePath),
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                configuration ?? "", ref errors, ref warnings) as ModelDoc2;
            openedByUs = model != null;
            if (model == null)
                throw new InvalidOperationException("STEP için kaynak açılamadı. Hata=" + errors + " Uyarı=" + warnings + " | " + sourcePath);
            return model;
        }

        private static void ActivateConfiguration(ModelDoc2 model, string configuration)
        {
            if (model == null || string.IsNullOrWhiteSpace(configuration)) return;
            try { model.ShowConfiguration2(configuration); } catch { }
        }

        private static int DocumentType(string path)
        {
            string ext = Path.GetExtension(path) ?? "";
            if (ext.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocASSEMBLY;
            return (int)swDocumentTypes_e.swDocPART;
        }
    }
}
