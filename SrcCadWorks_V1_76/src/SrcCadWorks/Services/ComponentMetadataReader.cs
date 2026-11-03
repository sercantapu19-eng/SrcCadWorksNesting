using System;
using System.Globalization;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.Services
{
    internal sealed class ComponentMetadata
    {
        public string Path { get; set; }
        public string Configuration { get; set; }
        public string PartCode { get; set; }
        public string SawProfileType { get; set; }
        public bool? SawHasHoleWizard { get; set; }
        public string ProductDescription { get; set; }
        public string OperationType { get; set; }
        public string Material { get; set; }
        public string Thickness { get; set; }
        public string ColorCode { get; set; }
        public string WeldProcess { get; set; }
        public string MassText { get; set; }
        public string DrawingPath { get; set; }
    }

    internal sealed class ComponentMetadataReader
    {
        private readonly SldWorks _swApp;
        public ComponentMetadataReader(SldWorks swApp) => _swApp = swApp;

        public ComponentMetadata Read(Component2 component)
        {
            if (component == null) return null;
            string path = Safe(() => component.GetPathName(), "");
            string cfg = Safe(() => component.ReferencedConfiguration, "");
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Read(path, cfg, component);
        }

        // Montaj Görüntüleme için salt-okunur/hafif yol. Lightweight bileşeni çözümlemez
        // ve OpenDoc6 ile yeni bir belge açmaz. Böylece aktif belge değişmez ve SOLIDWORKS CAM
        // "değişiklikler kaydedilsin mi" pencereleri oluşmaz. Yalnız zaten bellekte olan
        // ModelDoc2 üzerinden metadata okur; yüklenmemiş bileşenlerde null dönebilir.
        public ComponentMetadata ReadLoadedOnly(string path, string configuration, Component2 component = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            ModelDoc2 model = null;
            if (component != null)
            {
                try { model = component.GetModelDoc2() as ModelDoc2; } catch { }
            }
            if (model == null)
            {
                try { model = _swApp.GetOpenDocumentByName(path) as ModelDoc2; } catch { }
            }
            if (model == null) return null;
            return BuildMetadata(model, path, configuration);
        }

        public ComponentMetadata Read(string path, string configuration, Component2 component = null, Func<bool> shouldCancel = null, bool readSawDetails = false)
        {
            return ReadInternal(path, configuration, component, shouldCancel, readSawDetails, false);
        }

        // Montaj Hazırla'da belgeye özel (dosya adı) kimliği koru. STEP'ten içeri alınmış
        // standart parçalar, model içinde taşınan tedarikçi "Part Number/Number" alanlarıyla
        // adlandırılmış olabilir. Bu alanlar yerel belge adı yerine BOM kodu olarak kullanılmamalı.
        // Yalnız SrcCadWorks'e ait açık Ürün Kodu/Parça Kodu alanları varsa onları koru;
        // aksi halde SLDPRT/SLDASM dosya adını kullan.
        public ComponentMetadata ReadForAssemblyPreparation(string path, string configuration, Component2 component = null, Func<bool> shouldCancel = null)
        {
            return ReadInternal(path, configuration, component, shouldCancel, false, true);
        }

        private ComponentMetadata ReadInternal(string path, string configuration, Component2 component,
            Func<bool> shouldCancel, bool readSawDetails, bool preferDocumentName)
        {
            if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Metadata okuma ESC ile iptal edildi.");
            if (string.IsNullOrWhiteSpace(path)) return null;
            ModelDoc2 model = null;
            bool openedByUs = false;
            using (var foregroundGuard = ForegroundWindowGuard.CaptureExternal())
            try
            {
                if (component != null)
                {
                    try { model = component.GetModelDoc2() as ModelDoc2; } catch { }
                }
                if (model == null)
                {
                    try { model = _swApp.GetOpenDocumentByName(path) as ModelDoc2; } catch { }
                }
                if (model == null && File.Exists(path))
                {
                    if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Metadata okuma ESC ile iptal edildi.");
                    int errors = 0, warnings = 0;
                    int type = DocumentTypeFromPath(path);
                    model = _swApp.OpenDoc6(path, type,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        configuration ?? "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = model != null;
                    if (openedByUs) { try { ((dynamic)model).Visible = false; } catch { } }
                    foregroundGuard.RestoreIfSolidWorksStoleFocus();
                    if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Metadata okuma ESC ile iptal edildi.");
                }

                if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Metadata okuma ESC ile iptal edildi.");
                if (model == null) return BasicMetadata(path, configuration);
                var metadata = BuildMetadata(model, path, configuration, preferDocumentName);
                if (readSawDetails)
                {
                    metadata.SawHasHoleWizard = false;
                    if (ManufacturingOperationClassifier.IsSaw(metadata.OperationType))
                    {
                        string original = model.ConfigurationManager.ActiveConfiguration.Name ?? "";
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(configuration) && !string.Equals(original, configuration, StringComparison.OrdinalIgnoreCase) &&
                                !model.ShowConfiguration2(configuration))
                                throw new InvalidOperationException("Profil konfigürasyonu açılamadı: " + configuration);
                            var details = new WeldmentProfileReader(_swApp).ReadSawDetailsFromModel(model);
                            metadata.SawProfileType = details.ProfileType;
                            metadata.SawHasHoleWizard = details.HasHoleWizard;
                        }
                        finally { if (!string.IsNullOrWhiteSpace(original)) model.ShowConfiguration2(original); }
                    }
                }
                return metadata;
            }
            finally
            {
                if (openedByUs && model != null)
                {
                    // Tek seferlik CloseDoc yerine ortak sessiz kapatma yolunu kullan. ModalDialogGuard
                    // Kaynak Hazırla sırasında aktif olduğundan SOLIDWORKS/CAM kaydetme soruları otomatik HAYIR
                    // ile geçilir; CloseOpenedBySrc kapanmayı doğrulayıp kısa aralıklarla yeniden dener.
                    try { ComUtil.CloseOpenedBySrc(_swApp, model, path); } catch { }
                    ComUtil.Release(model);
                }
            }
        }

        private static ComponentMetadata BasicMetadata(string path, string configuration)
        {
            string baseName = Path.GetFileNameWithoutExtension(path) ?? "";
            return new ComponentMetadata
            {
                Path = path,
                Configuration = configuration ?? "",
                PartCode = baseName,
                ProductDescription = "",
                OperationType = "",
                Material = "",
                Thickness = "",
                ColorCode = "",
                WeldProcess = "",
                MassText = "",
                DrawingPath = FindDrawing(path, baseName)
            };
        }

        private static ComponentMetadata BuildMetadata(ModelDoc2 model, string path, string configuration, bool preferDocumentName = false)
        {
            var m = BasicMetadata(path, configuration);
            if (model == null) return m;
            string baseName = Path.GetFileNameWithoutExtension(path) ?? "";
            string companyPartCode = CustomPropertyReader.First(model, configuration,
                "Ürün Kodu", "Urun Kodu", "ÜRÜN KODU", "Parça Kodu", "Parca Kodu");
            if (preferDocumentName)
            {
                // Imported STEP documents can carry vendor-provided Part Number/Number fields
                // such as "91294A208_NO THREADS_...". The saved SOLIDWORKS document name is
                // the occurrence identity in Montaj Hazırla unless an explicit company code exists.
                m.PartCode = ManufacturingIdentity.AssemblyPreparationPartCode(path, companyPartCode);
            }
            else
            {
                m.PartCode = FirstOrFallback(FirstOrFallback(companyPartCode,
                    CustomPropertyReader.First(model, configuration, "Part Number", "Number")), baseName);
            }
            m.ProductDescription = CustomPropertyReader.First(model, configuration,
                "Ürün Tanımı", "Urun Tanimi", "ÜRÜN TANIMI", "Ürün Tanimi", "Description", "Tanım", "Tanim");
            m.OperationType = CustomPropertyReader.OperationType(model, configuration);
            m.Material = CustomPropertyReader.First(model, configuration,
                "Malzeme", "Material", "MALZEME");
            m.Thickness = CustomPropertyReader.First(model, configuration,
                "Kalınlık", "Kalinlik", "KALINLIK", "Thickness");
            m.ColorCode = CustomPropertyReader.First(model, configuration,
                "Boya Tipi", "Renk Kodu", "RenkKodu", "RAL Kodu", "RAL", "Color Code", "Renk", "Color");
            m.WeldProcess = CustomPropertyReader.First(model, configuration,
                "Kaynak İşlemi", "Kaynak Islemi", "KAYNAK İŞLEMİ", "KAYNAK ISLEMI", "Weld Process");
            m.MassText = ReadMass(model, configuration);
            m.DrawingPath = FindDrawing(path, m.PartCode);
            return m;
        }

        private static string ReadMass(ModelDoc2 model, string configuration)
        {
            // Önce gram olarak tanımlanan şirket özel özelliklerini oku. Bu alanlarda birim yazmasa
            // bile değer gram kabul edilir. Nokta/virgül ondalık ayracı güvenli biçimde normalize edilir.
            string grams = CustomPropertyReader.First(model, configuration,
                "Ağırlık : g", "Ağırlık", "Agirlik", "Kütle", "Kutle");
            if (!string.IsNullOrWhiteSpace(grams))
            {
                double value;
                if (MassValueFormatter.TryParseGrams(grams, true, out value))
                    return MassValueFormatter.FormatGrams(value);
                return grams.Trim();
            }

            // Genel SOLIDWORKS Mass/SW-Mass alanı bazen kg/g birimiyle döner. Birim varsa dönüştür,
            // yoksa şirket kullanımına uygun olarak gram kabul et.
            string generic = CustomPropertyReader.First(model, configuration, "Mass", "SW-Mass");
            if (!string.IsNullOrWhiteSpace(generic))
            {
                double value;
                if (MassValueFormatter.TryParseGrams(generic, true, out value))
                    return MassValueFormatter.FormatGrams(value);
                return generic.Trim();
            }
            return "";
        }

        private static string FindDrawing(string modelPath, string partCode)
        {
            try
            {
                string folder = Path.GetDirectoryName(modelPath) ?? "";
                string sourceName = Path.GetFileNameWithoutExtension(modelPath) ?? "";
                string bySource = Path.Combine(folder, sourceName + ".SLDDRW");
                if (File.Exists(bySource)) return bySource;
                if (!string.IsNullOrWhiteSpace(partCode))
                {
                    string byCode = Path.Combine(folder, partCode.Trim() + ".SLDDRW");
                    if (File.Exists(byCode)) return byCode;
                }
            }
            catch { }
            return "";
        }

        private static int DocumentTypeFromPath(string path)
        {
            string ext = Path.GetExtension(path) ?? "";
            if (ext.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocASSEMBLY;
            if (ext.Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocDRAWING;
            return (int)swDocumentTypes_e.swDocPART;
        }

        private static string FirstOrFallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? (fallback ?? "") : value.Trim();
        private static T Safe<T>(Func<T> get, T fallback) { try { return get(); } catch { return fallback; } }
    }
    internal static class MassValueFormatter
    {
        private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

        public static string FormatGrams(double grams)
        {
            if (double.IsNaN(grams) || double.IsInfinity(grams)) return "";
            return grams.ToString("0.##", Tr);
        }

        public static bool TryParseGrams(string text, bool assumeGramsWhenUnitMissing, out double grams)
        {
            grams = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string raw = text.Trim();
            string lower = raw.ToLowerInvariant();
            double factor = 1.0;
            if (lower.Contains("kg")) factor = 1000.0;
            else if (lower.Contains("mg")) factor = 0.001;
            else if (lower.Contains("lb") || lower.Contains("lbs")) factor = 453.59237;
            else if (lower.Contains("gram") || lower.Contains(" gr") || lower.EndsWith("g")) factor = 1.0;
            else if (!assumeGramsWhenUnitMissing) return false;

            var chars = new System.Text.StringBuilder();
            foreach (char ch in raw)
            {
                if (char.IsDigit(ch) || ch == '-' || ch == '+' || ch == ',' || ch == '.') chars.Append(ch);
            }
            string n = chars.ToString();
            if (string.IsNullOrWhiteSpace(n)) return false;

            int lastComma = n.LastIndexOf(',');
            int lastDot = n.LastIndexOf('.');
            char decimalSep = '\0';
            if (lastComma >= 0 && lastDot >= 0) decimalSep = lastComma > lastDot ? ',' : '.';
            else if (lastComma >= 0) decimalSep = ',';
            else if (lastDot >= 0) decimalSep = '.';

            if (decimalSep != '\0')
            {
                var normalized = new System.Text.StringBuilder();
                for (int i = 0; i < n.Length; i++)
                {
                    char ch = n[i];
                    if (char.IsDigit(ch) || ch == '-' || ch == '+') normalized.Append(ch);
                    else if (ch == decimalSep && i == n.LastIndexOf(decimalSep)) normalized.Append('.');
                }
                n = normalized.ToString();
            }

            double value;
            if (!double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            grams = value * factor;
            return true;
        }
    }

}
