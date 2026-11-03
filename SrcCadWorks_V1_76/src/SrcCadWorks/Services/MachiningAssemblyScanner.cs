using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class MachiningAssemblyScanner
    {
        private readonly SldWorks _swApp;
        private readonly ComponentMetadataReader _reader;
        private readonly WeldMetadataCache _cache = new WeldMetadataCache();
        private readonly Dictionary<string, ComponentMetadata> _local = new Dictionary<string, ComponentMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RootAccumulator> _roots = new Dictionary<string, RootAccumulator>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _folderNameCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        private readonly Func<bool> _cancel;
        private readonly Action<int, int, string> _progress;
        private readonly Action<MachiningRow, bool> _rowUpdate;
        private readonly bool _includeHidden, _includePassive;
        private readonly HashSet<string> _usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _processed;
        private int _total = 1;
        private int _nextRootNo;

        public MachiningAssemblyScanner(SldWorks swApp, Func<bool> cancel = null,
            Action<int, int, string> progress = null, Action<MachiningRow, bool> rowUpdate = null,
            bool includeHidden = true, bool includePassive = false)
        {
            _swApp = swApp;
            _reader = new ComponentMetadataReader(swApp);
            _cancel = cancel;
            _progress = progress;
            _rowUpdate = rowUpdate;
            _includeHidden = includeHidden;
            _includePassive = includePassive;
        }

        public MachiningScanResult Scan()
        {
            var result = new MachiningScanResult();
            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("TALAŞLI İMALAT DOSYASI HAZIRLA için aktif bir SLDASM açın.");

            // GetComponents(false) ile ikinci bir tam COM dizisi üretmek yerine yalnız sayı alınır.
            // Bu, büyük montajlarda önceki gereksiz çift dolaşımı ortadan kaldırır.
            try { _total = Math.Max(1, ((AssemblyDoc)model).GetComponentCount(false)); }
            catch { _total = 1; }

            Component2 root = null;
            try
            {
                root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2;
                if (root == null) throw new InvalidOperationException("Aktif montaj kökü okunamadı.");
                // Aktif TALAŞLI montaj da üretim satırıdır; yalnız çocuklardan başlamak
                // ana montajı kaybettirir ve alt montajları bağımsız kök gibi gösterir.
                RootAccumulator activeRoot = null;
                string activePath = model.GetPathName() ?? "";
                string activeConfiguration = model.ConfigurationManager.ActiveConfiguration.Name ?? "";
                if (!string.IsNullOrWhiteSpace(activePath))
                {
                    // Aktif belgenin güncel özelliklerini oku; eski cache ile kök tipi belirleme.
                    var activeMeta = _reader.ReadLoadedOnly(activePath, activeConfiguration, root);
                    if (activeMeta != null && ManufacturingOperationClassifier.IsMachining(activeMeta.OperationType))
                    {
                        _processed++;
                        _total++;
                        result.OccurrenceCount++;
                        activeRoot = GetOrCreateRoot(activeMeta, result);
                        activeRoot.RootCount = 1;
                        activeRoot.RootRow.Quantity = 1;
                        _rowUpdate?.Invoke(activeRoot.RootRow, true);
                        _progress?.Invoke(_processed, _total, activeMeta.PartCode);
                    }
                }
                Traverse(root, activeRoot, result);
            }
            finally { ComUtil.Release(root); }

            result.RootCount = result.RootRows.Count;
            result.PdfItemCount = result.Rows.Count;
            if (result.RootRows.Count == 0)
                result.Warnings.Add("Aktif montajda İşlem Tipi = TALAŞLI İMALAT olan kök parça/montaj bulunamadı.");
            try { _cache.Save(); } catch { }
            return result;
        }

        private void Traverse(Component2 parent, RootAccumulator activeRoot, MachiningScanResult result)
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
                    try { suppression = comp.GetSuppression2(); } catch { }
                    if (!_includePassive && suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                    if (!_includeHidden && !AssemblyScanner.IsEffectivelyVisible(comp)) continue;

                    string path = ""; try { path = comp.GetPathName() ?? ""; } catch { }
                    string cfg = ""; try { cfg = comp.ReferencedConfiguration ?? ""; } catch { }
                    RootAccumulator nextRoot = activeRoot;

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var meta = GetMetadata(path, cfg, comp);
                        result.OccurrenceCount++;
                        _processed++;
                        try { _progress?.Invoke(_processed, _total, meta == null ? Path.GetFileNameWithoutExtension(path) : meta.PartCode); } catch { }

                        if (meta != null && ManufacturingOperationClassifier.IsMachining(meta.OperationType))
                        {
                            if (activeRoot == null)
                            {
                                RootAccumulator acc = GetOrCreateRoot(meta, result);
                                acc.RootCount++;
                                acc.RootRow.Quantity = acc.RootCount;
                                try { _rowUpdate?.Invoke(acc.RootRow, acc.RootCount == 1); } catch { }
                                nextRoot = acc;
                            }
                            else
                            {
                                // KÖK satırın kendisi yeniden görülürse çocuk satır oluşturma.
                                if (!SameIdentity(meta, activeRoot.RootMeta))
                                {
                                    ItemAccumulator item = activeRoot.AddOrUpdate(meta, _processed, result, _rowUpdate);
                                    if (item != null) nextRoot = activeRoot;
                                }
                            }
                        }
                    }

                    // Tüm alt seviyeler taranır. Arada TALAŞLI olmayan montajlar olsa bile aktif kök korunur.
                    Traverse(comp, nextRoot, result);
                    if ((_processed % 10) == 0) System.Windows.Forms.Application.DoEvents();
                }
                finally { ComUtil.Release(comp); }
            }
        }

        private RootAccumulator GetOrCreateRoot(ComponentMetadata meta, MachiningScanResult result)
        {
            string key = RootKey(meta);
            RootAccumulator acc;
            if (_roots.TryGetValue(key, out acc)) return acc;

            _nextRootNo++;
            string folderName = MakeUniqueFolderName(BuildFolderName(meta));
            var rootRow = ToRow(meta, _nextRootNo.ToString(), true, 0, 0,
                meta.PartCode, folderName);

            acc = new RootAccumulator
            {
                FirstIndex = _processed,
                RootMeta = meta,
                RootCount = 0,
                FolderName = folderName,
                RootNo = _nextRootNo,
                RootRow = rootRow
            };
            _roots[key] = acc;
            result.Rows.Add(rootRow);
            result.RootRows.Add(rootRow);
            return acc;
        }

        private string MakeUniqueFolderName(string baseFolderName)
        {
            if (string.IsNullOrWhiteSpace(baseFolderName)) baseFolderName = "TALAŞLI İMALAT";
            int index;
            if (!_folderNameCounts.TryGetValue(baseFolderName, out index)) index = 0;
            string candidate;
            do
            {
                index++;
                candidate = index == 1 ? baseFolderName : baseFolderName + " (" + index + ")";
            } while (!_usedFolders.Add(candidate));
            _folderNameCounts[baseFolderName] = index;
            return candidate;
        }

        private ComponentMetadata GetMetadata(string path, string cfg, Component2 comp)
        {
            string key = (path ?? "") + "|" + (cfg ?? "");
            ComponentMetadata meta;
            if (_local.TryGetValue(key, out meta)) return meta;
            // Always read live metadata; reuse only within this scan.
            meta = _reader.Read(path, cfg, comp, _cancel);
            _local[key] = meta;
            if (meta != null) _cache.Put(meta);
            return meta;
        }

        private static MachiningRow ToRow(ComponentMetadata meta, string sequence, bool root, int depth,
            int quantity, string rootCode, string folder)
        {
            return new MachiningRow
            {
                Sequence = sequence,
                IsRoot = root,
                Depth = depth,
                StepSelected = false,
                RootPartCode = rootCode ?? "",
                FolderName = folder ?? "",
                PartCode = meta == null ? "" : (meta.PartCode ?? ""),
                Quantity = quantity,
                ProductDescription = meta == null ? "" : (meta.ProductDescription ?? ""),
                ColorCode = meta == null ? "" : (meta.ColorCode ?? ""),
                SourcePath = meta == null ? "" : (meta.Path ?? ""),
                Configuration = meta == null ? "" : (meta.Configuration ?? ""),
                DrawingPath = meta == null ? "" : (meta.DrawingPath ?? "")
            };
        }

        private static string RootKey(ComponentMetadata meta)
        {
            if (meta == null) return Guid.NewGuid().ToString("N");
            if (!string.IsNullOrWhiteSpace(meta.PartCode)) return meta.PartCode.Trim();
            return (meta.Path ?? "") + "|" + (meta.Configuration ?? "");
        }

        private static bool SameIdentity(ComponentMetadata a, ComponentMetadata b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Path ?? "", b.Path ?? "", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Configuration ?? "", b.Configuration ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildFolderName(ComponentMetadata meta)
        {
            string value = meta == null ? "" : (meta.ProductDescription ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value)) value = meta == null ? "TALAŞLI İMALAT" : (meta.PartCode ?? "TALAŞLI İMALAT");
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = value.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(value) ? "TALAŞLI İMALAT" : value;
        }

        private void ThrowIfCancelled()
        {
            if (_cancel != null && _cancel()) throw new OperationCanceledException("Talaşlı imalat taraması ESC ile iptal edildi.");
        }

        private sealed class RootAccumulator
        {
            public int FirstIndex;
            public ComponentMetadata RootMeta;
            public int RootCount;
            public string FolderName;
            public int RootNo;
            public MachiningRow RootRow;
            public int NextChildNo;
            public readonly Dictionary<string, ItemAccumulator> Items = new Dictionary<string, ItemAccumulator>(StringComparer.OrdinalIgnoreCase);

            public ItemAccumulator AddOrUpdate(ComponentMetadata meta, int index, MachiningScanResult result,
                Action<MachiningRow, bool> rowUpdate)
            {
                string key = (meta.Path ?? "") + "|" + (meta.Configuration ?? "");
                ItemAccumulator item;
                bool isNew = !Items.TryGetValue(key, out item);
                if (isNew)
                {
                    NextChildNo++;
                    var row = ToRow(meta, RootNo + "." + NextChildNo, false, 1, 0,
                        RootMeta == null ? "" : (RootMeta.PartCode ?? ""), FolderName);
                    item = new ItemAccumulator { Meta = meta, FirstIndex = index, Row = row };
                    Items[key] = item;
                    result.Rows.Add(row);
                }
                item.Count++;
                item.Row.Quantity = item.Count;
                try { rowUpdate?.Invoke(item.Row, isNew); } catch { }
                return item;
            }
        }

        private sealed class ItemAccumulator
        {
            public int FirstIndex;
            public int Count;
            public ComponentMetadata Meta;
            public MachiningRow Row;
        }
    }
}
