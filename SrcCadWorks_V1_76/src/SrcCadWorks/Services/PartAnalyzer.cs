using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class PartAnalyzer
    {
        private readonly SldWorks _swApp;
        private readonly Func<bool> _shouldCancel;
        private static readonly HashSet<string> BendTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OneBend", "EdgeFlange", "MiterFlange", "SketchBend", "Hem", "Jog", "Fold", "Unfold", "SM3dBend"
        };

        public PartAnalyzer(SldWorks swApp, Func<bool> shouldCancel = null)
        {
            _swApp = swApp;
            _shouldCancel = shouldCancel;
        }

        private void ThrowIfCancelled()
        {
            if (_shouldCancel != null && _shouldCancel())
                throw new OperationCanceledException("Parça analizi ESC/kullanıcı isteği ile iptal edildi.");
        }

        public LaserPartRow Analyze(string path, string configuration, int quantity)
        {
            ThrowIfCancelled();
            int openErrors = 0, openWarnings = 0;
            bool openedByUs = false;
            bool wasVisibleBefore = false;
            ModelDoc2 model = null;
            string originalConfiguration = "";
            bool switchedConfiguration = false;
            using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
            try
            {
                model = _swApp.GetOpenDocumentByName(path) as ModelDoc2;
                ThrowIfCancelled();
                if (model == null)
                {
                    ThrowIfCancelled();
                    model = _swApp.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        configuration ?? "", ref openErrors, ref openWarnings) as ModelDoc2;
                    openedByUs = model != null;
                    if (openedByUs) { try { ((dynamic)model).Visible = false; } catch { } }
                    foregroundGuard.RestoreIfSolidWorksStoleFocus();
                    ThrowIfCancelled();
                }
                if (model == null) throw new InvalidOperationException("Parça açılamadı. OpenDoc hata=" + openErrors);
                ThrowIfCancelled();

                wasVisibleBefore = ComUtil.SafeVisible(model);
                try { originalConfiguration = model.ConfigurationManager?.ActiveConfiguration?.Name ?? ""; } catch { }
                if (!openedByUs && !string.IsNullOrWhiteSpace(configuration) &&
                    !string.Equals(originalConfiguration, configuration, StringComparison.OrdinalIgnoreCase))
                {
                    try { switchedConfiguration = model.ShowConfiguration2(configuration); } catch { }
                }

                // Büyük montaj optimizasyonu: önce gövde seviyesinde hızlı Sheet Metal kontrolü yap.
                // Standart katı parçalar burada elenir; Hole Wizard/feature ağacını boşuna taramayız.
                bool isSheetMetal = IsAnyBodySheetMetal(model);
                if (!isSheetMetal) isSheetMetal = HasSheetMetalFeatureQuick(model);
                if (!isSheetMetal) return null;

                bool hasBends = false;
                bool hasTappedHoles = false;
                bool hasCountersink = false;
                int tappedHoleWizardCount = 0;
                var tappedHoleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                double? thickness = null;
                int flatPatterns = 0;

                // Ağır feature taraması yalnız gerçek saclarda yapılır.
                WalkFeatures(model.FirstFeature() as Feature, feature =>
                {
                    ThrowIfCancelled();
                    string type = SafeTypeName(feature);
                    if (string.Equals(type, "FlatPattern", StringComparison.OrdinalIgnoreCase)) flatPatterns++;
                    if (BendTypes.Contains(type)) hasBends = true;

                    if (!thickness.HasValue && string.Equals(type, "SheetMetal", StringComparison.OrdinalIgnoreCase))
                    {
                        object data = null;
                        try
                        {
                            data = feature.GetDefinition();
                            dynamic dyn = data;
                            double meters = Convert.ToDouble(dyn.Thickness, CultureInfo.InvariantCulture);
                            if (meters > 0) thickness = Math.Round(meters * 1000.0, 4);
                        }
                        catch { }
                        finally { ReleaseCom(data); }
                    }

                    if (string.Equals(type, "HoleWzd", StringComparison.OrdinalIgnoreCase))
                    {
                        // Yalnız gerçek tapped / pipe tapped Hole Wizard tipleri KILAVUZ sayılır.
                        // Clearance, düz delik, countersink ve counterbore KILAVUZ değildir.
                        IWizardHoleFeatureData2 data = null;
                        try
                        {
                            // Only features active in the analysed configuration affect the route.
                            var suppression = feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as Array;
                            if (suppression == null || suppression.Length == 0)
                            {
                                Logger.Info("Hole Wizard bastırma durumu okunamadı: " + (feature.Name ?? ""));
                                return;
                            }
                            if (Convert.ToBoolean(suppression.GetValue(0), CultureInfo.InvariantCulture)) return;
                            data = feature.GetDefinition() as IWizardHoleFeatureData2;
                            if (data != null)
                            {
                                int holeType = data.Type;
                                string enumName = Enum.GetName(typeof(swWzdHoleTypes_e), holeType) ?? "";
                                if (LaserOperation.IsCountersinkHoleType(enumName)) hasCountersink = true;
                                if (IsTappedHoleWizardType(enumName))
                                {
                                    hasTappedHoles = true;
                                    tappedHoleWizardCount++;
                                    if (!string.IsNullOrWhiteSpace(enumName)) tappedHoleTypes.Add(enumName);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Info("Hole Wizard tipi okunamadı; KILAVUZ/HAVŞA varsayılmadı. Özellik=" + (feature.Name ?? "") + " | " + ex.Message);
                        }
                        finally { ReleaseCom(data); }
                    }
                });

                if (!thickness.HasValue)
                {
                    var t = CustomPropertyReader.First(model, configuration, "Kalınlık", "Kalinlik", "Thickness", "Sac Kalınlığı", "Sac Kalinligi");
                    thickness = ParseMillimeters(t);
                }

                // Parça kodu çözümleme:
                // - Şirket içi üretim kodu dosya adındaysa (PBC..., RCRC..., DRLC... gibi) dosya adı önceliklidir.
                // - Aksi halde yalnız açık ve güvenilir parça-kodu alanları okunur.
                // - Genel "Kod" / "Code" alanları artık kullanılmaz; bunlar sensör/model/tedarikçi kodu olabilir.
                string sourceFileCode = Path.GetFileNameWithoutExtension(path) ?? "";
                string explicitPartCode = CustomPropertyReader.First(model, configuration,
                    "Parça Kodu", "Parca Kodu", "Parça No", "Parca No", "Parça Numarası", "Parca Numarasi",
                    "PART NUMBER", "Part Number", "PartNo", "Part No",
                    "Dosya Kodu", "Ürün Kodu", "Urun Kodu", "Item Number", "Item No", "Stock Code", "Stok Kodu");
                string code = ResolvePartCode(sourceFileCode, explicitPartCode);

                var material = CustomPropertyReader.First(model, configuration, "Malzeme", "Material", "SW-Material");

                // V1.11.10: Şirket özel özellik şablonunda Excel'deki "Renk Kodu" alanının kaynağı
                // SOLIDWORKS Custom Property Tab Builder'daki "Boya Tipi" seçimidir
                // (örn. Şase Rengi, ISG Rengi, Motor Grubu Rengi, Platform Rengi, Galvaniz).
                // Eski Renk Kodu/RAL/Color alanları geriye uyumluluk için fallback olarak korunur.
                var color = CustomPropertyReader.First(model, configuration,
                    "Boya Tipi", "BoyaTipi", "Paint Type",
                    "Renk Kodu", "RenkKodu", "RAL Kodu", "RAL Code", "Color Code",
                    "RAL", "Renk", "Color", "Boya Rengi");

                // V1.11.7: Custom Property Tab Builder'daki yeni 'Kaynak İşlemi' (VAR/YOK)
                // doğrudan Excel'deki Kaynak Prosesi sütununa aktarılır. Eski aliaslar geriye uyumluluk için korunur.
                var weld = CustomPropertyReader.First(model, configuration,
                    "Kaynak İşlemi", "Kaynak Islemi", "KAYNAK İŞLEMİ", "KAYNAK ISLEMI",
                    "Kaynak Prosesi", "Kaynak Var/Yok", "Kaynak Var Yok",
                    "Kaynak", "Weld", "Welding");
                var productionDefinition = CustomPropertyReader.ProductionDefinition(model, configuration);

                string laserProperty, laserValue;
                bool isLaserTagged = CustomPropertyReader.IsLaserTagged(model, configuration, out laserProperty, out laserValue);
                // Reuse the resolved İşlem Tipi value; Kaynak İşlemi (VAR/YOK) is a different property.
                bool isWeldTagged = LaserOperation.IsWeldProcessTag(laserValue);

                var operation = LaserOperation.Build(hasBends, hasTappedHoles, hasCountersink);

                string sanitizedCode = SanitizeCode(code);
                var row = new LaserPartRow
                {
                    Selected = true,
                    PartCode = sanitizedCode,
                    BaseQuantity = quantity,
                    Quantity = quantity,
                    ThicknessMm = thickness,
                    Material = material,
                    Operation = operation,
                    ColorCode = color,
                    WeldProcess = NormalizeWeld(weld),
                    Description = "",
                    SourcePath = path,
                    Configuration = configuration ?? "",
                    HasBends = hasBends,
                    HasTappedHoles = hasTappedHoles,
                    HasCountersink = hasCountersink,
                    TappedHoleWizardCount = tappedHoleWizardCount,
                    TappedHoleTypes = string.Join(", ", tappedHoleTypes.OrderBy(x => x)),
                    FlatPatternCount = flatPatterns,
                    IsLaserTagged = isLaserTagged,
                    IsWeldTagged = isWeldTagged,
                    LaserTagProperty = isLaserTagged ? laserProperty : "",
                    DrawingPath = FindDrawingPath(path, sanitizedCode),
                    ProductionDefinition = productionDefinition
                };

                if (string.IsNullOrWhiteSpace(row.PartCode)) row.Error = "Parça kodu bulunamadı.";
                else if (!row.ThicknessMm.HasValue) row.Error = "Sac kalınlığı okunamadı.";
                else if (flatPatterns > 1) row.Error = "Birden fazla FlatPattern bulundu (çok gövdeli sac). Manuel kontrol gerekli.";

                return row;
            }
            finally
            {
                if (!openedByUs && switchedConfiguration && model != null && !string.IsNullOrWhiteSpace(originalConfiguration))
                {
                    try { model.ShowConfiguration2(originalConfiguration); } catch { }
                }

                if (model != null)
                    ComUtil.CloseTransientDocument(_swApp, model, path, openedByUs, wasVisibleBefore);

                if (openedByUs || !wasVisibleBefore) ComUtil.Release(model);
            }
        }

        private static bool IsAnyBodySheetMetal(ModelDoc2 model)
        {
            try
            {
                var partDoc = model as PartDoc;
                object bodiesObject = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true);
                var bodies = bodiesObject as object[];
                if (bodies == null) return false;
                foreach (object item in bodies)
                {
                    var body = item as Body2;
                    try
                    {
                        if (body != null && body.IsSheetMetal()) return true;
                    }
                    finally { ReleaseCom(body); }
                }
            }
            catch { }
            return false;
        }

        private static bool HasSheetMetalFeatureQuick(ModelDoc2 model)
        {
            Feature current = null;
            try { current = model.FirstFeature() as Feature; } catch { }
            return FindSheetMetalFeature(current, false);
        }

        private static bool FindSheetMetalFeature(Feature first, bool subFeatures)
        {
            Feature current = first;
            while (current != null)
            {
                Feature next = null;
                Feature sub = null;
                bool found = false;
                try
                {
                    string type = SafeTypeName(current);
                    if (IsSheetMetalType(type)) found = true;
                    if (!found)
                    {
                        try { sub = current.GetFirstSubFeature() as Feature; } catch { }
                        if (sub != null) found = FindSheetMetalFeature(sub, true);
                    }
                    if (!found)
                    {
                        try { next = subFeatures ? current.GetNextSubFeature() as Feature : current.GetNextFeature() as Feature; } catch { }
                    }
                }
                finally { ReleaseCom(current); }
                if (found) return true;
                current = next;
            }
            return false;
        }

        private static bool IsTappedHoleWizardType(string enumName)
        {
            if (string.IsNullOrWhiteSpace(enumName)) return false;
            return enumName.StartsWith("swTap", StringComparison.OrdinalIgnoreCase)
                || enumName.StartsWith("swPipeTap", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindDrawingPath(string partPath, string partCode)
        {
            try
            {
                string folder = Path.GetDirectoryName(partPath) ?? "";
                string sourceName = Path.GetFileNameWithoutExtension(partPath);
                string bySource = Path.Combine(folder, sourceName + ".SLDDRW");
                if (File.Exists(bySource)) return bySource;
                if (!string.IsNullOrWhiteSpace(partCode))
                {
                    string byCode = Path.Combine(folder, partCode + ".SLDDRW");
                    if (File.Exists(byCode)) return byCode;
                }
            }
            catch { }
            return "";
        }


        private static string ResolvePartCode(string sourceFileCode, string explicitPartCode)
        {
            string fileCode = SanitizeCode(sourceFileCode);
            string propertyCode = SanitizeCode(explicitPartCode);

            // Firma içi kodlar örn. PBC100051070, RCRC00041016, DRLC00021008.
            // Böyle bir kod dosya adındaysa, yanlışlıkla başka bir "Code" özelliğine düşmeyiz.
            if (LooksLikeInternalPartCode(fileCode)) return fileCode;

            if (!string.IsNullOrWhiteSpace(propertyCode)) return propertyCode;
            return fileCode;
        }

        private static bool LooksLikeInternalPartCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim().ToUpperInvariant();
            // 2-8 harf + en az 6 rakam; isteğe bağlı revizyon/sonek.
            // GL6-#01#1S71-S240408 gibi tedarikçi/model kodlarını özellikle eşleştirmez.
            return Regex.IsMatch(v, @"^[A-Z]{2,8}\d{6,14}(?:[-_][A-Z0-9]+)*$", RegexOptions.CultureInvariant);
        }

        private static string NormalizeWeld(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value.Trim().ToUpperInvariant();
            if (v == "1" || v == "TRUE" || v == "EVET" || v == "VAR") return "VAR";
            if (v == "0" || v == "FALSE" || v == "HAYIR" || v == "YOK") return "YOK";
            return value.Trim();
        }

        private static double? ParseMillimeters(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = text.ToUpperInvariant().Replace("MM", "").Trim().Replace(',', '.');
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) return v;
            return null;
        }

        private static string SanitizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid) code = code.Replace(c, '_');
            return code.Trim();
        }

        private static bool IsSheetMetalType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            return type.Equals("SheetMetal", StringComparison.OrdinalIgnoreCase)
                || type.Equals("SMBaseFlange", StringComparison.OrdinalIgnoreCase)
                || type.Equals("BaseFlange", StringComparison.OrdinalIgnoreCase)
                || type.Equals("FlatPattern", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("SM", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeTypeName(Feature f)
        {
            try { return f.GetTypeName2() ?? ""; } catch { return ""; }
        }

        private static void WalkFeatures(Feature first, Action<Feature> visitor)
        {
            Feature current = first;
            while (current != null)
            {
                Feature next = null;
                Feature sub = null;
                try
                {
                    try { next = current.GetNextFeature() as Feature; } catch { }
                    try { sub = current.GetFirstSubFeature() as Feature; } catch { }
                    visitor(current);
                    if (sub != null) WalkSubFeatures(sub, visitor);
                }
                finally { ReleaseCom(current); }
                current = next;
            }
        }

        private static void WalkSubFeatures(Feature first, Action<Feature> visitor)
        {
            Feature current = first;
            while (current != null)
            {
                Feature next = null;
                Feature sub = null;
                try
                {
                    try { next = current.GetNextSubFeature() as Feature; } catch { }
                    try { sub = current.GetFirstSubFeature() as Feature; } catch { }
                    visitor(current);
                    if (sub != null) WalkSubFeatures(sub, visitor);
                }
                finally { ReleaseCom(current); }
                current = next;
            }
        }

        private static void ReleaseCom(object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); } catch { }
        }
    }
}
