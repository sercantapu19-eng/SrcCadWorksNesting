using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// Montaj Hazırla tarayıcısı. Hızlı üst-seviye modunda yalnız doğrudan alt montajları
    /// okur; ayrıntılı modda FeatureManager benzeri tam hiyerarşiyi tarar. Kaynak işlem
    /// tipi filtresi uygulamaz.
    /// </summary>
    internal sealed class AssemblyPreparationScanner
    {
        private readonly SldWorks _swApp;
        private readonly ComponentMetadataReader _metadataReader;
        private readonly Dictionary<string, ComponentMetadata> _metadataCache =
            new Dictionary<string, ComponentMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly List<OccurrenceRecord> _records = new List<OccurrenceRecord>();
        // Maps occurrence record indexes to the number of children captured during traversal.
        // Repeated instances of the same subassembly can expose different child data if one
        // occurrence is not fully resolved. When collapsing identical instances into a BOM row,
        // use the occurrence with the most captured children as the representative.
        private Dictionary<int, int> _childRecordCounts = new Dictionary<int, int>();
        private WeldScanResult _result;
        private Action<int, int, string> _progress;
        private Func<bool> _shouldCancel;
        private readonly bool _includeHidden;
        private readonly bool _includePassive;
        private readonly bool _topLevelMontajOnly;
        private int _progressTotal;
        private TimeSpan _metadataReadElapsed = TimeSpan.Zero;
        private int _metadataReadCalls;
        private readonly Dictionary<string, ComponentMetadata> _persistentMetadataCache =
            new Dictionary<string, ComponentMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _persistentMetadataOrder = new List<string>();
        private string _persistentMetadataCachePath;
        private bool _persistentMetadataCacheDirty;
        private int _persistentMetadataCacheHits;
        private int _persistentMetadataCacheMisses;
        private const int PersistentMetadataCacheLimit = 6000;

        public AssemblyPreparationScanner(SldWorks swApp, bool includeHidden = true, bool includePassive = false,
            bool topLevelMontajOnly = false)
        {
            _swApp = swApp;
            _includeHidden = includeHidden;
            _includePassive = includePassive;
            _topLevelMontajOnly = topLevelMontajOnly;
            _metadataReader = new ComponentMetadataReader(swApp);
        }

        public WeldScanResult Scan(Action<int, int, string> progress = null, Func<bool> shouldCancel = null)
        {
            _shouldCancel = shouldCancel;
            ThrowIfCancelled();
            _result = new WeldScanResult();
            _records.Clear();
            _childRecordCounts.Clear();
            _metadataCache.Clear();
            _progress = progress;
            _metadataReadElapsed = TimeSpan.Zero;
            _metadataReadCalls = 0;
            _persistentMetadataCacheHits = 0;
            _persistentMetadataCacheMisses = 0;
            _persistentMetadataCacheDirty = false;
            var scanWatch = Stopwatch.StartNew();
            LoadPersistentMetadataCache();

            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Aktif belge bir montaj olmalıdır.");

            Component2 root = null;
            try
            {
                root = model.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) as Component2;
                if (root == null) throw new InvalidOperationException("Aktif montajın kök bileşeni okunamadı.");

                // Varsayılan üst-seviye modunda GetComponents(false) kullanılmaz: SOLIDWORKS
                // bu çağrıda binlerce alt bileşeni materyalize edebilir. Yalnız kökün doğrudan
                // çocuklarını saymak ve taramak, pencere açılışındaki uzun beklemeyi önler.
                if (_topLevelMontajOnly)
                {
                    object[] directChildren = null;
                    try { directChildren = root.GetChildren() as object[]; } catch { }
                    int directAssemblyCount = 0;
                    if (directChildren != null)
                    {
                        foreach (object obj in directChildren)
                        {
                            var child = obj as Component2;
                            if (child != null)
                            {
                                try
                                {
                                    string childPath = Safe(() => child.GetPathName(), "");
                                    if (IsAssemblyOccurrence(child, childPath)) directAssemblyCount++;
                                }
                                catch { }
                            }
                            ComUtil.Release(obj);
                        }
                    }
                    _progressTotal = Math.Max(1, directAssemblyCount + 1);
                }
                else
                {
                    try
                    {
                        object raw = ((AssemblyDoc)model).GetComponents(false);
                        var components = raw as object[];
                        _progressTotal = components == null ? 1 : Math.Max(1, components.Length + 1);
                        if (components != null)
                            foreach (object obj in components) ComUtil.Release(obj);
                    }
                    catch { _progressTotal = 1; }
                }

                string rootPath = Safe(() => root.GetPathName(), Safe(() => model.GetPathName(), ""));
                string rootCfg = Safe(() => root.ReferencedConfiguration,
                    Safe(() => model.ConfigurationManager.ActiveConfiguration.Name, ""));
                string rootName = Safe(() => root.Name2, Path.GetFileNameWithoutExtension(rootPath));
                var rootMeta = ReadMetadata(root, rootPath, rootCfg, rootName);
                var rootRecord = new OccurrenceRecord
                {
                    Index = 0,
                    ParentRecordIndex = -1,
                    Path = rootPath,
                    Configuration = rootCfg,
                    Name = rootName,
                    SelectId = "",
                    Meta = rootMeta,
                    IsAssembly = true
                };
                _records.Add(rootRecord);
                _result.OccurrenceCount = 1;
                _progress?.Invoke(1, _progressTotal, rootMeta.PartCode);

                TraverseChildren(root, rootRecord.Index);

                // Index children by parent occurrence. When identical subassembly occurrences
                // are collapsed into one BOM row, this allows AppendChildren to choose the
                // occurrence whose hierarchy was actually captured most completely.
                _childRecordCounts = _records
                    .GroupBy(r => r.ParentRecordIndex)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Her kayıt ebeveyninin gerçek kayıt indeksine bağlıdır. Parça kodu veya
                // SOLIDWORKS seçim metni ebeveyn-çocuk ilişkisini belirlemek için kullanılmaz.
                var globalOccurrences = _records
                    .GroupBy(r => r.IdentityKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var registeredAssemblyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // The active document is a container, not a BOM item. Its first-level
                // children must be numbered 1, 2, 3...; do not emit the active assembly
                // name as row 1 or treat it as a Montaj Hazırla output target.
                var rootContainer = ToRow(rootRecord, "", 1, true, false, -1, "", new List<string>());
                rootContainer.TotalQuantity = 1;

                AppendChildren(rootContainer, rootRecord, globalOccurrences, registeredAssemblyIds);
                _result.UniqueWeldCount = registeredAssemblyIds.Count;
                if (_result.Rows.Count == 0)
                    _result.Warnings.Add("Aktif montajda bileşen bulunamadı.");
                return _result;
            }
            finally
            {
                try { SavePersistentMetadataCache(); } catch (Exception ex) { Logger.Info("Montaj metadata önbelleği kaydedilemedi: " + ex.Message); }
                scanWatch.Stop();
                Logger.Info("Montaj tarama performansı | Toplam=" + scanWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " sn" +
                    " | Metadata okuma=" + _metadataReadElapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " sn" +
                    " | Metadata disk-cache hit/miss=" + _persistentMetadataCacheHits + "/" + _persistentMetadataCacheMisses +
                    " | Bellek cache hit/miss=" + (_result == null ? 0 : _result.MetadataCacheHits) + "/" + (_result == null ? 0 : _result.MetadataCacheMisses) +
                    " | Metadata çağrısı=" + _metadataReadCalls +
                    " | Occurrence=" + (_result == null ? 0 : _result.OccurrenceCount));
                ComUtil.Release(root);
            }
        }

        private void TraverseChildren(Component2 parent, int parentRecordIndex)
        {
            ThrowIfCancelled();
            object[] children = null;
            try { children = parent.GetChildren() as object[]; } catch { }
            if (children == null) return;

            foreach (object obj in children)
            {
                ThrowIfCancelled();
                var comp = obj as Component2;
                if (comp == null) { ComUtil.Release(obj); continue; }
                try
                {
                    int suppression = -1;
                    try { suppression = comp.GetSuppression2(); }
                    catch { try { suppression = comp.GetSuppression(); } catch { } }
                    // Pasif/suppressed bileşenler varsayılan olarak hariç; seçenek açıksa satır olarak dahil edilir.
                    if (!_includePassive && suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                    // Gizli bileşenler varsayılan olarak dahildir. Seçenek kapatılırsa üst montajı
                    // gizli olan alt bileşenleri de dışarıda bırakmak için etkin görünürlük kontrol edilir.
                    if (!_includeHidden && !AssemblyScanner.IsEffectivelyVisible(comp)) continue;

                    string path = Safe(() => comp.GetPathName(), "");
                    string cfg = Safe(() => comp.ReferencedConfiguration, "");
                    string name = Safe(() => comp.Name2, Path.GetFileNameWithoutExtension(path));
                    string selectId = Safe(() => comp.GetSelectByIDString(), "");
                    bool isAssembly = IsAssemblyOccurrence(comp, path);

                    // In upper-level mode this feature prepares only direct child subassemblies.
                    // Direct parts and all descendants are irrelevant to the requested top-level
                    // Montaj BOM, so don't open their documents just to read metadata.
                    if (_topLevelMontajOnly && parentRecordIndex == 0 && !isAssembly) continue;

                    var meta = ReadMetadata(comp, path, cfg, name);
                    int index = _records.Count;
                    var record = new OccurrenceRecord
                    {
                        Index = index,
                        ParentRecordIndex = parentRecordIndex,
                        Path = path,
                        Configuration = cfg,
                        Name = name,
                        SelectId = selectId,
                        Meta = meta,
                        IsAssembly = isAssembly
                    };
                    _records.Add(record);
                    _result.OccurrenceCount++;
                    _progress?.Invoke(_records.Count, _progressTotal, meta.PartCode);
                    if ((_records.Count % 20) == 0) System.Windows.Forms.Application.DoEvents();

                    // Top-level Montaj Hazırla modunda alt montajların içine girme. İleri/ayrıntılı
                    // modda ise önceki hiyerarşik tarama davranışı korunur.
                    if (isAssembly && !_topLevelMontajOnly) TraverseChildren(comp, index);
                    ThrowIfCancelled();
                }
                finally { ComUtil.Release(comp); }
            }
        }

        private ComponentMetadata ReadMetadata(Component2 component, string path, string cfg, string name)
        {
            ThrowIfCancelled();
            string key = !string.IsNullOrWhiteSpace(path)
                ? path + "|" + (cfg ?? "")
                : "VIRTUAL|" + (name ?? "") + "|" + (cfg ?? "");
            ComponentMetadata metadata;
            if (_metadataCache.TryGetValue(key, out metadata))
            {
                _result.MetadataCacheHits++;
                return CloneMetadata(metadata);
            }

            string diskKey = BuildPersistentMetadataKey(path, cfg);
            bool diskCacheAllowed = !string.IsNullOrWhiteSpace(diskKey) && !IsSourceDocumentDirty(path, component);
            bool persistentHit = false;
            if (diskCacheAllowed && _persistentMetadataCache.TryGetValue(diskKey, out metadata))
            {
                metadata = CloneMetadata(metadata);
                metadata.Path = path ?? "";
                metadata.Configuration = cfg ?? "";
                metadata.DrawingPath = ResolveDrawingPath(path, metadata.PartCode);
                persistentHit = true;
                _persistentMetadataCacheHits++;
                TouchPersistentMetadataKey(diskKey);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(diskKey)) _persistentMetadataCacheMisses++;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var metadataWatch = Stopwatch.StartNew();
                    try
                    {
                        try { metadata = _metadataReader.ReadForAssemblyPreparation(path, cfg, component, _shouldCancel); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Logger.Info("Montaj metadata okuma uyarısı: " + path + " | " + ex.Message); metadata = null; }
                    }
                    finally
                    {
                        metadataWatch.Stop();
                        _metadataReadElapsed += metadataWatch.Elapsed;
                        _metadataReadCalls++;
                    }
                }
                else
                {
                    // Sanal/path'siz bileşen de BOM'da kalmalı. Yüklenmiş modelden özel
                    // özellikleri oku; parça kodu yoksa occurrence adını kullan.
                    ModelDoc2 loaded = null;
                    try { loaded = component.GetModelDoc2() as ModelDoc2; } catch { }
                    metadata = new ComponentMetadata
                    {
                        Path = "",
                        Configuration = cfg ?? "",
                        PartCode = First(CustomPropertyReader.First(loaded, cfg, "Ürün Kodu", "Urun Kodu", "Parça Kodu", "Part Number"), name),
                        ProductDescription = CustomPropertyReader.First(loaded, cfg, "Ürün Tanımı", "Urun Tanimi", "Description", "Tanım", "Tanim"),
                        OperationType = CustomPropertyReader.OperationType(loaded, cfg),
                        ColorCode = CustomPropertyReader.First(loaded, cfg, "Boya Tipi", "Renk Kodu", "RAL Kodu", "Color Code", "Renk", "Color"),
                        MassText = "",
                        DrawingPath = ""
                    };
                    ComUtil.Release(loaded);
                }
            }

            if (metadata == null)
            {
                metadata = new ComponentMetadata
                {
                    Path = path ?? "",
                    Configuration = cfg ?? "",
                    PartCode = First(Path.GetFileNameWithoutExtension(path ?? ""), name),
                    ProductDescription = "",
                    OperationType = "",
                    ColorCode = "",
                    MassText = "",
                    DrawingPath = ""
                };
            }

            if (string.IsNullOrWhiteSpace(metadata.Path)) metadata.Path = path ?? "";
            if (string.IsNullOrWhiteSpace(metadata.Configuration)) metadata.Configuration = cfg ?? "";
            if (string.IsNullOrWhiteSpace(metadata.PartCode)) metadata.PartCode = First(Path.GetFileNameWithoutExtension(path ?? ""), name);

            // Cache only a result that actually contains the operation-type property. This avoids
            // persisting a temporary BasicMetadata fallback if SOLIDWORKS could not open a file.
            if (!persistentHit && diskCacheAllowed && metadata != null &&
                !string.IsNullOrWhiteSpace(metadata.OperationType))
            {
                metadata.DrawingPath = ResolveDrawingPath(path, metadata.PartCode);
                StorePersistentMetadata(diskKey, metadata);
            }

            _metadataCache[key] = CloneMetadata(metadata);
            if (persistentHit) _result.MetadataCacheHits++;
            else _result.MetadataCacheMisses++;
            return metadata;
        }

        private static string GetPersistentMetadataCachePath()
        {
            string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
            // V1.67 cache schema/identity policy: force a fresh metadata read so older cached
            // vendor Part Number values cannot override the local SOLIDWORKS document name.
            return Path.Combine(localAppData, "SrcCadWorks", "Cache", "MontajMetadata_DocumentName_v2.tsv");
        }

        private void LoadPersistentMetadataCache()
        {
            _persistentMetadataCache.Clear();
            _persistentMetadataOrder.Clear();
            _persistentMetadataCachePath = GetPersistentMetadataCachePath();
            try
            {
                if (!File.Exists(_persistentMetadataCachePath)) return;
                string[] lines = File.ReadAllLines(_persistentMetadataCachePath, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - PersistentMetadataCacheLimit);
                for (int i = start; i < lines.Length; i++)
                {
                    try
                    {
                        string[] fields = lines[i].Split('\t');
                        if (fields.Length != 13) continue;
                        string key = DecodeCacheField(fields[0]);
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        var item = new ComponentMetadata
                        {
                            Path = DecodeCacheField(fields[1]),
                            Configuration = DecodeCacheField(fields[2]),
                            PartCode = DecodeCacheField(fields[3]),
                            SawProfileType = DecodeCacheField(fields[4]),
                            ProductDescription = DecodeCacheField(fields[6]),
                            OperationType = DecodeCacheField(fields[7]),
                            Material = DecodeCacheField(fields[8]),
                            Thickness = DecodeCacheField(fields[9]),
                            ColorCode = DecodeCacheField(fields[10]),
                            WeldProcess = DecodeCacheField(fields[11]),
                            MassText = DecodeCacheField(fields[12]),
                            DrawingPath = ""
                        };
                        string holeWizard = DecodeCacheField(fields[5]);
                        item.SawHasHoleWizard = holeWizard == "1" ? (bool?)true : holeWizard == "0" ? (bool?)false : null;
                        _persistentMetadataCache[key] = item;
                        _persistentMetadataOrder.Add(key);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Montaj metadata önbelleği okunamadı; bu taramada yeniden okunacak: " + ex.Message);
                _persistentMetadataCache.Clear();
                _persistentMetadataOrder.Clear();
            }
        }

        private void SavePersistentMetadataCache()
        {
            if (!_persistentMetadataCacheDirty || string.IsNullOrWhiteSpace(_persistentMetadataCachePath)) return;
            string directory = Path.GetDirectoryName(_persistentMetadataCachePath);
            Directory.CreateDirectory(directory);
            var keys = _persistentMetadataOrder
                .Where(k => _persistentMetadataCache.ContainsKey(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keys.Count > PersistentMetadataCacheLimit)
                keys = keys.Skip(keys.Count - PersistentMetadataCacheLimit).ToList();
            string temp = _persistentMetadataCachePath + ".building_" + Guid.NewGuid().ToString("N");
            try
            {
                var lines = new List<string>(keys.Count);
                foreach (string key in keys)
                {
                    ComponentMetadata item = _persistentMetadataCache[key];
                    lines.Add(string.Join("\t", new[]
                    {
                        EncodeCacheField(key), EncodeCacheField(item.Path), EncodeCacheField(item.Configuration),
                        EncodeCacheField(item.PartCode), EncodeCacheField(item.SawProfileType),
                        EncodeCacheField(item.SawHasHoleWizard.HasValue ? (item.SawHasHoleWizard.Value ? "1" : "0") : ""),
                        EncodeCacheField(item.ProductDescription), EncodeCacheField(item.OperationType),
                        EncodeCacheField(item.Material), EncodeCacheField(item.Thickness), EncodeCacheField(item.ColorCode),
                        EncodeCacheField(item.WeldProcess), EncodeCacheField(item.MassText)
                    }));
                }
                File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                if (File.Exists(_persistentMetadataCachePath))
                {
                    string backup = _persistentMetadataCachePath + ".bak";
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    try
                    {
                        File.Replace(temp, _persistentMetadataCachePath, backup, true);
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    }
                    catch
                    {
                        File.Copy(temp, _persistentMetadataCachePath, true);
                        File.Delete(temp);
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    }
                }
                else File.Move(temp, _persistentMetadataCachePath);
                _persistentMetadataCacheDirty = false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static string BuildPersistentMetadataKey(string path, string configuration)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                var info = new FileInfo(Path.GetFullPath(path));
                if (!info.Exists) return null;
                return info.FullName + "\u001f" + (configuration ?? "") + "\u001f" +
                    info.Length.ToString(CultureInfo.InvariantCulture) + "\u001f" +
                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        private bool IsSourceDocumentDirty(string path, Component2 component)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                ModelDoc2 model = null;
                if (component != null)
                {
                    try { model = component.GetModelDoc2() as ModelDoc2; } catch { }
                }
                if (model == null)
                {
                    try { model = _swApp.GetOpenDocumentByName(path) as ModelDoc2; } catch { }
                }
                return model != null && model.GetSaveFlag();
            }
            catch { return true; }
        }

        private void StorePersistentMetadata(string key, ComponentMetadata metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || metadata == null) return;
            _persistentMetadataCache[key] = CloneMetadata(metadata);
            TouchPersistentMetadataKey(key);
            _persistentMetadataCacheDirty = true;
        }

        private void TouchPersistentMetadataKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _persistentMetadataOrder.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            _persistentMetadataOrder.Add(key);
            _persistentMetadataCacheDirty = true;
        }

        private static string ResolveDrawingPath(string modelPath, string partCode)
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

        private static string EncodeCacheField(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string DecodeCacheField(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? ""));
        }

        private void AppendChildren(
            WeldFormRow parentRow,
            OccurrenceRecord parentRecord,
            IDictionary<string, int> globalOccurrences,
            ISet<string> registeredAssemblyIds)
        {
            ThrowIfCancelled();
            var childGroups = _records
                .Where(r => r.ParentRecordIndex == parentRecord.Index)
                .GroupBy(r => r.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Min(r => r.Index))
                .ToList();

            int childNo = 0;
            foreach (var group in childGroups)
            {
                ThrowIfCancelled();
                childNo++;
                // Don't blindly use the first occurrence. If its subassembly children were
                // unavailable during traversal but another identical occurrence was expanded,
                // choosing the first would silently drop that entire subtree from the Excel BOM.
                var rep = group
                    .OrderByDescending(r =>
                    {
                        int count;
                        return _childRecordCounts.TryGetValue(r.Index, out count) ? count : 0;
                    })
                    .ThenBy(r => r.Index)
                    .First();
                var ids = group.Select(r => r.SelectId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string sequence = string.IsNullOrWhiteSpace(parentRow.Sequence)
                    ? childNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : parentRow.Sequence + "." + childNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool isDirectChildOfActiveAssembly = parentRecord.ParentRecordIndex < 0;
                // IsTopLevelWeld yalnızca aktif montajın doğrudan çocuğu olan ve işlem tipi
                // MONTAJ olan alt montajlar için işaretlenir. Bu, Montaj Adedi çarpanını ve
                // "Sadece üst montajları dahil et" çıktı filtresini doğru kayıtlarla sınırlar.
                bool isTopLevelMontaj = isDirectChildOfActiveAssembly && rep.IsAssembly &&
                    string.Equals(WeldAssemblyScanner.NormalizeOperation(rep.Meta == null ? "" : rep.Meta.OperationType),
                        "MONTAJ", StringComparison.OrdinalIgnoreCase);
                var row = ToRow(rep, sequence, group.Count(), rep.IsAssembly,
                    isTopLevelMontaj, parentRow.Depth + 1, isDirectChildOfActiveAssembly ? "" : parentRow.PartCode, ids);
                int total;
                row.TotalQuantity = globalOccurrences.TryGetValue(rep.IdentityKey, out total) ? total : group.Count();
                _result.Rows.Add(row);

                if (rep.IsAssembly && registeredAssemblyIds.Add(rep.IdentityKey)) _result.WeldRoots.Add(row);
                if (rep.IsAssembly) AppendChildren(row, rep, globalOccurrences, registeredAssemblyIds);
            }
        }

        private static WeldFormRow ToRow(OccurrenceRecord record, string sequence, int quantity, bool isAssembly,
            bool topLevel, int depth, string parentPartCode, List<string> occurrenceSelectIds)
        {
            var metadata = record.Meta ?? new ComponentMetadata();
            string path = metadata.Path ?? record.Path ?? "";
            string code = ManufacturingIdentity.PartCode(path, metadata.PartCode);
            // Some configurable/imported roller components have a generic template code
            // (for example CRxxxx_Tube) or a numeric fallback, while their actual SOLIDWORKS
            // component/BOM display name is specific (for example Interroll_R734..._596mm).
            // Do not invent a body name: prefer the component instance name only for these
            // generic placeholders, keeping normal manufacturing part codes unchanged.
            string componentDisplayName = GetComponentDisplayName(record.Name);
            if (IsGenericPlaceholderPartCode(code) &&
                !string.IsNullOrWhiteSpace(componentDisplayName) &&
                !IsGenericPlaceholderPartCode(componentDisplayName) &&
                !string.Equals(componentDisplayName, code, StringComparison.OrdinalIgnoreCase))
            {
                code = componentDisplayName;
            }
            return new WeldFormRow
            {
                Sequence = sequence,
                IsWeldRoot = isAssembly,
                IsTopLevelWeld = topLevel,
                TotalQuantity = quantity,
                Depth = depth,
                PartCode = string.IsNullOrWhiteSpace(code) ? (record.Name ?? "BİLEŞEN") : code,
                ProductDescription = metadata.ProductDescription ?? "",
                Quantity = quantity,
                MassText = metadata.MassText ?? "",
                ColorCode = metadata.ColorCode ?? "",
                OperationType = WeldAssemblyScanner.NormalizeOperation(metadata.OperationType),
                SourcePath = path,
                Configuration = metadata.Configuration ?? record.Configuration ?? "",
                DrawingPath = ManufacturingIdentity.Drawing(path, metadata.PartCode,
                    string.IsNullOrWhiteSpace(code) ? metadata.PartCode : code, metadata.DrawingPath),
                SelectId = record.SelectId ?? "",
                OccurrenceSelectIds = occurrenceSelectIds ?? new List<string>(),
                ParentPartCode = parentPartCode ?? ""
            };
        }

        private static bool IsAssemblyOccurrence(Component2 component, string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) return path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
            try
            {
                var model = component.GetModelDoc2() as ModelDoc2;
                return model != null && model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            }
            catch { return false; }
        }

        private void ThrowIfCancelled()
        {
            if (_shouldCancel != null && _shouldCancel())
                throw new OperationCanceledException("Montaj taraması ESC/kullanıcı isteği ile iptal edildi.");
        }

        private static T Safe<T>(Func<T> getter, T fallback)
        {
            try { return getter(); } catch { return fallback; }
        }

        private static string GetComponentDisplayName(string componentName)
        {
            string value = (componentName ?? "").Trim();
            if (value.Length == 0) return "";

            // Component2.Name2 may be returned as "ComponentName-1@AssemblyName".
            // The BOM-facing name is the component portion before the owning assembly.
            int at = value.IndexOf('@');
            if (at >= 0) value = value.Substring(0, at).Trim();

            // Remove only a trailing SOLIDWORKS occurrence index (e.g. -1, -2).
            int dash = value.LastIndexOf('-');
            if (dash > 0 && dash < value.Length - 1)
            {
                bool numericSuffix = true;
                for (int i = dash + 1; i < value.Length; i++)
                    if (!char.IsDigit(value[i])) { numericSuffix = false; break; }
                if (numericSuffix) value = value.Substring(0, dash).Trim();
            }
            return value;
        }

        private static bool IsGenericPlaceholderPartCode(string value)
        {
            string candidate = (value ?? "").Trim();
            if (candidate.Length == 0) return true;

            // Known generic roller template name, not an individual roller model identity.
            string compact = candidate.Replace("_", "").Replace("-", "").Replace(" ", "");
            if (compact.StartsWith("CR", StringComparison.OrdinalIgnoreCase) &&
                compact.IndexOf("TUBE", StringComparison.OrdinalIgnoreCase) >= 0 &&
                compact.IndexOf("X", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Numeric fallback names are not useful when SOLIDWORKS exposes a descriptive
            // component name for the same occurrence. A genuinely numeric part remains intact
            // when the component name is also just that numeric value.
            bool allDigits = true;
            foreach (char ch in candidate)
                if (!char.IsDigit(ch)) { allDigits = false; break; }
            return allDigits && candidate.Length <= 8;
        }

        private static string First(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a.Trim() : (b ?? "").Trim();

        private static ComponentMetadata CloneMetadata(ComponentMetadata m)
        {
            if (m == null) return null;
            return new ComponentMetadata
            {
                Path = m.Path, Configuration = m.Configuration, PartCode = m.PartCode,
                SawProfileType = m.SawProfileType, SawHasHoleWizard = m.SawHasHoleWizard,
                ProductDescription = m.ProductDescription, OperationType = m.OperationType,
                Material = m.Material, Thickness = m.Thickness, ColorCode = m.ColorCode,
                WeldProcess = m.WeldProcess, MassText = m.MassText, DrawingPath = m.DrawingPath
            };
        }

        private sealed class OccurrenceRecord
        {
            public int Index;
            public int ParentRecordIndex;
            public string Path;
            public string Configuration;
            public string Name;
            public string SelectId;
            public ComponentMetadata Meta;
            public bool IsAssembly;
            public string IdentityKey => !string.IsNullOrWhiteSpace(Path)
                ? Path + "|" + (Configuration ?? "")
                : "VIRTUAL|" + (Name ?? Meta?.PartCode ?? "BILEŞEN") + "|" + (Configuration ?? "") + (IsAssembly ? "|ASM" : "|PRT");
        }
    }
}
