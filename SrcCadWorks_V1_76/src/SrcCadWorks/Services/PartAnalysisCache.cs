using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    [Serializable]
    public sealed class PartAnalysisCacheStore
    {
        public int Version { get; set; } = 10;
        public List<PartAnalysisCacheEntry> Entries { get; set; } = new List<PartAnalysisCacheEntry>();
    }

    [Serializable]
    public sealed class PartAnalysisCacheEntry
    {
        public string Path { get; set; }
        public string Configuration { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public long Length { get; set; }
        public string SourceSha256 { get; set; }
        public bool IsSheetMetal { get; set; }
        public string PartCode { get; set; }
        public bool HasThickness { get; set; }
        public double ThicknessMm { get; set; }
        public string Material { get; set; }
        public string Operation { get; set; }
        public string ColorCode { get; set; }
        public string WeldProcess { get; set; }
        public string Description { get; set; }
        public bool HasBends { get; set; }
        public bool HasTappedHoles { get; set; }
        public bool HasCountersink { get; set; }
        public int TappedHoleWizardCount { get; set; }
        public string TappedHoleTypes { get; set; }
        public int FlatPatternCount { get; set; }
        public string Error { get; set; }
        public bool IsLaserTagged { get; set; }
        public bool IsWeldTagged { get; set; }
        public string LaserTagProperty { get; set; }
        public string ProductionDefinition { get; set; }
    }

    internal sealed class PartAnalysisCache
    {
        private readonly string _filePath;
        private readonly Dictionary<string, PartAnalysisCacheEntry> _entries;
        private bool _dirty;

        public PartAnalysisCache()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "cache");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "part-analysis-v10.xml");
            _entries = Load();
        }

        public bool TryGet(string path, string configuration, int quantity, out LaserPartRow row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            string key = Key(path, configuration);
            PartAnalysisCacheEntry e;
            if (!_entries.TryGetValue(key, out e)) return false;

            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch { return false; }
            if (e.LastWriteUtcTicks != fi.LastWriteTimeUtc.Ticks || e.Length != fi.Length) return false;
            // Tarih ve boyut aynı kalsa bile PDM/ARGE sırasında dosya içeriği değişebilir.
            // Hash uyuşmadan analiz cache'i kullanılmaz.
            string currentHash = OutputProvenanceStore.ComputeFileSha256(path);
            if (string.IsNullOrWhiteSpace(currentHash) ||
                !string.Equals(e.SourceSha256 ?? "", currentHash, StringComparison.OrdinalIgnoreCase)) return false;

            if (!e.IsSheetMetal) return true; // Geçerli negatif cache.
            row = new LaserPartRow
            {
                Selected = true,
                PartCode = e.PartCode,
                BaseQuantity = quantity,
                Quantity = quantity,
                ThicknessMm = e.HasThickness ? (double?)e.ThicknessMm : null,
                Material = e.Material,
                Operation = e.Operation,
                ColorCode = e.ColorCode,
                WeldProcess = e.WeldProcess,
                Description = e.Description,
                SourcePath = path,
                Configuration = configuration ?? "",
                HasBends = e.HasBends,
                HasTappedHoles = e.HasTappedHoles,
                HasCountersink = e.HasCountersink,
                TappedHoleWizardCount = e.TappedHoleWizardCount,
                TappedHoleTypes = e.TappedHoleTypes,
                FlatPatternCount = e.FlatPatternCount,
                Error = e.Error,
                IsLaserTagged = e.IsLaserTagged,
                IsWeldTagged = e.IsWeldTagged,
                LaserTagProperty = e.LaserTagProperty,
                ProductionDefinition = e.ProductionDefinition,
                DrawingPath = FindDrawingPath(path, e.PartCode)
            };
            return true;
        }

        public void Put(string path, string configuration, LaserPartRow row)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { return; }
            var e = new PartAnalysisCacheEntry
            {
                Path = path,
                Configuration = configuration ?? "",
                LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                Length = fi.Length,
                SourceSha256 = OutputProvenanceStore.ComputeFileSha256(path),
                IsSheetMetal = row != null
            };
            if (row != null)
            {
                e.PartCode = row.PartCode;
                e.HasThickness = row.ThicknessMm.HasValue;
                e.ThicknessMm = row.ThicknessMm ?? 0;
                e.Material = row.Material;
                e.Operation = row.Operation;
                e.ColorCode = row.ColorCode;
                e.WeldProcess = row.WeldProcess;
                e.Description = row.Description;
                e.HasBends = row.HasBends;
                e.HasTappedHoles = row.HasTappedHoles;
                e.HasCountersink = row.HasCountersink;
                e.TappedHoleWizardCount = row.TappedHoleWizardCount;
                e.TappedHoleTypes = row.TappedHoleTypes;
                e.FlatPatternCount = row.FlatPatternCount;
                e.Error = row.Error;
                e.IsLaserTagged = row.IsLaserTagged;
                e.IsWeldTagged = row.IsWeldTagged;
                e.LaserTagProperty = row.LaserTagProperty;
                e.ProductionDefinition = row.ProductionDefinition;
            }
            _entries[Key(path, configuration)] = e;
            _dirty = true;
        }

        public void Save()
        {
            if (!_dirty) return;
            try
            {
                var store = new PartAnalysisCacheStore { Version = 10, Entries = _entries.Values.ToList() };
                string temp = _filePath + ".tmp";
                using (var stream = File.Create(temp)) new XmlSerializer(typeof(PartAnalysisCacheStore)).Serialize(stream, store);
                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(temp, _filePath);
                _dirty = false;
            }
            catch (Exception ex) { Logger.Info("Analiz önbelleği yazılamadı: " + ex.Message); }
        }

        private Dictionary<string, PartAnalysisCacheEntry> Load()
        {
            var result = new Dictionary<string, PartAnalysisCacheEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(_filePath)) return result;
                PartAnalysisCacheStore store;
                using (var stream = File.OpenRead(_filePath)) store = (PartAnalysisCacheStore)new XmlSerializer(typeof(PartAnalysisCacheStore)).Deserialize(stream);
                if (store == null || store.Version != 10) return result;
                foreach (var e in store.Entries ?? new List<PartAnalysisCacheEntry>())
                    if (e != null && !string.IsNullOrWhiteSpace(e.Path)) result[Key(e.Path, e.Configuration)] = e;
            }
            catch (Exception ex) { Logger.Info("Analiz önbelleği okunamadı; yeni cache oluşturulacak: " + ex.Message); }
            return result;
        }

        private static string Key(string path, string configuration) => (path ?? "").Trim().ToUpperInvariant() + "|" + (configuration ?? "").Trim().ToUpperInvariant();

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
    }
}
