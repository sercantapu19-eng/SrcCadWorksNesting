using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace SrcCadWorks.Services
{
    [Serializable]
    public sealed class WeldMetadataCacheStore
    {
        public int Version { get; set; } = 3;
        public List<WeldMetadataCacheEntry> Entries { get; set; } = new List<WeldMetadataCacheEntry>();
    }

    [Serializable]
    public sealed class WeldMetadataCacheEntry
    {
        public string Path { get; set; }
        public string Configuration { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public long Length { get; set; }
        public string PartCode { get; set; }
        public string ProductDescription { get; set; }
        public string OperationType { get; set; }
        public string Material { get; set; }
        public string Thickness { get; set; }
        public string ColorCode { get; set; }
        public string WeldProcess { get; set; }
        public string MassText { get; set; }
        public string DrawingPath { get; set; }
        public string SawProfileType { get; set; }
        public bool? SawHasHoleWizard { get; set; }
    }

    // V1.12.11: İşlem Tipi eşlemesi düzeltildiği için eski cache bilinçli olarak bırakılır.
    // Kaynak taramasında her benzersiz dosyayı yeniden OpenDoc6 ile açmamak için
    // dosya+konfigürasyon bazlı kalıcı hafif metadata önbelleği. Dosya tarihi/boyutu değişirse
    // kayıt otomatik geçersiz olur. Böylece ilk okumadan sonraki taramalarda SOLIDWORKS belge
    // aç/kapat döngüsü büyük ölçüde ortadan kalkar.
    internal sealed class WeldMetadataCache
    {
        private readonly string _filePath;
        private readonly Dictionary<string, WeldMetadataCacheEntry> _entries;
        private bool _dirty;

        public WeldMetadataCache()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "cache");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "weld-metadata-v3.xml");
            _entries = Load();
        }

        public bool TryGet(string path, string configuration, out ComponentMetadata metadata)
        {
            metadata = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            WeldMetadataCacheEntry e;
            if (!_entries.TryGetValue(Key(path, configuration), out e) || e == null) return false;

            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch { return false; }

            if (e.LastWriteUtcTicks != fi.LastWriteTimeUtc.Ticks || e.Length != fi.Length)
            {
                _entries.Remove(Key(path, configuration));
                _dirty = true;
                return false;
            }

            metadata = new ComponentMetadata
            {
                Path = path,
                Configuration = configuration ?? "",
                PartCode = e.PartCode ?? "",
                ProductDescription = e.ProductDescription ?? "",
                OperationType = e.OperationType ?? "",
                Material = e.Material ?? "",
                Thickness = e.Thickness ?? "",
                ColorCode = e.ColorCode ?? "",
                WeldProcess = e.WeldProcess ?? "",
                MassText = NormalizeMassText(e.MassText),
                SawProfileType = e.SawProfileType,
                SawHasHoleWizard = e.SawHasHoleWizard,
                DrawingPath = ResolveDrawingPath(path, e.PartCode, e.DrawingPath)
            };
            return true;
        }

        public void Put(ComponentMetadata metadata)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Path) || !File.Exists(metadata.Path)) return;
            FileInfo fi;
            try { fi = new FileInfo(metadata.Path); }
            catch { return; }

            _entries[Key(metadata.Path, metadata.Configuration)] = new WeldMetadataCacheEntry
            {
                Path = metadata.Path,
                Configuration = metadata.Configuration ?? "",
                LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                Length = fi.Length,
                PartCode = metadata.PartCode ?? "",
                ProductDescription = metadata.ProductDescription ?? "",
                OperationType = metadata.OperationType ?? "",
                Material = metadata.Material ?? "",
                Thickness = metadata.Thickness ?? "",
                ColorCode = metadata.ColorCode ?? "",
                WeldProcess = metadata.WeldProcess ?? "",
                MassText = NormalizeMassText(metadata.MassText),
                SawProfileType = metadata.SawProfileType,
                SawHasHoleWizard = metadata.SawHasHoleWizard,
                DrawingPath = metadata.DrawingPath ?? ""
            };
            _dirty = true;
        }

        public void Save()
        {
            if (!_dirty) return;
            try
            {
                var store = new WeldMetadataCacheStore { Version = 3, Entries = _entries.Values.ToList() };
                string temp = _filePath + ".tmp";
                using (var stream = File.Create(temp))
                    new XmlSerializer(typeof(WeldMetadataCacheStore)).Serialize(stream, store);
                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(temp, _filePath);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Logger.Info("Kaynak metadata önbelleği yazılamadı: " + ex.Message);
            }
        }

        private Dictionary<string, WeldMetadataCacheEntry> Load()
        {
            var result = new Dictionary<string, WeldMetadataCacheEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(_filePath)) return result;
                WeldMetadataCacheStore store;
                using (var stream = File.OpenRead(_filePath))
                    store = (WeldMetadataCacheStore)new XmlSerializer(typeof(WeldMetadataCacheStore)).Deserialize(stream);
                if (store == null || store.Version != 3) return result;
                foreach (var e in store.Entries ?? new List<WeldMetadataCacheEntry>())
                    if (e != null && !string.IsNullOrWhiteSpace(e.Path)) result[Key(e.Path, e.Configuration)] = e;
            }
            catch (Exception ex)
            {
                Logger.Info("Kaynak metadata önbelleği okunamadı; yeni cache oluşturulacak: " + ex.Message);
            }
            return result;
        }

        private static string NormalizeMassText(string value)
        {
            double grams;
            if (MassValueFormatter.TryParseGrams(value, true, out grams))
                return MassValueFormatter.FormatGrams(grams);
            return (value ?? "").Trim();
        }

        private static string ResolveDrawingPath(string modelPath, string partCode, string cached)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached)) return cached;
                string folder = Path.GetDirectoryName(modelPath) ?? "";
                string sourceName = Path.GetFileNameWithoutExtension(modelPath) ?? "";
                string bySource = Path.Combine(folder, sourceName + ".SLDDRW");
                if (File.Exists(bySource)) return bySource;
                if (!string.IsNullOrWhiteSpace(partCode))
                {
                    string byCode = Path.Combine(folder, partCode.Trim() + ".SLDDRW");
                    if (File.Exists(byCode)) return byCode;
                }
                return "";
            }
            catch { return ""; }
        }

        private static string Key(string path, string configuration)
        {
            return (path ?? "").Trim().ToUpperInvariant() + "|" + (configuration ?? "").Trim().ToUpperInvariant();
        }
    }
}
