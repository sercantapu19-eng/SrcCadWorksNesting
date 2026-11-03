using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class ScanResult
    {
        public List<LaserPartRow> Rows { get; } = new List<LaserPartRow>();
        public List<string> Warnings { get; } = new List<string>();
        public int ComponentOccurrences { get; set; }
        public int UniquePartConfigurations { get; set; }
        public int LightweightOccurrences { get; set; }
        public int PassiveOccurrencesSkipped { get; set; }
        public int HiddenOccurrencesSkipped { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public string FinalResourceText { get; set; }
    }

    internal sealed class ScanProgress
    {
        public int Index { get; set; }
        public int Total { get; set; }
        public string PartName { get; set; }
        public LaserPartRow Row { get; set; }
        public bool CacheHit { get; set; }
        public string ResourceText { get; set; }
    }

    internal sealed class AssemblyScanner
    {
        private readonly SldWorks _swApp;
        public AssemblyScanner(SldWorks swApp) => _swApp = swApp;

        public ScanResult ScanActiveAssembly(Action<ScanProgress> progress = null, Func<bool> shouldCancel = null, bool forceRefresh = false, bool includeHidden = true, bool includePassive = false)
        {
            var result = new ScanResult();
            if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Tarama iptal edildi.");
            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Aktif belge bir SLDASM montajı olmalıdır.");

            var assembly = (AssemblyDoc)model;
            object raw = assembly.GetComponents(false);
            var components = raw as object[];
            if (components == null) return result;

            // Binlerce occurrence olsa bile ağır analiz yalnızca benzersiz dosya+konfigürasyon için bir kez yapılır.
            var groups = new Dictionary<string, Occurrence>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in components)
            {
                if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Tarama iptal edildi.");
                var comp = obj as Component2;
                if (comp == null) continue;
                try
                {
                    int suppression = -1;
                    try { suppression = comp.GetSuppression2(); }
                    catch { try { suppression = comp.GetSuppression(); } catch { } }
                    bool passive = suppression == (int)swComponentSuppressionState_e.swComponentSuppressed;
                    if (passive && !includePassive)
                    {
                        result.PassiveOccurrencesSkipped++;
                        continue;
                    }

                    if (!includeHidden && !IsEffectivelyVisible(comp))
                    {
                        result.HiddenOccurrencesSkipped++;
                        continue;
                    }

                    // Varsayılan kapsam: gizliler dahil, pasif/suppressed hariç.
                    // Kullanıcı arayüzündeki seçeneklerle her iki davranış da değiştirilebilir.
                    if (suppression == (int)swComponentSuppressionState_e.swComponentLightweight ||
                        suppression == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                        result.LightweightOccurrences++;

                    string path = "";
                    try { path = comp.GetPathName() ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        result.Warnings.Add("Sanal veya yolu olmayan bileşen atlandı: " + SafeName(comp));
                        continue;
                    }
                    if (!path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)) continue;

                    string cfg = "";
                    try { cfg = comp.ReferencedConfiguration ?? ""; } catch { }
                    string key = path + "|" + cfg;
                    Occurrence occurrence;
                    if (!groups.TryGetValue(key, out occurrence))
                        groups[key] = occurrence = new Occurrence { Path = path, Configuration = cfg };
                    occurrence.Quantity++;

                    // V1.11.21: Sonradan "İşlenenleri gizle" istendiğinde 20-30 bin bileşenli
                    // montajı tekrar GetComponents(false) ile dolaşmamak için component seçim
                    // kimliğini ilk taramada sakla. Önceden gizli occurrence'ları kaydetmiyoruz;
                    // böylece "Gizlenenleri geri getir" kullanıcı tarafından gizlenmiş parçaları açmaz.
                    try
                    {
                        if (IsSingleComponentVisible(comp))
                        {
                            string selectId = comp.GetSelectByIDString() ?? "";
                            if (!string.IsNullOrWhiteSpace(selectId)) occurrence.SelectIds.Add(selectId);
                        }
                    }
                    catch { }

                    result.ComponentOccurrences++;
                }
                finally { ComUtil.Release(comp); }
            }

            result.UniquePartConfigurations = groups.Count;
            var analyzer = new PartAnalyzer(_swApp, shouldCancel);
            var cache = new PartAnalysisCache();
            var resourceMonitor = new SolidWorksResourceMonitor(_swApp);
            var ordered = groups.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();

            try
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException("Tarama iptal edildi.");
                    var occurrence = ordered[i];
                    LaserPartRow row = null;
                    bool cacheHit = false;
                    try
                    {
                        if (!forceRefresh) cacheHit = cache.TryGet(occurrence.Path, occurrence.Configuration, occurrence.Quantity, out row);
                        if (cacheHit) result.CacheHits++;
                        else
                        {
                            result.CacheMisses++;
                            row = analyzer.Analyze(occurrence.Path, occurrence.Configuration, occurrence.Quantity);
                            cache.Put(occurrence.Path, occurrence.Configuration, row); // null = geçerli non-sheet negatif cache
                        }
                        if (row != null)
                        {
                            row.OccurrenceSelectIds = occurrence.SelectIds
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            row = LaserPartRow.AddByPartCode(result.Rows, row);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(Path.GetFileName(occurrence.Path) + ": " + ex.Message);
                        Logger.Error("Parça analizi başarısız: " + occurrence.Path, ex);
                    }

                    var snapshot = resourceMonitor.Checkpoint(i + 1);
                    progress?.Invoke(new ScanProgress
                    {
                        Index = i + 1,
                        Total = ordered.Count,
                        PartName = Path.GetFileNameWithoutExtension(occurrence.Path),
                        Row = row,
                        CacheHit = cacheHit,
                        ResourceText = snapshot.ShortText
                    });
                }
            }
            finally
            {
                cache.Save();
                try { result.FinalResourceText = resourceMonitor.Checkpoint(Math.Max(1, ordered.Count), true).ShortText; } catch { result.FinalResourceText = resourceMonitor.Snapshot().ShortText; }
            }


            return result;
        }

        internal static bool IsEffectivelyVisible(Component2 comp)
        {
            if (comp == null) return false;

            if (!IsSingleComponentVisible(comp)) return false;

            Component2 parent = null;
            try { parent = comp.GetParent() as Component2; } catch { parent = null; }

            while (parent != null)
            {
                Component2 next = null;
                try
                {
                    if (!IsSingleComponentVisible(parent)) return false;
                    try { next = parent.GetParent() as Component2; } catch { next = null; }
                }
                finally
                {
                    ComUtil.Release(parent);
                }
                parent = next;
            }

            return true;
        }

        private static bool IsSingleComponentVisible(Component2 comp)
        {
            // Bu helper yalnız "Gizlileri dahil et" kapalıyken kullanılır.
            // Suppressed davranışı includePassive parametresi tarafından ayrı yönetilir.
            try
            {
                if (comp.IsHidden(false)) return false;
            }
            catch { }

            try
            {
                int visibility = comp.Visible;
                if (visibility == (int)swComponentVisibilityState_e.swComponentHidden) return false;
            }
            catch { }

            return true;
        }

        private static string SafeName(Component2 c)
        {
            try { return c.Name2 ?? "(adsız)"; } catch { return "(adsız)"; }
        }

        private sealed class Occurrence
        {
            public string Path;
            public string Configuration;
            public int Quantity;
            public List<string> SelectIds { get; } = new List<string>();
        }
    }
}
