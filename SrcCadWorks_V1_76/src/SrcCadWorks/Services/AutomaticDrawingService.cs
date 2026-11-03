using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.Services
{
    internal sealed class AutomaticDrawingOptions
    {
        public string DrawingTemplatePath { get; set; } = "";
        public bool ImportModelAnnotations { get; set; } = true;
        public bool AutoDimensionFallback { get; set; } = true;
    }

    internal sealed class AutomaticDrawingResult
    {
        public bool Created { get; set; }
        public bool ExistingDrawing { get; set; }
        public bool IsSheetMetal { get; set; }
        public bool FirstAngleProjection { get; set; }
        public bool BendOrientationProtected { get; set; }
        public string DrawingPath { get; set; } = "";
        public string Message { get; set; } = "";
        public int ViewCount { get; set; }
        public int ImportedAnnotationCount { get; set; }
        public int AutoDimensionedViewCount { get; set; }
    }

    /// <summary>
    /// V1.46 - SLDPRT otomatik teknik resim motoru (DERLEME_FIX2).
    /// Sabit bir örnek sayfayı kopyalamaz; görünüş sınırlarına ve sayfa ölçüsüne göre dinamik ölçek kullanır.
    /// Ana sıra: Ön (sol üst) -> Yan (sağ) -> Üst (önün altında) -> Flat Pattern -> İzometrik.
    /// Ortografik görünüşler SOLIDWORKS Create1stAngleViews2 ile ilk-açı olarak üretilir.
    /// Sac açınımında FlipView=false korunur; native büküm notları kullanılır. Böylece UP/DOWN yönleri
    /// kullanıcı yerleşimi için ters çevrilmez veya aynalanmaz.
    /// Mevcut aynı isimli SLDDRW hiçbir koşulda üzerine yazılmaz.
    /// </summary>
    internal sealed class AutomaticDrawingService
    {
        private readonly SldWorks _swApp;

        // swInsertAnnotation_e bitmask. Sayısal sabitler eski/new API sürümlerinde aynı kalmıştır.
        private const int InsertTypes =
            1 |        // cosmetic threads
            32 |       // GTol
            128 |      // surface finish
            32768 |    // dimensions marked for drawing
            65536 |    // Hole Wizard profile dimensions
            131072 |   // Hole Wizard location dimensions
            1048576 |  // hole callout
            16777216;  // toleranced dimensions

        public AutomaticDrawingService(SldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        public AutomaticDrawingResult CreateForActivePart(AutomaticDrawingOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var part = _swApp.ActiveDoc as ModelDoc2;
            if (part == null || part.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new InvalidOperationException("Otomatik Teknik Resim için aktif bir SLDPRT açın.");

            string partPath = part.GetPathName() ?? "";
            if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                throw new InvalidOperationException("Parça önce diske kaydedilmelidir. Adsız/kaydedilmemiş parçadan teknik resim oluşturulmaz.");

            string template = (options.DrawingTemplatePath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
                throw new FileNotFoundException("Teknik resim şablonu bulunamadı. Geçerli bir .DRWDOT şablonu seçin.", template);
            if (!string.Equals(Path.GetExtension(template), ".drwdot", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Yeni teknik resim oluşturmak için .DRWDOT uzantılı SOLIDWORKS teknik resim şablonu seçin.");

            string drawingPath = Path.ChangeExtension(partPath, ".SLDDRW");
            if (File.Exists(drawingPath))
            {
                return new AutomaticDrawingResult
                {
                    ExistingDrawing = true,
                    DrawingPath = drawingPath,
                    Message = "Bu parçanın teknik resmi zaten var. Mevcut SLDDRW korunarak yeni dosya oluşturulmadı."
                };
            }

            string configName = "";
            try { configName = part.ConfigurationManager?.ActiveConfiguration?.Name ?? ""; } catch { }
            bool isSheetMetal = IsSheetMetal(part);

            ModelDoc2 drawingModel = null;
            DrawingDoc drawing = null;
            bool saved = false;
            string createdTitle = "";
            try
            {
                drawingModel = _swApp.NewDocument(template, 0, 0.0, 0.0) as ModelDoc2;
                if (drawingModel == null || drawingModel.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                    throw new InvalidOperationException("Seçilen şablondan SOLIDWORKS teknik resim belgesi oluşturulamadı.");

                createdTitle = drawingModel.GetTitle() ?? "";
                drawing = drawingModel as DrawingDoc;
                if (drawing == null) throw new InvalidOperationException("Yeni belge DrawingDoc olarak açılamadı.");

                // Şablondaki diğer sayfa ayarlarını koruyup yansıtma tipini açıkça ilk açı yap.
                ForceFirstAngleProjection(drawing);

                double sheetWidth, sheetHeight;
                GetSheetSize(drawing, out sheetWidth, out sheetHeight);
                if (sheetWidth <= 0 || sheetHeight <= 0)
                {
                    // A3 yatay yaklaşık değerleri. Normalde şablon ölçüsü okunur; bu yalnız güvenli fallback.
                    sheetWidth = 0.420;
                    sheetHeight = 0.297;
                }

                var views = CreateRequiredViews(drawing, part, partPath, configName, isSheetMetal, sheetWidth, sheetHeight);
                drawingModel.ForceRebuild3(false);

                var titleBlock = GetTitleBlockBounds(drawing, sheetWidth, sheetHeight);
                LayoutViewsDynamically(views, isSheetMetal, sheetWidth, sheetHeight, titleBlock);
                drawingModel.ForceRebuild3(false);

                ValidateRequiredViews(views, isSheetMetal);
                ValidatePlacement(views, sheetWidth, sheetHeight, titleBlock);
                SetSheetCustomPropertyView(drawing, views);

                // V1.12.71'de aynı ölçü grubunun farklı görünüşlere yığılması okunabilirliği bozuyordu.
                // V1.46'de işaretli model ölçüleri sadece ana/ön görünüşe aktarılır. Flat Pattern'da
                // SOLIDWORKS native büküm notları korunur; izometrik görünüş ölçülendirilmez.
                int imported = 0;
                int autoDimmed = 0;
                var front = views.FirstOrDefault(v => v.Role == DrawingViewRole.Front);
                if (front != null && (options.ImportModelAnnotations || options.AutoDimensionFallback))
                {
                    if (options.ImportModelAnnotations)
                        imported = ImportModelAnnotations(drawingModel, drawing, front.View);

                    if (options.AutoDimensionFallback && imported == 0)
                    {
                        if (AutoDimensionView(drawingModel, drawing, front.View)) autoDimmed = 1;
                    }
                }

                drawingModel.ClearSelection2(true);
                drawingModel.ForceRebuild3(false);
                ValidateRequiredViews(views, isSheetMetal);
                ValidatePlacement(views, sheetWidth, sheetHeight, titleBlock);

                int saveErrors = 0, saveWarnings = 0;
                bool ok = drawingModel.Extension.SaveAs(
                    drawingPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, ref saveErrors, ref saveWarnings);
                if (!ok || saveErrors != 0 || !File.Exists(drawingPath))
                    throw new InvalidOperationException("Teknik resim kaydedilemedi. SOLIDWORKS hata=" + saveErrors + ", uyarı=" + saveWarnings);

                saved = true;
                Logger.Info("Otomatik teknik resim V1.46 oluşturuldu: " + drawingPath +
                            " | İlkAçı=True | Sac=" + isSheetMetal + " | Görünüş=" + views.Count +
                            " | Aktarılan anotasyon=" + imported + " | AutoDim=" + autoDimmed +
                            (isSheetMetal ? " | FlatFlip=False | BendNotes=True" : ""));

                return new AutomaticDrawingResult
                {
                    Created = true,
                    IsSheetMetal = isSheetMetal,
                    FirstAngleProjection = true,
                    BendOrientationProtected = isSheetMetal,
                    DrawingPath = drawingPath,
                    ViewCount = views.Count,
                    ImportedAnnotationCount = imported,
                    AutoDimensionedViewCount = autoDimmed,
                    Message = isSheetMetal
                        ? "Teknik resim oluşturuldu. İlk açı kullanıldı; Ön/yan/üst + Flat Pattern + izometrik görünüşler doğrulandı. Flat Pattern çevrilmedi (Flip kapalı) ve SOLIDWORKS büküm notları korundu."
                        : "Teknik resim oluşturuldu. İlk açı kullanıldı; Ön/yan/üst + izometrik görünüşler doğrulandı."
                };
            }
            catch
            {
                // Yalnız bu metot tarafından açılan ve kaydedilemeyen yeni drawing kapanır.
                // Aktif SLDPRT ve kullanıcıdaki diğer belgeler asla kapatılmaz.
                if (!saved && drawingModel != null)
                {
                    try
                    {
                        string title = drawingModel.GetTitle() ?? createdTitle;
                        if (!string.IsNullOrWhiteSpace(title)) _swApp.CloseDoc(title);
                    }
                    catch { }
                }
                throw;
            }
        }

        public bool OpenDrawing(string drawingPath)
        {
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath)) return false;
            try
            {
                var already = _swApp.GetOpenDocumentByName(drawingPath) as ModelDoc2;
                if (already != null)
                {
                    int errors = 0;
                    _swApp.ActivateDoc3(already.GetTitle(), true, 0, ref errors);
                    return true;
                }

                int openErrors = 0, openWarnings = 0;
                var opened = _swApp.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref openErrors, ref openWarnings) as ModelDoc2;
                return opened != null;
            }
            catch (Exception ex)
            {
                Logger.Info("Mevcut teknik resim açılamadı: " + ex.Message);
                return false;
            }
        }

        private static List<DrawingViewSlot> CreateRequiredViews(DrawingDoc drawing, ModelDoc2 part, string partPath, string configName,
            bool isSheetMetal, double width, double height)
        {
            var list = new List<DrawingViewSlot>();

            // Ön/yan/üst görünüşleri tek tek isimle eklemek yerine SOLIDWORKS'ün kendi ilk-açı
            // 3-view üreticisini kullan. Bu, farklı model boyutlarında ve konfigürasyonlarda daha güvenilir.
            list.AddRange(CreateFirstAngleOrthographicViews(drawing, partPath, configName));

            // Standard model görünüş adları SOLIDWORKS diline göre farklı dönebilir.
            // CreateDrawViewFromModelView3 görünüş adını modeldeki adla birebir ister; bu nedenle
            // sabit *Isometric yerine modelin gerçek görünüş listesinden İzometrik/Isometric adını çöz.
            var iso = CreateRequiredIsometricView(drawing, part, partPath, configName,
                width * 0.50, height * 0.25);
            list.Add(iso);

            if (isSheetMetal)
            {
                try
                {
                    // FlipView=false bilinçli: büküm yönleri yerleşim uğruna ters çevrilmez/aynalanmaz.
                    var flat = drawing.CreateFlatPatternViewFromModelView3(partPath, configName ?? "",
                        width * 0.76, height * 0.70, 0.0, false, false);
                    if (flat == null)
                        throw new InvalidOperationException("SOLIDWORKS Flat Pattern görünüşü döndürmedi.");

                    try { flat.ReferencedConfiguration = configName ?? ""; } catch { }
                    try { flat.FlipView = false; } catch { }
                    try { flat.ShowSheetMetalBendNotes = true; } catch { }

                    bool isFlat = false;
                    try { isFlat = flat.IsFlatPatternView(); } catch { }
                    if (!isFlat)
                        throw new InvalidOperationException("Oluşturulan görünüş Flat Pattern olarak doğrulanamadı.");

                    bool flipped = false;
                    try { flipped = flat.FlipView; } catch { }
                    if (flipped)
                        throw new InvalidOperationException("Flat Pattern görünüşü beklenmedik şekilde ters çevrildi. Yanlış büküm yönü riski nedeniyle işlem durduruldu.");

                    list.Add(new DrawingViewSlot(flat, DrawingViewRole.FlatPattern));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Sac parça algılandı ancak güvenli Flat Pattern oluşturulamadı. Teknik resim kaydedilmedi; Flat-Pattern/konfigürasyon ve büküm yönlerini kontrol edin.", ex);
                }
            }

            return list;
        }

        private static List<DrawingViewSlot> CreateFirstAngleOrthographicViews(DrawingDoc drawing, string partPath, string configName)
        {
            var beforeNames = new HashSet<string>(EnumerateDrawingViews(drawing)
                .Select(v => SafeViewName(v)), StringComparer.OrdinalIgnoreCase);

            bool created = false;
            try { created = drawing.Create1stAngleViews2(partPath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("SOLIDWORKS ilk-açı üç görünüş komutu çalıştırılamadı.", ex);
            }
            if (!created)
                throw new InvalidOperationException("SOLIDWORKS ilk-açı Ön/Yan/Üst görünüşlerini oluşturamadı.");

            var newViews = EnumerateDrawingViews(drawing)
                .Where(v => !beforeNames.Contains(SafeViewName(v)))
                .ToList();

            if (newViews.Count < 3)
                throw new InvalidOperationException("İlk-açı komutu tamamlandı ancak üç ortografik görünüş bulunamadı. Bulunan: " + newViews.Count);

            // Create1stAngleViews2'nin oluşturduğu üç görünüşü konum ilişkisine göre sınıflandır.
            // İlk-açı yerleşiminde ön ve üst dikey hizalıdır; üst, ön görünüşün altındadır.
            SolidWorks.Interop.sldworks.View front, top, side;
            ClassifyFirstAngleViews(newViews, out front, out top, out side);

            foreach (var view in new[] { front, top, side })
            {
                try { view.ReferencedConfiguration = configName ?? ""; } catch { }
            }

            // Kullanıcının üretim yerleşimi: yan görünüş önün SAĞINDA, üst görünüş önün ALTINDA.
            // İlk-açıdan gelen gerçek görünüş yönünü koruyabilmek için hizalamayı önce kır; yalnız kağıttaki
            // pozisyonu değiştir. Böylece karşı tarafa taşırken projeksiyon yönü otomatik değişmez.
            try { side.RemoveAlignment(); } catch { }
            try { top.RemoveAlignment(); } catch { }

            return new List<DrawingViewSlot>
            {
                new DrawingViewSlot(front, DrawingViewRole.Front),
                new DrawingViewSlot(side, DrawingViewRole.Side),
                new DrawingViewSlot(top, DrawingViewRole.Top)
            };
        }

        private static void ClassifyFirstAngleViews(List<SolidWorks.Interop.sldworks.View> views,
            out SolidWorks.Interop.sldworks.View front,
            out SolidWorks.Interop.sldworks.View top,
            out SolidWorks.Interop.sldworks.View side)
        {
            if (views == null || views.Count < 3) throw new ArgumentException("Üç görünüş gerekli.");

            // Yalnız yeni oluşturulan ilk üç ortografik görünüşü kullan.
            var candidates = views.Take(3).ToList();
            double bestDx = double.MaxValue;
            int a = 0, b = 1;
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var pi = GetViewPosition(candidates[i]);
                    var pj = GetViewPosition(candidates[j]);
                    double dx = Math.Abs(pi[0] - pj[0]);
                    if (dx < bestDx)
                    {
                        bestDx = dx;
                        a = i;
                        b = j;
                    }
                }
            }

            var pa = GetViewPosition(candidates[a]);
            var pb = GetViewPosition(candidates[b]);
            if (pa[1] >= pb[1])
            {
                front = candidates[a];
                top = candidates[b];
            }
            else
            {
                front = candidates[b];
                top = candidates[a];
            }

            // out parametreler C# lambda içinde kullanılamaz (CS1628).
            // Ön/üst görünüşleri yerel değişkenlere alıp kalan üçüncü görünüşü açık döngüyle bul.
            var frontLocal = front;
            var topLocal = top;
            side = null;
            foreach (var candidate in candidates)
            {
                if (!ReferenceEquals(candidate, frontLocal) && !ReferenceEquals(candidate, topLocal))
                {
                    side = candidate;
                    break;
                }
            }
            if (side == null)
                throw new InvalidOperationException("İlk-açı yan görünüşü sınıflandırılamadı.");
        }

        private static DrawingViewSlot CreateRequiredIsometricView(DrawingDoc drawing, ModelDoc2 part, string partPath,
            string configName, double x, double y)
        {
            var candidates = new List<string>();
            AddUniqueViewName(candidates, "*Isometric");
            AddUniqueViewName(candidates, "*İzometrik");
            AddUniqueViewName(candidates, "*Izometrik");
            AddUniqueViewName(candidates, "*Isometrik");

            var modelNames = GetModelViewNames(part);
            foreach (var name in modelNames)
            {
                if (LooksLikeIsometricViewName(name))
                    AddUniqueViewName(candidates, name);
            }

            Exception lastError = null;
            foreach (var viewName in candidates)
            {
                try
                {
                    var view = drawing.CreateDrawViewFromModelView3(partPath, viewName, x, y, 0.0);
                    if (view == null) continue;
                    try { view.ReferencedConfiguration = configName ?? ""; } catch { }
                    Logger.Info("İzometrik görünüş model adıyla oluşturuldu: " + viewName);
                    return new DrawingViewSlot(view, DrawingViewRole.Isometric);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Logger.Info("İzometrik görünüş adayı başarısız: " + viewName + " | " + ex.Message);
                }
            }

            string available = modelNames.Count == 0
                ? "(model görünüş listesi boş)"
                : string.Join(", ", modelNames.Take(24));

            throw new InvalidOperationException(
                "Zorunlu izometrik görünüş oluşturulamadı. Modeldeki görünüş adları: " + available, lastError);
        }

        private static List<string> GetModelViewNames(ModelDoc2 part)
        {
            var result = new List<string>();
            if (part == null) return result;
            try
            {
                var raw = part.GetModelViewNames();
                var array = raw as Array;
                if (array == null) return result;
                foreach (var item in array)
                {
                    var name = item as string;
                    if (!string.IsNullOrWhiteSpace(name)) AddUniqueViewName(result, name.Trim());
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Model görünüş adları okunamadı: " + ex.Message);
            }
            return result;
        }

        private static void AddUniqueViewName(List<string> names, string name)
        {
            if (names == null || string.IsNullOrWhiteSpace(name)) return;
            if (!names.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                names.Add(name);
        }

        private static bool LooksLikeIsometricViewName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.Trim().TrimStart('*')
                .Replace('İ', 'I')
                .Replace('ı', 'i')
                .ToLowerInvariant();
            return n.Contains("isometric") || n.Contains("isometrik") ||
                   n.Contains("izometric") || n.Contains("izometrik");
        }

        private static DrawingViewSlot CreateRequiredModelView(DrawingDoc drawing, string partPath, string configName,
            string modelViewName, double x, double y, DrawingViewRole role)
        {
            try
            {
                var view = drawing.CreateDrawViewFromModelView3(partPath, modelViewName, x, y, 0.0);
                if (view == null)
                    throw new InvalidOperationException(modelViewName + " görünüşü SOLIDWORKS tarafından oluşturulmadı.");
                try { view.ReferencedConfiguration = configName ?? ""; } catch { }
                return new DrawingViewSlot(view, role);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Zorunlu " + modelViewName + " görünüşü oluşturulamadı.", ex);
            }
        }

        private static void ForceFirstAngleProjection(DrawingDoc drawing)
        {
            var sheet = drawing?.GetCurrentSheet() as Sheet;
            if (sheet == null) throw new InvalidOperationException("Aktif teknik resim sayfası bulunamadı.");

            try
            {
                var props = GetSheetProperties(sheet);
                if (props == null || props.Length < 7)
                    throw new InvalidOperationException("Sayfa özellikleri okunamadı.");

                // Şablonun kağıt tipi, ölçeği ve boyutları korunur; yalnız FirstAngle=True yapılır.
                sheet.SetProperties((int)props[0], (int)props[1], props[2], props[3], true, props[5], props[6]);

                // Kullanıcının istediği yalnız görünüş yerleşimi değil, sayfanın gerçek yansıtma özelliğinin
                // de İlk Açı olmasıdır. Bu nedenle ayarı tekrar okuyup doğrulamadan devam etmiyoruz.
                var verify = GetSheetProperties(sheet);
                if (verify == null || verify.Length < 5 || Math.Abs(verify[4]) < 0.5)
                    throw new InvalidOperationException("SOLIDWORKS sayfa yansıtma tipini İlk Açı olarak doğrulamadı.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Teknik resim sayfası İlk Açı yansıtma tipine geçirilemedi. Yanlış projeksiyon riski nedeniyle işlem durduruldu.", ex);
            }
        }

        private static double[] GetSheetProperties(Sheet sheet)
        {
            if (sheet == null) return null;
            object raw = null;
            try { raw = sheet.GetProperties2(); }
            catch
            {
                try { raw = sheet.GetProperties(); } catch { }
            }

            var direct = raw as double[];
            if (direct != null) return direct;

            var array = raw as Array;
            if (array == null) return null;
            var values = new double[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                try { values[i] = Convert.ToDouble(array.GetValue(i)); }
                catch { return null; }
            }
            return values;
        }

        private static List<SolidWorks.Interop.sldworks.View> EnumerateDrawingViews(DrawingDoc drawing)
        {
            var result = new List<SolidWorks.Interop.sldworks.View>();
            if (drawing == null) return result;
            try
            {
                var sheetView = drawing.GetFirstView() as SolidWorks.Interop.sldworks.View;
                var view = sheetView?.GetNextView() as SolidWorks.Interop.sldworks.View;
                int guard = 0;
                while (view != null && guard++ < 100)
                {
                    result.Add(view);
                    view = view.GetNextView() as SolidWorks.Interop.sldworks.View;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Teknik resim görünüşleri listelenemedi: " + ex.Message);
            }
            return result;
        }

        private static string SafeViewName(SolidWorks.Interop.sldworks.View view)
        {
            try { return view?.GetName2() ?? ""; } catch { return ""; }
        }

        private static double[] GetViewPosition(SolidWorks.Interop.sldworks.View view)
        {
            try
            {
                var p = view?.Position as double[];
                if (p != null && p.Length >= 2) return new[] { p[0], p[1] };
            }
            catch { }
            return new[] { 0.0, 0.0 };
        }

        private static void LayoutViewsDynamically(List<DrawingViewSlot> views, bool isSheetMetal,
            double sheetWidth, double sheetHeight, SheetBounds titleBlock)
        {
            if (views == null || views.Count == 0) return;

            var regions = BuildLayoutRegions(isSheetMetal, sheetWidth, sheetHeight, titleBlock);
            foreach (var slot in views)
            {
                SheetBounds region;
                if (!regions.TryGetValue(slot.Role, out region)) continue;
                slot.Region = region;
            }

            // Ön/yan/üst aynı teknik resim ölçeğini kullanmalı.
            var ortho = views.Where(v => v.Role == DrawingViewRole.Front ||
                                         v.Role == DrawingViewRole.Side ||
                                         v.Role == DrawingViewRole.Top).ToList();
            double commonScale = double.MaxValue;
            foreach (var slot in ortho)
            {
                double candidate = CalculateFitScale(slot.View, slot.Region, 0.84);
                if (candidate > 0 && candidate < commonScale) commonScale = candidate;
            }
            if (commonScale == double.MaxValue || commonScale <= 0) commonScale = 1.0;
            foreach (var slot in ortho) ApplyViewScale(slot.View, commonScale);

            // Flat Pattern ve izometrik kendi alanına göre bağımsız ölçeklenebilir.
            foreach (var slot in views.Where(v => v.Role == DrawingViewRole.FlatPattern || v.Role == DrawingViewRole.Isometric))
            {
                double scale = CalculateFitScale(slot.View, slot.Region, slot.Role == DrawingViewRole.FlatPattern ? 0.88 : 0.82);
                if (scale > 0) ApplyViewScale(slot.View, scale);
            }

            // Ölçekten sonra her görünüş kendi hücresinin merkezine taşınır.
            foreach (var slot in views)
            {
                try { slot.View.Position = new double[] { slot.Region.CenterX, slot.Region.CenterY }; }
                catch (Exception ex) { Logger.Info(slot.Role + " görünüşü konumlandırılamadı: " + ex.Message); }
            }
        }

        private static Dictionary<DrawingViewRole, SheetBounds> BuildLayoutRegions(bool isSheetMetal,
            double width, double height, SheetBounds titleBlock)
        {
            double left = width * 0.045;
            double right = width * 0.955;
            double bottom = height * 0.075;
            double top = height * 0.925;
            double gapX = width * 0.025;
            double gapY = height * 0.035;

            double topRowBottom = height * 0.535;
            double lowerRowTop = height * 0.455;

            var map = new Dictionary<DrawingViewRole, SheetBounds>();
            if (isSheetMetal)
            {
                double frontRight = width * 0.315;
                double sideLeft = width * 0.345;
                double sideRight = width * 0.555;
                double flatLeft = width * 0.585;

                map[DrawingViewRole.Front] = new SheetBounds(left, topRowBottom, frontRight, top);
                map[DrawingViewRole.Side] = new SheetBounds(sideLeft, topRowBottom, sideRight, top);

                double flatBottom = topRowBottom;
                if (titleBlock.IsValid && titleBlock.IntersectsHorizontalRange(flatLeft, right))
                    flatBottom = Math.Max(flatBottom, titleBlock.Top + gapY);
                if (flatBottom > top - height * 0.10) flatBottom = topRowBottom;
                map[DrawingViewRole.FlatPattern] = new SheetBounds(flatLeft, flatBottom, right, top);

                map[DrawingViewRole.Top] = new SheetBounds(left, bottom, frontRight, lowerRowTop);

                double isoLeft = sideLeft;
                double isoRight = width * 0.635;
                if (titleBlock.IsValid && titleBlock.Bottom < lowerRowTop && titleBlock.Left > isoLeft)
                    isoRight = Math.Min(isoRight, titleBlock.Left - gapX);
                if (isoRight - isoLeft < width * 0.16) isoRight = width * 0.635;
                map[DrawingViewRole.Isometric] = new SheetBounds(isoLeft, bottom, isoRight, lowerRowTop);
            }
            else
            {
                double frontRight = width * 0.405;
                double sideLeft = width * 0.445;
                double sideRight = width * 0.735;
                map[DrawingViewRole.Front] = new SheetBounds(left, topRowBottom, frontRight, top);
                map[DrawingViewRole.Side] = new SheetBounds(sideLeft, topRowBottom, sideRight, top);
                map[DrawingViewRole.Top] = new SheetBounds(left, bottom, frontRight, lowerRowTop);

                double isoRight = width * 0.705;
                if (titleBlock.IsValid && titleBlock.Bottom < lowerRowTop && titleBlock.Left > sideLeft)
                    isoRight = Math.Min(isoRight, titleBlock.Left - gapX);
                if (isoRight - sideLeft < width * 0.17) isoRight = width * 0.705;
                map[DrawingViewRole.Isometric] = new SheetBounds(sideLeft, bottom, isoRight, lowerRowTop);
            }
            return map;
        }

        private static double CalculateFitScale(SolidWorks.Interop.sldworks.View view, SheetBounds region, double paddingFactor)
        {
            if (view == null || !region.IsValid) return 0;
            try
            {
                double[] outline = view.GetOutline() as double[];
                if (outline == null || outline.Length < 4) return 0;
                double currentW = Math.Abs(outline[2] - outline[0]);
                double currentH = Math.Abs(outline[3] - outline[1]);
                if (currentW < 1e-9 || currentH < 1e-9) return 0;

                double currentScale = view.ScaleDecimal;
                if (currentScale <= 0) currentScale = 1.0;
                double ratio = Math.Min(region.Width / currentW, region.Height / currentH) * paddingFactor;
                ratio = Math.Max(0.10, Math.Min(3.0, ratio));
                return Math.Max(0.005, currentScale * ratio);
            }
            catch { return 0; }
        }

        private static void ApplyViewScale(SolidWorks.Interop.sldworks.View view, double scale)
        {
            if (view == null || scale <= 0) return;
            try
            {
                view.UseSheetScale = 0;
                view.ScaleDecimal = scale;
            }
            catch (Exception ex)
            {
                Logger.Info("Teknik resim görünüş ölçeği ayarlanamadı: " + ex.Message);
            }
        }

        private static SheetBounds GetTitleBlockBounds(DrawingDoc drawing, double width, double height)
        {
            try
            {
                var sheet = drawing?.GetCurrentSheet() as Sheet;
                dynamic titleBlock = sheet != null ? sheet.TitleBlock : null;
                if (titleBlock != null)
                {
                    double xUpperLeft = 0, yUpperLeft = 0, xLowerRight = 0, yLowerRight = 0;
                    // SOLIDWORKS interop sürümleri bazı COM ByRef parametrelerini out yerine ref üretir.
                    // dynamic + ref kullanımı 2024 dahil bu imza farkına karşı daha dayanıklıdır.
                    titleBlock.GetExtents(ref xUpperLeft, ref yUpperLeft, ref xLowerRight, ref yLowerRight);
                    var actual = new SheetBounds(
                        Math.Min(xUpperLeft, xLowerRight), Math.Min(yUpperLeft, yLowerRight),
                        Math.Max(xUpperLeft, xLowerRight), Math.Max(yUpperLeft, yLowerRight));
                    if (actual.IsValid) return actual;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Antet sınırları okunamadı; güvenli varsayılan alan kullanılacak: " + ex.Message);
            }

            // Şablonda TitleBlock feature tanımlı değilse sağ-altı güvenli alan olarak ayır.
            return new SheetBounds(width * 0.67, height * 0.02, width * 0.98, height * 0.30);
        }

        private static void ValidateRequiredViews(List<DrawingViewSlot> views, bool isSheetMetal)
        {
            var required = new List<DrawingViewRole>
            {
                DrawingViewRole.Front,
                DrawingViewRole.Side,
                DrawingViewRole.Top,
                DrawingViewRole.Isometric
            };
            if (isSheetMetal) required.Add(DrawingViewRole.FlatPattern);

            foreach (var role in required)
            {
                if (views == null || views.Count(v => v.Role == role && v.View != null) != 1)
                    throw new InvalidOperationException("Zorunlu " + RoleText(role) + " görünüşü doğrulanamadı; teknik resim kaydedilmedi.");
            }

            if (views.Count != required.Count)
                throw new InvalidOperationException("Beklenen görünüş sayısı " + required.Count + " fakat " + views.Count + " bulundu; teknik resim kaydedilmedi.");

            if (isSheetMetal)
            {
                var flat = views.First(v => v.Role == DrawingViewRole.FlatPattern).View;
                bool isFlat = false;
                try { isFlat = flat.IsFlatPatternView(); } catch { }
                if (!isFlat) throw new InvalidOperationException("Flat Pattern görünüşü doğrulanamadı; teknik resim kaydedilmedi.");

                bool flipped = false;
                try { flipped = flat.FlipView; } catch { }
                if (flipped)
                    throw new InvalidOperationException("Flat Pattern ters çevrilmiş görünüyor. Büküm yönü güvenliği için teknik resim kaydedilmedi.");
            }
        }

        private static void ValidatePlacement(List<DrawingViewSlot> views, double width, double height, SheetBounds titleBlock)
        {
            const double border = 0.0015; // 1.5 mm güvenlik payı
            var outlines = new Dictionary<DrawingViewSlot, SheetBounds>();
            foreach (var slot in views)
            {
                var box = GetViewBounds(slot.View);
                if (!box.IsValid)
                    throw new InvalidOperationException(RoleText(slot.Role) + " görünüş sınırı okunamadı; teknik resim kaydedilmedi.");
                if (box.Left < border || box.Bottom < border || box.Right > width - border || box.Top > height - border)
                    throw new InvalidOperationException(RoleText(slot.Role) + " görünüşü sayfa dışına taşıyor; teknik resim kaydedilmedi.");
                if (titleBlock.IsValid && box.Intersects(titleBlock, 0.0015))
                    throw new InvalidOperationException(RoleText(slot.Role) + " görünüşü antet alanına taşıyor; teknik resim kaydedilmedi.");
                outlines[slot] = box;
            }

            var items = views.ToList();
            for (int i = 0; i < items.Count; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (outlines[items[i]].Intersects(outlines[items[j]], 0.0025))
                        throw new InvalidOperationException(RoleText(items[i].Role) + " ve " + RoleText(items[j].Role) +
                            " görünüşleri çakışıyor; teknik resim kaydedilmedi.");
                }
            }
        }

        private static SheetBounds GetViewBounds(SolidWorks.Interop.sldworks.View view)
        {
            try
            {
                var o = view?.GetOutline() as double[];
                if (o != null && o.Length >= 4)
                    return new SheetBounds(Math.Min(o[0], o[2]), Math.Min(o[1], o[3]), Math.Max(o[0], o[2]), Math.Max(o[1], o[3]));
            }
            catch { }
            return SheetBounds.Invalid;
        }

        private static string RoleText(DrawingViewRole role)
        {
            switch (role)
            {
                case DrawingViewRole.Front: return "Ön";
                case DrawingViewRole.Side: return "Yan";
                case DrawingViewRole.Top: return "Üst";
                case DrawingViewRole.FlatPattern: return "Düz Çoğaltma (Flat Pattern)";
                case DrawingViewRole.Isometric: return "İzometrik";
                default: return role.ToString();
            }
        }

        private static void SetSheetCustomPropertyView(DrawingDoc drawing, List<DrawingViewSlot> views)
        {
            if (drawing == null || views == null) return;
            try
            {
                DrawingViewSlot primary = views.FirstOrDefault(v => v.Role == DrawingViewRole.Front) ??
                                          (views.Count > 0 ? views[0] : null);
                if (primary == null || primary.View == null) return;
                var sheet = drawing.GetCurrentSheet() as Sheet;
                if (sheet != null) sheet.CustomPropertyView = primary.View.GetName2();
            }
            catch (Exception ex)
            {
                Logger.Info("Teknik resim antetinin model özel özellik görünüşü atanamadı: " + ex.Message);
            }
        }

        private static void GetSheetSize(DrawingDoc drawing, out double width, out double height)
        {
            width = 0; height = 0;
            try
            {
                var sheet = drawing.GetCurrentSheet() as Sheet;
                if (sheet != null) sheet.GetSize(ref width, ref height);
            }
            catch { }
        }

        private static int ImportModelAnnotations(ModelDoc2 drawingModel, DrawingDoc drawing, SolidWorks.Interop.sldworks.View view)
        {
            try
            {
                drawingModel.ClearSelection2(true);
                if (!SelectView(drawingModel, view)) return 0;
                object inserted = drawing.InsertModelAnnotations3(0, InsertTypes, false, true, false, false);
                var array = inserted as Array;
                int count = array?.Length ?? (inserted != null ? 1 : 0);
                if (count > 1) TryAutoArrangeImportedDimensions(drawingModel, inserted);
                return count;
            }
            catch (Exception ex)
            {
                Logger.Info("Model ölçüleri/anotasyonları aktarılamadı: " + ex.Message);
                return 0;
            }
            finally
            {
                try { drawingModel.ClearSelection2(true); } catch { }
            }
        }

        private static void TryAutoArrangeImportedDimensions(ModelDoc2 drawingModel, object inserted)
        {
            try
            {
                var array = inserted as Array;
                if (array == null || array.Length == 0) return;
                drawingModel.ClearSelection2(true);

                var selectionMgr = drawingModel.SelectionManager as SelectionMgr;
                var selectData = selectionMgr?.CreateSelectData();
                if (selectData == null) return;

                int selected = 0;
                foreach (object item in array)
                {
                    var annotation = item as Annotation;
                    if (annotation == null) continue;
                    try
                    {
                        if (annotation.Select3(true, selectData)) selected++;
                    }
                    catch { }
                }

                if (selected > 1)
                {
                    // SOLIDWORKS'ün AutoArrange motoru, seçili ölçüleri görünüş çevresine dağıtır.
                    // 6 mm aralık üretim resimlerinde üst üste binmeyi ciddi ölçüde azaltır.
                    drawingModel.Extension.AlignDimensions(
                        (int)swAlignDimensionType_e.swAlignDimensionType_AutoArrange, 0.006);
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Aktarılan ölçüler otomatik düzenlenemedi: " + ex.Message);
            }
        }

        private static bool AutoDimensionView(ModelDoc2 drawingModel, DrawingDoc drawing, SolidWorks.Interop.sldworks.View view)
        {
            try
            {
                drawingModel.ClearSelection2(true);
                if (!SelectView(drawingModel, view)) return false;
                // swAutodimEntitiesAll=1; baseline=1; horizontal below=-1; vertical left=-1.
                int status = drawing.AutoDimension(1, 1, -1, 1, -1);
                return status == 0; // swAutodimStatusSuccess
            }
            catch (Exception ex)
            {
                Logger.Info("Görünüş otomatik ölçülendirilemedi: " + ex.Message);
                return false;
            }
            finally
            {
                try { drawingModel.ClearSelection2(true); } catch { }
            }
        }

        private static bool SelectView(ModelDoc2 drawingModel, SolidWorks.Interop.sldworks.View view)
        {
            try
            {
                string name = view.GetName2();
                return drawingModel.Extension.SelectByID2(name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
            }
            catch { return false; }
        }

        private static bool IsSheetMetal(ModelDoc2 model)
        {
            if (model == null) return false;
            try
            {
                var part = model as PartDoc;
                object bodiesObject = part?.GetBodies2((int)swBodyType_e.swSolidBody, true);
                var bodies = bodiesObject as object[];
                if (bodies != null)
                {
                    foreach (object item in bodies)
                    {
                        var body = item as Body2;
                        try { if (body != null && body.IsSheetMetal()) return true; }
                        catch { }
                        finally { ReleaseCom(body); }
                    }
                }
            }
            catch { }

            Feature feature = null;
            try { feature = model.FirstFeature() as Feature; } catch { }
            while (feature != null)
            {
                Feature next = null;
                try
                {
                    string type = feature.GetTypeName2() ?? "";
                    if (type.Equals("SheetMetal", StringComparison.OrdinalIgnoreCase) ||
                        type.Equals("FlatPattern", StringComparison.OrdinalIgnoreCase) ||
                        type.Equals("SMBaseFlange", StringComparison.OrdinalIgnoreCase)) return true;
                    next = feature.GetNextFeature() as Feature;
                }
                catch { }
                finally { ReleaseCom(feature); }
                feature = next;
            }
            return false;
        }

        private static void ReleaseCom(object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
        }

        private enum DrawingViewRole
        {
            Front,
            Side,
            Top,
            FlatPattern,
            Isometric
        }

        private sealed class DrawingViewSlot
        {
            public DrawingViewSlot(SolidWorks.Interop.sldworks.View view, DrawingViewRole role)
            {
                View = view;
                Role = role;
                Region = SheetBounds.Invalid;
            }

            public SolidWorks.Interop.sldworks.View View { get; }
            public DrawingViewRole Role { get; }
            public SheetBounds Region { get; set; }
        }

        private struct SheetBounds
        {
            public static readonly SheetBounds Invalid = new SheetBounds(0, 0, 0, 0);

            public SheetBounds(double left, double bottom, double right, double top)
            {
                Left = left;
                Bottom = bottom;
                Right = right;
                Top = top;
            }

            public double Left { get; }
            public double Bottom { get; }
            public double Right { get; }
            public double Top { get; }
            public double Width => Right - Left;
            public double Height => Top - Bottom;
            public double CenterX => (Left + Right) * 0.5;
            public double CenterY => (Bottom + Top) * 0.5;
            public bool IsValid => Right > Left + 1e-9 && Top > Bottom + 1e-9;

            public bool Intersects(SheetBounds other, double padding)
            {
                if (!IsValid || !other.IsValid) return false;
                return Left < other.Right + padding && Right > other.Left - padding &&
                       Bottom < other.Top + padding && Top > other.Bottom - padding;
            }

            public bool IntersectsHorizontalRange(double left, double right)
            {
                return IsValid && Left < right && Right > left;
            }
        }
    }
}
