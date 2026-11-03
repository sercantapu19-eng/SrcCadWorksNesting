using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class AssemblyViewerSnapshotService
    {
        private readonly SldWorks _swApp;
        private readonly ComponentMetadataReader _reader;
        private readonly WeldMetadataCache _weldCache;
        private readonly PartAnalysisCache _laserCache;
        private Func<bool> _shouldCancel;
        private int _flatOrderCounter;
        private int _visitedHierarchyComponents;
        private string _liveSnapshotPath;
        private AssemblyViewerSnapshot _liveSnapshot;
        private IDictionary<string, TempGroup> _liveGroups;
        private int _lastPublishedCount;
        private readonly System.Diagnostics.Stopwatch _publishWatch = new System.Diagnostics.Stopwatch();
        public AssemblyViewerSnapshotService(SldWorks swApp)
        {
            _swApp = swApp;
            _reader = new ComponentMetadataReader(swApp);
            _weldCache = new WeldMetadataCache();
            _laserCache = new PartAnalysisCache();
        }

        public string CreateLoadingSnapshotFile()
        {
            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Montaj Görüntüleme için aktif belge bir SLDASM olmalıdır.");

            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SrcCadWorks", "viewer");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "assembly_snapshot_" + Guid.NewGuid().ToString("N") + ".json");

            var snapshot = new AssemblyViewerSnapshot
            {
                AssemblyPath = model.GetPathName() ?? "",
                AssemblyTitle = Path.GetFileNameWithoutExtension(model.GetPathName()) ?? model.GetTitle(),
                AssemblyConfiguration = SafeActiveConfiguration(model),
                IsLoading = true,
                ProcessedComponents = 0,
                StatusText = "Montaj okunuyor..."
            };
            WriteSnapshotAtomic(file, snapshot);
            return file;
        }

        public void MarkLiveSnapshotStopped(string path, string status)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                var snapshot = _liveSnapshot ?? new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<AssemblyViewerSnapshot>(
                    File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (_liveGroups != null) UpdateFlatItems(snapshot, _liveGroups);
                if (snapshot == null) return;
                snapshot.IsLoading = false;
                snapshot.ProcessedComponents = _visitedHierarchyComponents;
                snapshot.ProgressPercent = snapshot.TotalComponents > 0
                    ? (int)Math.Min(99L, 100L * _visitedHierarchyComponents / snapshot.TotalComponents) : 0;
                snapshot.StatusText = status ?? "Durduruldu.";
                WriteSnapshotAtomic(path, snapshot);
            }
            catch { }
        }

        public string CreateSnapshot(Func<bool> shouldCancel = null, string liveSnapshotPath = null)
        {
            _shouldCancel = shouldCancel;
            _flatOrderCounter = 0;
            _visitedHierarchyComponents = 0;
            _lastPublishedCount = 0; _publishWatch.Restart();
            _liveSnapshotPath = liveSnapshotPath;
            _liveSnapshot = null;
            ThrowIfCancelled();

            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Montaj Görüntüleme için aktif belge bir SLDASM olmalıdır.");

            var asm = model as AssemblyDoc;
            var groups = new Dictionary<string, TempGroup>(StringComparer.OrdinalIgnoreCase);
            _liveGroups = groups;

            var snapshot = new AssemblyViewerSnapshot
            {
                AssemblyPath = model.GetPathName() ?? "",
                AssemblyTitle = Path.GetFileNameWithoutExtension(model.GetPathName()) ?? model.GetTitle(),
                AssemblyConfiguration = SafeActiveConfiguration(model),
                IsLoading = true,
                ProcessedComponents = 0,
                StatusText = "Montaj okunuyor..."
            };
            _liveSnapshot = snapshot;
            PublishLiveSnapshot(true);

            // V1.12.70 performans yolu:
            // Aynı büyük montajı birden çok kez dolaşmak yerine tek recursive geçiş
            // hem global miktarları hem top-level miktarları hem de ağaç satırlarını toplar.
            snapshot.TotalComponents = CountReadableComponents(asm);
            snapshot.StatusText = "Montaj okunuyor...";
            PublishLiveSnapshot(true);

            object[] hierarchyTop = null;
            Exception componentError = null;
            try { hierarchyTop = asm.GetComponents(true) as object[]; } catch (Exception ex) { componentError = ex; }
            if (hierarchyTop == null || hierarchyTop.Length == 0)
            {
                Component2 root = null;
                try
                {
                    root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2;
                    if (root != null) hierarchyTop = root.GetChildren() as object[];
                }
                catch (Exception ex) { componentError = ex; }
                finally { ComUtil.Release(root); }
            }
            if (hierarchyTop == null)
                throw new InvalidOperationException("Montaj bileşenleri okunamadı." + (componentError == null ? "" : " " + componentError.Message));

            BuildHierarchyLevel(hierarchyTop, "", 0, snapshot.TreeItems, groups, "R");
            ThrowIfCancelled();

            UpdateFlatItems(snapshot, groups);

            ThrowIfCancelled();
            snapshot.IsLoading = false;
            snapshot.ProgressPercent = 100;
            snapshot.ProcessedComponents = _visitedHierarchyComponents;
            snapshot.StatusText = _visitedHierarchyComponents == 0 ? "Montajda okunabilir etkin bileşen bulunamadı." : "Hazır";
            PublishLiveSnapshot(true);

            if (!string.IsNullOrWhiteSpace(_liveSnapshotPath))
                return _liveSnapshotPath;

            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SrcCadWorks", "viewer");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "assembly_snapshot_" + Guid.NewGuid().ToString("N") + ".json");
            WriteSnapshotAtomic(file, snapshot);
            return file;
        }

        private int CountReadableComponents(AssemblyDoc assembly)
        {
            // A single lightweight enumeration supplies the denominator; no documents
            // are opened and no properties are read for this counting pass.
            object[] components = null;
            try { components = assembly.GetComponents(false) as object[]; } catch { }
            if (components == null) return 0;
            int count = 0;
            foreach (object value in components)
            {
                ThrowIfCancelled();
                var component = value as Component2;
                if (component == null) continue;
                try
                {
                    int suppression = -1;
                    try { suppression = component.GetSuppression2(); } catch { try { suppression = component.GetSuppression(); } catch { } }
                    if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                    string path = ""; try { path = component.GetPathName() ?? ""; } catch { }
                    if (!string.IsNullOrWhiteSpace(path)) count++;
                }
                finally { ComUtil.Release(component); }
            }
            return count;
        }

        private void UpdateFlatItems(AssemblyViewerSnapshot snapshot, IDictionary<string, TempGroup> groups)
        {
            if (snapshot == null || groups == null) return;
            snapshot.Items.Clear();
            foreach (var g in groups.Values.OrderBy(x => x.FirstIndex))
            {
                ComponentMetadata m = g.Meta ?? ReadViewerMetadataWithoutOpening(g.Path, g.Configuration);
                g.Meta = m;
                string operation = WeldAssemblyScanner.NormalizeOperation(m?.OperationType);
                string documentType = GetDocumentType(g.Path);
                bool hasWeld = string.Equals(operation, "KAYNAK", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(m?.WeldProcess);

                snapshot.Items.Add(new AssemblyViewerItem
                {
                    FileName = Path.GetFileNameWithoutExtension(g.Path),
                    PartCode = string.IsNullOrWhiteSpace(m?.PartCode) ? Path.GetFileNameWithoutExtension(g.Path) : m.PartCode,
                    Quantity = g.Quantity,
                    TopLevelQuantity = g.TopLevelQuantity,
                    DocumentType = documentType,
                    OperationType = operation,
                    ProductDescription = m?.ProductDescription ?? "",
                    Material = m?.Material ?? "",
                    Thickness = m?.Thickness ?? "",
                    ColorCode = m?.ColorCode ?? "",
                    WeldProcess = m?.WeldProcess ?? "",
                    WeldStatus = hasWeld ? "VAR" : "YOK",
                    MassText = m?.MassText ?? "",
                    Configuration = g.Configuration,
                    SourcePath = g.Path,
                    DrawingPath = m?.DrawingPath ?? "",
                    SelectId = g.TopLevelSelectIds.FirstOrDefault() ?? g.SelectIds.FirstOrDefault() ?? "",
                    OccurrenceSelectIds = g.SelectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    TopLevelOccurrenceSelectIds = g.TopLevelSelectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }

        }

        private void PublishLiveSnapshot(bool force)
        {
            if (string.IsNullOrWhiteSpace(_liveSnapshotPath) || _liveSnapshot == null) return;
            if (!force && (_visitedHierarchyComponents <= _lastPublishedCount || _publishWatch.ElapsedMilliseconds < 350)) return;

            _lastPublishedCount = _visitedHierarchyComponents; _publishWatch.Restart();
            _liveSnapshot.ProcessedComponents = _visitedHierarchyComponents;
            if (_liveSnapshot.IsLoading)
                _liveSnapshot.ProgressPercent = _liveSnapshot.TotalComponents > 0
                    ? (int)Math.Min(99L, 100L * _visitedHierarchyComponents / _liveSnapshot.TotalComponents) : 0;
            UpdateFlatItems(_liveSnapshot, _liveGroups);
            if (force) WriteSnapshotAtomic(_liveSnapshotPath, _liveSnapshot);
            else { try { WriteSnapshotAtomic(_liveSnapshotPath, _liveSnapshot); } catch { } }
        }

        private static void WriteSnapshotAtomic(string path, AssemblyViewerSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(path) || snapshot == null) return;
            string dir = Path.GetDirectoryName(path) ?? "";
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            string json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(snapshot);
            File.WriteAllText(temp, json, System.Text.Encoding.UTF8);

            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private void ThrowIfCancelled()
        {
            if (_shouldCancel != null && _shouldCancel())
                throw new OperationCanceledException("Montaj Görüntüleme hazırlığı ESC ile iptal edildi.");
        }

        private void BuildHierarchyLevel(object[] components, string parentNodeId, int depth,
            IList<AssemblyViewerItem> output, IDictionary<string, TempGroup> flatGroups, string nodePrefix)
        {
            if (components == null || components.Length == 0) return;

            var siblingGroups = new Dictionary<string, TreeTempGroup>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<TreeTempGroup>();

            for (int i = 0; i < components.Length; i++)
            {
                ThrowIfCancelled();
                var comp = components[i] as Component2;
                if (comp == null) continue;
                bool keepRepresentative = false;
                try
                {
                    int suppression = -1;
                    try { suppression = comp.GetSuppression2(); } catch { try { suppression = comp.GetSuppression(); } catch { } }
                    if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;

                    string path = "";
                    try { path = comp.GetPathName() ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string cfg = "";
                    try { cfg = comp.ReferencedConfiguration ?? ""; } catch { }
                    string key = path + "|" + cfg;

                    TempGroup flatGroup;
                    if (!flatGroups.TryGetValue(key, out flatGroup))
                    {
                        flatGroup = new TempGroup
                        {
                            Path = path,
                            Configuration = cfg,
                            FirstIndex = _flatOrderCounter++
                        };
                        flatGroups[key] = flatGroup;

                        try { flatGroup.Meta = _reader.ReadLoadedOnly(path, cfg, comp); } catch { }
                        if (flatGroup.Meta == null) flatGroup.Meta = ReadViewerMetadataFromCaches(path, cfg);
                    }

                    flatGroup.Quantity++;
                    string globalSid = "";
                    try { globalSid = comp.GetSelectByIDString() ?? ""; } catch { }
                    if (!string.IsNullOrWhiteSpace(globalSid)) flatGroup.SelectIds.Add(globalSid);
                    if (depth == 0)
                    {
                        flatGroup.TopLevelQuantity++;
                        if (!string.IsNullOrWhiteSpace(globalSid)) flatGroup.TopLevelSelectIds.Add(globalSid);
                    }

                    TreeTempGroup group;
                    if (!siblingGroups.TryGetValue(key, out group))
                    {
                        group = new TreeTempGroup
                        {
                            Path = path,
                            Configuration = cfg,
                            FirstIndex = i,
                            Representative = comp
                        };
                        siblingGroups[key] = group;
                        ordered.Add(group);
                        keepRepresentative = true;
                    }

                    // The tree renders one representative per sibling group. Count all
                    // other actual occurrences too, including instance-specific children.
                    if (!keepRepresentative && string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase))
                    {
                        object[] nested = null; try { nested = comp.GetChildren() as object[]; } catch { }
                        CountNestedOccurrences(nested, flatGroups);
                    }
                    group.Quantity++;
                    if (!string.IsNullOrWhiteSpace(globalSid)) group.SelectIds.Add(globalSid);

                    _visitedHierarchyComponents++;
                    PublishLiveSnapshot(false);
                    if ((_visitedHierarchyComponents % 50) == 0)
                    {
                        ThrowIfCancelled();
                        if (_liveSnapshot != null)
                        {
                            _liveSnapshot.ProcessedComponents = _visitedHierarchyComponents;
                            _liveSnapshot.StatusText = "Montaj okunuyor • " + _visitedHierarchyComponents + " bileşen işlendi";
                        }
                        PublishLiveSnapshot(false);
                        System.Windows.Forms.Application.DoEvents();
                    }
                }
                finally
                {
                    if (!keepRepresentative) ComUtil.Release(comp);
                }
            }

            int siblingOrder = 0;
            foreach (var group in ordered.OrderBy(x => x.FirstIndex))
            {
                ThrowIfCancelled();
                siblingOrder++;
                var comp = group.Representative;
                try
                {
                    string flatKey = group.Path + "|" + group.Configuration;
                    TempGroup flat = null;
                    ComponentMetadata m = null;
                    if (flatGroups != null && flatGroups.TryGetValue(flatKey, out flat)) m = flat.Meta;
                    if (m == null)
                    {
                        m = ReadViewerMetadataWithoutOpening(group.Path, group.Configuration);
                        if (flat != null) flat.Meta = m;
                    }

                    object[] childComponents = null;
                    bool isAssembly = string.Equals(Path.GetExtension(group.Path), ".SLDASM", StringComparison.OrdinalIgnoreCase);
                    if (isAssembly && comp != null)
                    {
                        try { childComponents = comp.GetChildren() as object[]; } catch { }
                    }
                    if (childComponents == null) childComponents = Array.Empty<object>();

                    string nodeId = nodePrefix + "." + siblingOrder.ToString();
                    string operation = WeldAssemblyScanner.NormalizeOperation(m?.OperationType);
                    bool hasWeld = string.Equals(operation, "KAYNAK", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(m?.WeldProcess);

                    output.Add(new AssemblyViewerItem
                    {
                        FileName = Path.GetFileNameWithoutExtension(group.Path),
                        PartCode = string.IsNullOrWhiteSpace(m?.PartCode) ? Path.GetFileNameWithoutExtension(group.Path) : m.PartCode,
                        Quantity = group.Quantity,
                        TopLevelQuantity = depth == 0 ? group.Quantity : 0,
                        DocumentType = GetDocumentType(group.Path),
                        OperationType = operation,
                        ProductDescription = m?.ProductDescription ?? "",
                        Material = m?.Material ?? "",
                        Thickness = m?.Thickness ?? "",
                        ColorCode = m?.ColorCode ?? "",
                        WeldProcess = m?.WeldProcess ?? "",
                        WeldStatus = hasWeld ? "VAR" : "YOK",
                        MassText = m?.MassText ?? "",
                        Configuration = group.Configuration,
                        SourcePath = group.Path,
                        DrawingPath = m?.DrawingPath ?? "",
                        SelectId = group.SelectIds.FirstOrDefault() ?? "",
                        OccurrenceSelectIds = group.SelectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        TopLevelOccurrenceSelectIds = depth == 0
                            ? group.SelectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                            : new List<string>(),
                        NodeId = nodeId,
                        ParentNodeId = parentNodeId ?? "",
                        Depth = depth,
                        HasChildren = childComponents.Length > 0,
                        HierarchyOrder = siblingOrder
                    });

                    if (childComponents.Length > 0)
                    {
                        ThrowIfCancelled();
                        BuildHierarchyLevel(childComponents, nodeId, depth + 1, output, flatGroups, nodeId);
                    }
                }
                finally
                {
                    ComUtil.Release(comp);
                }
            }
        }

        private void CountNestedOccurrences(object[] components, IDictionary<string, TempGroup> groups)
        {
            if (components == null) return;
            for (int i = 0; i < components.Length; i++)
            {
                ThrowIfCancelled();
                var comp = components[i] as Component2; if (comp == null) continue;
                try
                {
                    int suppression = -1; try { suppression = comp.GetSuppression2(); } catch { }
                    if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                    string path = "", cfg = "";
                    try { path = comp.GetPathName() ?? ""; cfg = comp.ReferencedConfiguration ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string key = path + "|" + cfg; TempGroup group;
                    if (!groups.TryGetValue(key, out group))
                    {
                        group = new TempGroup { Path = path, Configuration = cfg, FirstIndex = _flatOrderCounter++ };
                        try { group.Meta = _reader.ReadLoadedOnly(path,cfg,comp); } catch { }
                        if (group.Meta == null) group.Meta = ReadViewerMetadataFromCaches(path,cfg);
                        groups[key] = group;
                    }
                    group.Quantity++;
                    string sid = ""; try { sid = comp.GetSelectByIDString() ?? ""; } catch { }
                    if (!string.IsNullOrWhiteSpace(sid)) group.SelectIds.Add(sid);
                    _visitedHierarchyComponents++;
                    PublishLiveSnapshot(false);
                    PublishLiveSnapshot(false);
                    if ((_visitedHierarchyComponents % 50) == 0) { System.Windows.Forms.Application.DoEvents(); }
                    if (string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase))
                    {
                        object[] nested = null; try { nested = comp.GetChildren() as object[]; } catch { }
                        CountNestedOccurrences(nested,groups);
                    }
                }
                finally { ComUtil.Release(comp); }
            }
        }

        private ComponentMetadata ReadViewerMetadataWithoutOpening(string path, string configuration)
        {
            ComponentMetadata m = null;
            try { m = _reader.ReadLoadedOnly(path, configuration); } catch { }
            return m ?? ReadViewerMetadataFromCaches(path, configuration) ?? BasicViewerMetadata(path, configuration);
        }

        private ComponentMetadata ReadViewerMetadataFromCaches(string path, string configuration)
        {
            ComponentMetadata m;
            try
            {
                if (_weldCache.TryGet(path, configuration, out m) && m != null) return m;
            }
            catch { }

            try
            {
                LaserPartRow row;
                if (_laserCache.TryGet(path, configuration, 1, out row) && row != null)
                {
                    return new ComponentMetadata
                    {
                        Path = path,
                        Configuration = configuration ?? "",
                        PartCode = string.IsNullOrWhiteSpace(row.PartCode) ? Path.GetFileNameWithoutExtension(path) : row.PartCode,
                        ProductDescription = row.ProductionDefinition ?? "",
                        OperationType = row.IsLaserTagged ? "LAZER" : (row.Operation ?? ""),
                        Material = row.Material ?? "",
                        Thickness = row.ThicknessText ?? "",
                        ColorCode = row.ColorCode ?? "",
                        WeldProcess = row.WeldProcess ?? "",
                        MassText = "",
                        DrawingPath = row.DrawingPath ?? ""
                    };
                }
            }
            catch { }
            return null;
        }

        private static ComponentMetadata BasicViewerMetadata(string path, string configuration)
        {
            string code = Path.GetFileNameWithoutExtension(path) ?? "";
            return new ComponentMetadata
            {
                Path = path ?? "",
                Configuration = configuration ?? "",
                PartCode = code,
                ProductDescription = "",
                OperationType = "",
                Material = "",
                Thickness = "",
                ColorCode = "",
                WeldProcess = "",
                MassText = "",
                DrawingPath = File.Exists(Path.ChangeExtension(path, ".SLDDRW")) ? Path.ChangeExtension(path, ".SLDDRW") : ""
            };
        }

        private static string GetDocumentType(string path)
        {
            string ext = Path.GetExtension(path ?? "");
            if (string.Equals(ext, ".SLDASM", StringComparison.OrdinalIgnoreCase)) return "MONTAJ";
            if (string.Equals(ext, ".SLDPRT", StringComparison.OrdinalIgnoreCase)) return "PARÇA";
            return "DOSYA";
        }

        private static string SafeActiveConfiguration(ModelDoc2 model)
        {
            try { return model.ConfigurationManager.ActiveConfiguration.Name ?? ""; } catch { return ""; }
        }

        private sealed class TreeTempGroup
        {
            public int FirstIndex;
            public string Path;
            public string Configuration;
            public int Quantity;
            public Component2 Representative;
            public List<string> SelectIds { get; } = new List<string>();
        }

        private sealed class TempGroup
        {
            public int FirstIndex;
            public string Path;
            public string Configuration;
            public int Quantity;
            public int TopLevelQuantity;
            public List<string> SelectIds { get; } = new List<string>();
            public List<string> TopLevelSelectIds { get; } = new List<string>();
            public ComponentMetadata Meta;
        }
    }
}
