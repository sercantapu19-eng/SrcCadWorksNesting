using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class WeldAssemblyScanner
    {
        private readonly SldWorks _swApp;
        private readonly ComponentMetadataReader _metadataReader;
        private readonly WeldMetadataCache _persistentMetadataCache;
        private readonly Dictionary<string, ComponentMetadata> _metadataCache = new Dictionary<string, ComponentMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly List<OccurrenceRecord> _records = new List<OccurrenceRecord>();
        private WeldScanResult _result;
        private Action<int,int,string> _progress;
        private int _progressTotal;
        private Func<bool> _shouldCancel;

        public WeldAssemblyScanner(SldWorks swApp)
        {
            _swApp = swApp;
            _metadataReader = new ComponentMetadataReader(swApp);
            _persistentMetadataCache = new WeldMetadataCache();
        }

        public WeldScanResult Scan(Action<int,int,string> progress = null, Func<bool> shouldCancel = null)
        {
            _shouldCancel = shouldCancel;
            ThrowIfCancelled();
            _result = new WeldScanResult();
            _records.Clear();
            _metadataCache.Clear();
            _progress = progress;
            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Aktif belge bir montaj olmalıdır.");

            try
            {
                var asm = model as AssemblyDoc;
                var flat = asm?.GetComponents(false) as object[];
                _progressTotal = flat == null ? 1 : Math.Max(1, flat.Length);
                if (flat != null)
                    foreach (object o in flat) ComUtil.Release(o);
            }
            catch { _progressTotal = 1; }

            Component2 root = null;
            try
            {
                var cfg = model.ConfigurationManager?.ActiveConfiguration;
                root = cfg?.GetRootComponent3(true) as Component2;
                if (root == null) throw new InvalidOperationException("Aktif montajın kök bileşeni okunamadı.");
                TraverseChildren(root, "", -1);
            }
            finally { ComUtil.Release(root); }

            // Kaynak montajlarını gerçek hiyerarşilerinde göster. Bir KAYNAK montajının
            // içinde bulunan başka bir KAYNAK montajı ayrı bir üst satıra taşınmaz;
            // satırı ana montajın altında kalır ve kendi alt parçaları 1.7.1 / 1.7.2
            // gibi o satıra bağlanır.
            var weldRecords = _records
                .Where(r => IsWeld(r.Meta.OperationType) && !string.IsNullOrWhiteSpace(r.Meta.Path))
                .ToList();
            var allWeldGroups = weldRecords
                .GroupBy(r => ManufacturingIdentity.Key(r.Meta.Path, r.Meta.Configuration), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .ToList();
            var totalQuantityByIdentity = allWeldGroups.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // SOLIDWORKS GetSelectByIDString bazı sanal/aynalanmış bileşenlerde tam
            // occurrence anahtarı olarak güvenilir değildir. Kendi tarama ağacımızda her
            // kayda benzersiz Index verip çocukları ParentRecordIndex ile bağlarız. Bu,
            // iç içe KAYNAK montajlarının ikinci kez üst seviye (10, 11...) olmasını önler.
            var recordByIndex = _records.ToDictionary(r => r.Index);
            var registeredWeldIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Yalnız doğrudan bir KAYNAK bileşeninin içinde olmayan KAYNAK montajları
            // reçete ağacının birinci seviyesi olur. İç içe KAYNAK bileşenleri aşağıda
            // ebeveyn satırının çocukları olarak genişletilir.
            var topLevelGroups = weldRecords
                .Where(r => !HasWeldParent(r, recordByIndex))
                .GroupBy(r => ManufacturingIdentity.Key(r.Meta.Path, r.Meta.Configuration), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .OrderBy(g => g.Min(r => r.Index))
                .ToList();

            int rootNo = 0;
            foreach (var group in topLevelGroups)
            {
                ThrowIfCancelled();
                rootNo++;
                var rep = group.OrderBy(r => r.Index).First();
                var rootSelectIds = group.Select(r => r.SelectId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string identity = ManufacturingIdentity.Key(rep.Meta.Path, rep.Meta.Configuration);
                int totalQuantity;
                if (!totalQuantityByIdentity.TryGetValue(identity, out totalQuantity)) totalQuantity = group.Count();

                // Quantity ana seviyede bu montaj içerisindeki bu kaynak montajının adetidir.
                // Montaj çarpanı UI'da yalnız bu seviyedeki satıra uygulanır. TotalQuantity
                // ise özet sekmesinin bütün ağaçtaki toplam ihtiyacını hesaplaması içindir.
                var rootRow = ToRow(rep.Meta, rootNo.ToString(), group.Count(), true, 0, rep.SelectId, "", rootSelectIds);
                rootRow.IsTopLevelWeld = true;
                rootRow.TotalQuantity = totalQuantity;
                _result.Rows.Add(rootRow);
                if (registeredWeldIdentities.Add(identity)) _result.WeldRoots.Add(rootRow);

                AppendRecipeChildren(rootRow, rep, totalQuantityByIdentity, registeredWeldIdentities);
            }

            _result.UniqueWeldCount = _result.WeldRoots.Count;
            if (_result.UniqueWeldCount == 0)
                _result.Warnings.Add("Aktif montajda İşlem Tipi = KAYNAK olan bileşen bulunamadı.");

            try { _persistentMetadataCache.Save(); } catch { }
            return _result;
        }

        private static bool HasWeldParent(OccurrenceRecord record, IDictionary<int, OccurrenceRecord> recordByIndex)
        {
            if (record == null || record.ParentRecordIndex < 0) return false;
            OccurrenceRecord parent;
            if (!recordByIndex.TryGetValue(record.ParentRecordIndex, out parent)) return false;
            return IsWeld(parent.Meta == null ? "" : parent.Meta.OperationType);
        }

        private void AppendRecipeChildren(
            WeldFormRow parentRow,
            OccurrenceRecord parentOccurrence,
            IDictionary<string, int> totalQuantityByIdentity,
            ISet<string> registeredWeldIdentities)
        {
            ThrowIfCancelled();
            if (parentRow == null || parentOccurrence == null) return;

            var children = _records
                .Where(r => r.ParentRecordIndex == parentOccurrence.Index)
                .GroupBy(r => ManufacturingIdentity.Key(r.Meta.Path, r.Meta.Configuration) + "|" + NormalizeOperation(r.Meta.OperationType), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Min(r => r.Index))
                .ToList();

            int childNo = 0;
            foreach (var childGroup in children)
            {
                ThrowIfCancelled();
                childNo++;
                var childRep = childGroup.OrderBy(r => r.Index).First();
                var childIdentity = ManufacturingIdentity.Key(childRep.Meta.Path, childRep.Meta.Configuration);
                bool childIsWeld = IsWeld(childRep.Meta.OperationType);
                int totalQuantity;
                if (!totalQuantityByIdentity.TryGetValue(childIdentity, out totalQuantity)) totalQuantity = childGroup.Count();
                var childSelectIds = childGroup.Select(r => r.SelectId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var childRow = ToRow(
                    childRep.Meta,
                    parentRow.Sequence + "." + childNo,
                    childGroup.Count(),
                    childIsWeld,
                    parentRow.Depth + 1,
                    childRep.SelectId,
                    parentRow.PartCode,
                    childSelectIds);
                childRow.IsTopLevelWeld = false;
                childRow.TotalQuantity = childIsWeld ? totalQuantity : childGroup.Count();
                _result.Rows.Add(childRow);

                if (childIsWeld && registeredWeldIdentities.Add(childIdentity))
                    _result.WeldRoots.Add(childRow);

                // Alt kaynak montajı kendi reçetesini hemen altında taşır.
                // Lazer/testere/talaşlı imalat parçaları ise kaynak satırının alt bileşeni
                // olarak kalır; onların bağımsız üretim reçeteleri burada açılmaz.
                if (childIsWeld)
                    AppendRecipeChildren(childRow, childRep, totalQuantityByIdentity, registeredWeldIdentities);
            }
        }

        private void ThrowIfCancelled()
        {
            if (_shouldCancel != null && _shouldCancel())
                throw new OperationCanceledException("Kaynak taraması ESC/kullanıcı isteği ile iptal edildi.");
        }

        private void TraverseChildren(Component2 parent, string parentSelectId, int parentRecordIndex)
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
                    try { suppression = comp.GetSuppression2(); } catch { try { suppression = comp.GetSuppression(); } catch { } }
                    if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;

                    string path = ""; try { path = comp.GetPathName() ?? ""; } catch { }
                    string cfg = ""; try { cfg = comp.ReferencedConfiguration ?? ""; } catch { }
                    string selectId = ""; try { selectId = comp.GetSelectByIDString() ?? ""; } catch { }

                    int currentRecordIndex = parentRecordIndex;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        ThrowIfCancelled();
                        var meta = GetMetadata(path, cfg, comp);
                        ThrowIfCancelled();
                        if (meta != null)
                        {
                            var record = new OccurrenceRecord
                            {
                                Index = _records.Count,
                                ParentRecordIndex = parentRecordIndex,
                                Path = path,
                                Configuration = cfg,
                                SelectId = selectId,
                                ParentSelectId = parentSelectId,
                                Meta = meta
                            };
                            _records.Add(record);
                            currentRecordIndex = record.Index;
                            _result.OccurrenceCount++;
                            _progress?.Invoke(_records.Count, _progressTotal, meta.PartCode);
                            if ((_records.Count % 20) == 0) System.Windows.Forms.Application.DoEvents();
                        }
                    }

                    // FeatureManager/girintili BOM sırasına yakın derinlik-öncelikli dolaşım.
                    // Metadata'sı olmayan ara bileşen varsa çocukları en yakın kayıtlı
                    // ebeveyne bağlı tutarak hiyerarşi boşluklarını da koru.
                    ThrowIfCancelled();
                    TraverseChildren(comp, selectId, currentRecordIndex);
                }
                finally { ComUtil.Release(comp); }
            }
        }

        private ComponentMetadata GetMetadata(string path, string cfg, Component2 comp)
        {
            string key = path + "|" + (cfg ?? "");
            ComponentMetadata meta;
            if (_metadataCache.TryGetValue(key, out meta)) return meta;

            // V1.12.10: Önce kalıcı Kaynak metadata cache. Dosya değişmemişse SOLIDWORKS'te
            // parça/montaj açmaya gerek kalmaz. Büyük montajlarda en büyük hız kazancı buradadır.
            // Always read live metadata; reuse only within this scan.
            // Kaynak Hazırla için Lazer analiz cache'indeki türetilmiş KESİM/KESİM+BÜKÜM
            // değeri kullanılmaz. İşlem Tipi, kütle ve proses bilgileri doğrudan SOLIDWORKS
            // özel özelliklerinden bir kez okunur; ardından v2 Kaynak metadata cache'e yazılır.
            // Böylece LAZER/TALAŞLI İMALAT/TESTERE eşlemesi doğru kalırken ikinci taramalar hızlıdır.

            _result.MetadataCacheMisses++;
            ThrowIfCancelled();
            meta = _metadataReader.Read(path, cfg, comp, _shouldCancel);
            ThrowIfCancelled();
            _metadataCache[key] = meta;
            if (meta != null) _persistentMetadataCache.Put(meta);
            return meta;
        }

        private static WeldFormRow ToRow(ComponentMetadata m, string seq, int qty, bool root, int depth, string selectId, string parent, List<string> occurrenceSelectIds)
        {
            return new WeldFormRow
            {
                Sequence = seq,
                IsWeldRoot = root,
                Depth = depth,
                PartCode = ManufacturingIdentity.PartCode(m.Path, m.PartCode),
                ProductDescription = m.ProductDescription ?? "",
                Quantity = qty,
                MassText = m.MassText ?? "",
                ColorCode = m.ColorCode ?? "",
                OperationType = NormalizeOperation(m.OperationType),
                SourcePath = m.Path ?? "",
                Configuration = m.Configuration ?? "",
                DrawingPath = ManufacturingIdentity.Drawing(m.Path, m.PartCode,
                    ManufacturingIdentity.PartCode(m.Path, m.PartCode), m.DrawingPath),
                SelectId = selectId ?? "",
                OccurrenceSelectIds = occurrenceSelectIds ?? new List<string>(),
                ParentPartCode = parent ?? ""
            };
        }

        internal static string NormalizeOperation(string value)
        {
            string v = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v)) return "";
            string u = v.ToUpperInvariant().Replace("İ", "I").Replace("ı", "I").Replace("Ş", "S").Replace("Ğ", "G").Replace("Ü", "U").Replace("Ö", "O").Replace("Ç", "C");
            if (u.Contains("KAYNAK")) return "KAYNAK";
            if (u.Contains("TALASLI") || u.Contains("TORNA") || u.Contains("FREZE")) return "TALAŞLI İMALAT";
            if (u.Contains("LAZER")) return "LAZER";
            // Eski/yanlış Kaynak cache'lerinden veya türetilmiş sac operasyonundan KESİM gelirse
            // Kaynak prosesinde bu parça LAZER olarak gösterilir. Lazer Hazırla'nın kendi
            // KESİM/KESİM+BÜKÜM davranışı bundan etkilenmez.
            if (u.Contains("KESIM")) return "LAZER";
            if (u.Contains("HAZIR")) return "HAZIR MALZEME";
            if (u.Contains("TESTERE")) return "İMALAT";
            if (u.Contains("IMALAT")) return "İMALAT";
            if (u.Contains("MONTAJ")) return "MONTAJ";
            return v.ToUpperInvariant();
        }

        private static bool IsWeld(string op) => string.Equals(NormalizeOperation(op), "KAYNAK", StringComparison.OrdinalIgnoreCase);

        private sealed class OccurrenceRecord
        {
            public int Index;
            public int ParentRecordIndex = -1;
            public string Path;
            public string Configuration;
            public string SelectId;
            public string ParentSelectId;
            public ComponentMetadata Meta;
        }
    }
}
