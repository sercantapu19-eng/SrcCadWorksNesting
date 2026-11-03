using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class AssemblyVisibilityTracker : IDisposable
    {
        private readonly SldWorks _swApp;
        private ModelDoc2 _assemblyModel;
        private readonly HashSet<string> _hiddenSelectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AssemblyVisibilityTracker(SldWorks swApp)
        {
            _swApp = swApp;
            var active = _swApp == null ? null : _swApp.ActiveDoc as ModelDoc2;
            if (active == null || active.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("İşlenen parçaları gizlemek için aktif belge bir SLDASM montajı olmalıdır.");
            _assemblyModel = active;
        }

        public int HiddenOccurrenceCount => _hiddenSelectIds.Count;

        public List<string> VerifyHidden(IEnumerable<LaserPartRow> rows)
        {
            var failures = new List<string>();
            var requested = rows.Where(r => r != null).ToList();
            var found = new HashSet<LaserPartRow>();
            var visible = new HashSet<LaserPartRow>();
            var assembly = _assemblyModel as AssemblyDoc;
            var components = assembly == null ? null : assembly.GetComponents(false) as object[];
            if (components == null) throw new InvalidOperationException("Gizleme sonrası montaj doğrulanamadı.");
            foreach (var obj in components)
            {
                var comp = obj as Component2;
                try
                {
                    if (comp == null) continue;
                    foreach (var row in requested.Where(r => Matches(comp, r)))
                    {
                        found.Add(row);
                        if (comp.GetSuppression() != (int)swComponentSuppressionState_e.swComponentSuppressed &&
                            IsEffectivelyVisible(comp)) visible.Add(row);
                    }
                }
                finally { ComUtil.Release(obj); }
            }
            foreach (var row in requested)
            {
                if (!found.Contains(row)) failures.Add(row.PartCode + ": gizlenecek bileşen montajda bulunamadı");
                else if (visible.Contains(row)) failures.Add(row.PartCode + ": montajda görünür kalan bileşen var");
            }
            return failures;
        }

        public int HideOccurrences(LaserPartRow row)
        {
            return HideOccurrencesBatch(new[] { row });
        }

        public int HideOccurrencesBatch(IEnumerable<LaserPartRow> rows, Action<int, int> progress = null)
        {
            if (_assemblyModel == null || rows == null) return 0;
            var rowList = rows.Where(r => r != null).SelectMany(r => r.SourceOccurrences != null && r.SourceOccurrences.Count > 0
                ? (IEnumerable<LaserPartRow>)r.SourceOccurrences : new[] { r }).ToList();
            if (rowList.Count == 0) return 0;
            var owners = new Dictionary<string, LaserPartRow>(StringComparer.OrdinalIgnoreCase);
            var retry = new HashSet<LaserPartRow>();
            foreach (var row in rowList)
            {
                var ids = (row.OccurrenceSelectIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();
                if (ids.Count == 0) retry.Add(row);
                foreach (string id in ids) owners[id] = row;
            }

            int changed = HideBySelectIds(owners, retry, progress);
            // Only failed/missing identities use a full traversal. All retry rows share
            // one traversal, including rows without identities in mixed batches.
            if (retry.Count > 0)
            {
                Logger.Info("Gizleme: dosya yolu/konfigürasyon ile yeniden arama: " +
                    string.Join(", ", retry.Select(row => row.PartCode)));
                changed += HideOccurrencesBatchLegacy(retry, progress);
            }
            return changed;
        }

        private int HideBySelectIds(Dictionary<string, LaserPartRow> owners,
            HashSet<LaserPartRow> retry, Action<int, int> progress)
        {
            if (owners.Count == 0) return 0;
            ModelDocExtension ext = null;
            SelectionMgr selection = null;
            var pending = new List<KeyValuePair<string, Component2>>();
            int changed = 0, processed = 0;
            try
            {
                ext = _assemblyModel.Extension;
                selection = _assemblyModel.SelectionManager as SelectionMgr;
                if (ext == null || selection == null)
                {
                    foreach (var row in owners.Values) retry.Add(row);
                    return 0;
                }
                _assemblyModel.ClearSelection2(true);
                foreach (var item in owners)
                {
                    Component2 component = null;
                    try
                    {
                        if (ext.SelectByID2(item.Key, "COMPONENT", 0, 0, 0, pending.Count > 0,
                            0, null, (int)swSelectOption_e.swSelectOptionDefault))
                        {
                            component = selection.GetSelectedObjectsComponent4(
                                selection.GetSelectedObjectCount2(-1), -1) as Component2;
                        }
                        if (component == null || !Matches(component, item.Value))
                        {
                            retry.Add(item.Value);
                            // Never hide an unverified selection, even if the old ID now
                            // resolves to a different component. Rebuild at next iteration.
                            FlushPending(pending, owners, retry, ref changed, false);
                        }
                        else if (!IsEffectivelyVisible(component))
                        {
                            // Already hidden before this call: do not count or restore it.
                            FlushPending(pending, owners, retry, ref changed, false);
                        }
                        else
                        {
                            pending.Add(new KeyValuePair<string, Component2>(item.Key, component));
                            component = null; // FlushPending owns this COM reference.
                        }
                    }
                    catch (Exception ex)
                    {
                        retry.Add(item.Value);
                        Logger.Info("Bileşen seçimi doğrulanamadı: " + item.Key + " | " + ex.Message);
                        FlushPending(pending, owners, retry, ref changed, false);
                    }
                    finally { ComUtil.Release(component); }
                    processed++;
                    if (pending.Count >= 64 || processed == owners.Count)
                        FlushPending(pending, owners, retry, ref changed, true);
                    if (processed % 64 == 0 || processed == owners.Count)
                        try { progress?.Invoke(processed, owners.Count); } catch { }
                }
            }
            catch (Exception ex)
            {
                foreach (var row in owners.Values) retry.Add(row);
                Logger.Info("Gizleme seçim yolu başarısız: " + ex.Message);
            }
            finally
            {
                foreach (var item in pending) ComUtil.Release(item.Value);
                try { _assemblyModel.ClearSelection2(true); } catch { }
                ComUtil.Release(selection);
                ComUtil.Release(ext);
            }
            return changed;
        }

        private void FlushPending(List<KeyValuePair<string, Component2>> pending,
            Dictionary<string, LaserPartRow> owners, HashSet<LaserPartRow> retry,
            ref int changed, bool selectionVerified)
        {
            if (selectionVerified && pending.Count > 0)
                try { _assemblyModel.HideComponent2(); }
                catch (Exception ex) { Logger.Info("Toplu gizleme komutu: " + ex.Message); }
            foreach (var item in pending)
            {
                try
                {
                    // A successful command return alone is not evidence of hiding.
                    // Use the component itself if selection/active-document context failed.
                    if (!IsHiddenConfirmed(item.Value))
                        item.Value.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
                    if (IsHiddenConfirmed(item.Value))
                    {
                        _hiddenSelectIds.Add(item.Key);
                        changed++;
                    }
                    else retry.Add(owners[item.Key]);
                }
                catch (Exception ex)
                {
                    retry.Add(owners[item.Key]);
                    Logger.Info("Bileşen gizlenemedi: " + item.Key + " | " + ex.Message);
                }
                finally { ComUtil.Release(item.Value); }
            }
            pending.Clear();
            try { _assemblyModel.ClearSelection2(true); } catch { }
        }

        private static bool IsHiddenConfirmed(Component2 component)
        {
            try { return component.Visible == (int)swComponentVisibilityState_e.swComponentHidden; }
            catch { return false; }
        }

        private int HideOccurrencesBatchLegacy(IEnumerable<LaserPartRow> rows, Action<int, int> progress = null)
        {
            var assembly = _assemblyModel as AssemblyDoc;
            if (assembly == null || rows == null) return 0;

            var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anyConfiguration = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.SourcePath)) continue;
                string path = NormalizePath(row.SourcePath);
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (string.IsNullOrWhiteSpace(row.Configuration)) anyConfiguration.Add(path);
                else exact.Add(path + "|" + (row.Configuration ?? "").Trim());
            }
            if (exact.Count == 0 && anyConfiguration.Count == 0) return 0;

            object raw = null;
            try { raw = assembly.GetComponents(false); } catch (Exception ex) { Logger.Info("Gizleme yeniden araması başarısız: " + ex.Message); return 0; }
            var components = raw as object[];
            if (components == null) return 0;

            int changed = 0;
            int processed = 0;
            int total = components.Length;
            var foundExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in components)
            {
                var comp = obj as Component2;
                if (comp == null) { processed++; continue; }
                try
                {
                    string path = "";
                    string cfg = "";
                    try { path = NormalizePath(comp.GetPathName() ?? ""); } catch { path = ""; }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try { cfg = (comp.ReferencedConfiguration ?? "").Trim(); } catch { cfg = ""; }
                    if (!anyConfiguration.Contains(path) && !exact.Contains(path + "|" + cfg)) continue;
                    foundExact.Add(path + "|" + cfg);
                    foundPaths.Add(path);

                    int suppression = -1;
                    try { suppression = comp.GetSuppression2(); }
                    catch { try { suppression = comp.GetSuppression(); } catch { } }
                    if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                    if (!IsEffectivelyVisible(comp)) continue;

                    string selectId = SafeSelectId(comp);
                    string occurrenceName = SafeName(comp);
                    try
                    {
                        comp.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
                        if (!IsHiddenConfirmed(comp))
                        {
                            Logger.Info("Gizleme doğrulanamadı: " + path + " | " + cfg + " | " + occurrenceName);
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(selectId)) _hiddenSelectIds.Add(selectId);
                        else if (!string.IsNullOrWhiteSpace(occurrenceName)) _hiddenSelectIds.Add(occurrenceName);
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("Bileşen toplu gizlenemedi: " + occurrenceName + " | " + ex.Message);
                    }
                }
                finally
                {
                    ComUtil.Release(comp);
                    processed++;
                    if ((processed % 100) == 0 || processed == total)
                    {
                        try { progress?.Invoke(processed, total); } catch { }
                    }
                }
            }
            foreach (string key in exact.Where(key => !foundExact.Contains(key)))
                Logger.Info("Gizlenecek bileşen montajda bulunamadı: " + key);
            foreach (string path in anyConfiguration.Where(path => !foundPaths.Contains(path)))
                Logger.Info("Gizlenecek dosya montajda bulunamadı: " + path);
            return changed;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return System.IO.Path.GetFullPath(path).Trim(); }
            catch { return path.Trim(); }
        }

        public int RestoreAll()
        {
            if (_assemblyModel == null || _hiddenSelectIds.Count == 0) return 0;
            var ids = _hiddenSelectIds.ToList();
            var ext = _assemblyModel.Extension;
            if (ext == null) return 0;

            const int batchSize = 64;
            int restored = 0;
            int selectedInBatch = 0;
            int processed = 0;
            try { _assemblyModel.ClearSelection2(true); } catch { }

            foreach (string selectId in ids)
            {
                bool selected = false;
                try
                {
                    selected = ext.SelectByID2(selectId, "COMPONENT", 0, 0, 0, selectedInBatch > 0, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                }
                catch { }
                if (selected)
                {
                    selectedInBatch++;
                    restored++;
                }

                processed++;
                bool flush = selectedInBatch >= batchSize || processed == ids.Count;
                if (flush && selectedInBatch > 0)
                {
                    try { _assemblyModel.ShowComponent2(); } catch { }
                    try { _assemblyModel.ClearSelection2(true); } catch { }
                    selectedInBatch = 0;
                }
            }

            try { _assemblyModel.ClearSelection2(true); } catch { }
            _hiddenSelectIds.Clear();
            Redraw();
            return restored;
        }

        public void Redraw()
        {
            try { _assemblyModel?.GraphicsRedraw2(); } catch { }
        }

        private static bool Matches(Component2 comp, LaserPartRow row)
        {
            string path = "";
            string cfg = "";
            try { path = comp.GetPathName() ?? ""; } catch { }
            if (!string.Equals(NormalizePath(path), NormalizePath(row.SourcePath), StringComparison.OrdinalIgnoreCase)) return false;
            try { cfg = comp.ReferencedConfiguration ?? ""; } catch { }
            if (!string.IsNullOrWhiteSpace(row.Configuration) &&
                !string.Equals(cfg.Trim(), row.Configuration.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static bool IsEffectivelyVisible(Component2 comp)
        {
            if (comp == null || !IsSingleVisible(comp)) return false;
            Component2 parent = null;
            try { parent = comp.GetParent() as Component2; } catch { parent = null; }
            while (parent != null)
            {
                Component2 next = null;
                try
                {
                    if (!IsSingleVisible(parent)) return false;
                    try { next = parent.GetParent() as Component2; } catch { next = null; }
                }
                finally { ComUtil.Release(parent); }
                parent = next;
            }
            return true;
        }

        private static bool IsSingleVisible(Component2 comp)
        {
            try { if (comp.IsHidden(false)) return false; } catch { }
            try { if (comp.Visible == (int)swComponentVisibilityState_e.swComponentHidden) return false; } catch { }
            return true;
        }

        private static string SafeSelectId(Component2 comp)
        {
            try { return comp == null ? "" : (comp.GetSelectByIDString() ?? ""); } catch { return ""; }
        }

        private static string SafeName(Component2 comp)
        {
            try { return comp.Name2 ?? ""; } catch { return ""; }
        }

        public void Dispose()
        {
            try { ComUtil.Release(_assemblyModel); } catch { }
            _assemblyModel = null;
        }
    }
}
