using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class ManufacturingAssemblyScanner
    {
        private readonly SldWorks _swApp;
        private readonly ComponentMetadataReader _metadataReader;
        private readonly WeldMetadataCache _cache = new WeldMetadataCache();
        private readonly Dictionary<string, ComponentMetadata> _local = new Dictionary<string, ComponentMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<bool> _cancel;
        private readonly Action<int, int, string> _progress;
        private readonly Action<ManufacturingRow, bool> _rowUpdate;
        private readonly bool _includeHidden, _includePassive;

        public ManufacturingAssemblyScanner(SldWorks swApp, Func<bool> cancel = null,
            Action<int, int, string> progress = null, Action<ManufacturingRow, bool> rowUpdate = null,
            bool includeHidden = true, bool includePassive = false)
        {
            _swApp = swApp;
            _metadataReader = new ComponentMetadataReader(swApp);
            _cancel = cancel;
            _progress = progress;
            _rowUpdate = rowUpdate;
            _includeHidden = includeHidden;
            _includePassive = includePassive;
        }

        public ManufacturingScanResult Scan()
        {
            var result = new ManufacturingScanResult();
            var model = _swApp.ActiveDoc as ModelDoc2;
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("İMALAT DOSYASI HAZIRLA için aktif bir SLDASM açın.");

            var byIdentity = new Dictionary<string, ManufacturingRow>(StringComparer.OrdinalIgnoreCase);
            var baseQuantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int sequence = 0, processed = 0;
            int total = 1;
            try { total = Math.Max(1, ((AssemblyDoc)model).GetComponentCount(false)); } catch { }
            Component2 root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2;
            if (root == null) throw new InvalidOperationException("Montaj kökü okunamadı.");
            Action<Component2> visit = null;
            visit = parent =>
            {
                ThrowIfCancelled();
                object[] children = parent.GetChildren() as object[];
                if (children == null) return;
                foreach (object obj in children)
                {
                    var comp = obj as Component2;
                    if (comp == null) { ComUtil.Release(obj); continue; }
                    try
                    {
                        ThrowIfCancelled();
                        int suppression = -1;
                        try { suppression = comp.GetSuppression2(); }
                        catch { try { suppression = comp.GetSuppression(); } catch { } }
                        if (!_includePassive && suppression == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                        if (!_includeHidden && !AssemblyScanner.IsEffectivelyVisible(comp)) continue;
                        string path = comp.GetPathName() ?? "";
                        string cfg = comp.ReferencedConfiguration ?? "";
                        processed++;
                        try { _progress?.Invoke(processed, total, Path.GetFileNameWithoutExtension(path)); } catch { }
                        if (path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
                        {
                            result.OccurrenceCount++;
                            ComponentMetadata meta = GetMetadata(path, cfg, comp);
                            if (meta != null && ManufacturingOperationClassifier.IsSaw(meta.OperationType))
                            {
                                result.SawOccurrenceCount++;
                                string code = Path.GetFileNameWithoutExtension(path);
                                var details = new SawPartDetails { ProfileType = meta.SawProfileType ?? "", HasHoleWizard = meta.SawHasHoleWizard == true };
                                string profile = details == null ? "" : (details.ProfileType ?? "").Trim();
                                if (string.IsNullOrWhiteSpace(profile))
                                {
                                    // Structural Member/Weldment profili yoksa TESTERE parçalarında Ürün Tanımı profil tipidir.
                                    profile = (meta.ProductDescription ?? "").Trim();
                                    if (string.IsNullOrWhiteSpace(profile))
                                        result.Warnings.Add((meta.PartCode ?? code) + " için profil bilgisi ve Ürün Tanımı bulunamadı.");
                                }

                                string partCode = ManufacturingIdentity.PartCode(path, meta.PartCode);
                                string identity = ManufacturingIdentity.Key(path, cfg);
                                string drawing = ManufacturingIdentity.Drawing(path, meta.PartCode, partCode, meta.DrawingPath);
                                Logger.Info("İmalat tarama: " + identity + " | özellik kodu=" + meta.PartCode + " | çıktı kodu=" + partCode + " | teknik resim=" + drawing);
                                ManufacturingRow row;
                                bool isNew = !byIdentity.TryGetValue(identity, out row);
                                if (isNew)
                                {
                                    sequence++;
                                    row = new ManufacturingRow
                                    {
                                        Sequence = sequence.ToString(),
                                        PartCode = partCode,
                                        Quantity = 0,
                                        ProfileType = profile ?? "",
                                        ProductDescription = meta.ProductDescription ?? "",
                                        ColorCode = meta.ColorCode ?? "",
                                        Route = (details != null && details.HasHoleWizard) ? "TESTERE+MATKAP" : "TESTERE",
                                        SourcePath = path,
                                        Configuration = cfg,
                                        DrawingPath = drawing
                                    };
                                    byIdentity[identity] = row;
                                    baseQuantities[identity] = 0;
                                    result.Rows.Add(row);
                                }
                                else
                                {
                                    // Only occurrences of the same file and configuration share a row.
                                    if (string.IsNullOrWhiteSpace(row.ProfileType) && !string.IsNullOrWhiteSpace(profile)) row.ProfileType = profile;
                                    if (string.IsNullOrWhiteSpace(row.ProductDescription) && !string.IsNullOrWhiteSpace(meta.ProductDescription)) row.ProductDescription = meta.ProductDescription;
                                    if (string.IsNullOrWhiteSpace(row.ColorCode) && !string.IsNullOrWhiteSpace(meta.ColorCode)) row.ColorCode = meta.ColorCode;
                                    if (string.IsNullOrWhiteSpace(row.DrawingPath) && !string.IsNullOrWhiteSpace(drawing)) row.DrawingPath = drawing;
                                    if (details != null && details.HasHoleWizard) row.Route = "TESTERE+MATKAP";
                                }

                                try
                                {
                                    string selectId = comp.GetSelectByIDString();
                                    if (!string.IsNullOrWhiteSpace(selectId) && !row.OccurrenceSelectIds.Contains(selectId))
                                        row.OccurrenceSelectIds.Add(selectId);
                                }
                                catch { }
                                int quantity = baseQuantities[identity] + 1;
                                baseQuantities[identity] = quantity;
                                row.Quantity = quantity;
                                try { _rowUpdate?.Invoke(row, isNew); } catch { }
                                System.Windows.Forms.Application.DoEvents();

                            }
                        }
                        visit(comp);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result.Warnings.Add("İmalat bileşeni okunamadı: " + ex.Message); }
                    finally { ComUtil.Release(comp); }
                    if ((processed % 10) == 0) System.Windows.Forms.Application.DoEvents();
                }
            };
            try { visit(root); }
            finally { ComUtil.Release(root); }

            if (result.Rows.Count == 0)
                result.Warnings.Add("Aktif montajda İşlem Tipi = TESTERE olan parça bulunamadı.");
            try { _cache.Save(); } catch { }
            return result;
        }

        private ComponentMetadata GetMetadata(string path, string cfg, Component2 comp)
        {
            string key = (path ?? "") + "|" + (cfg ?? "");
            ComponentMetadata meta;
            if (_local.TryGetValue(key, out meta)) return meta;
            // Always read live metadata; reuse only within this scan.
            meta = _metadataReader.Read(path, cfg, comp, _cancel, true);
            _local[key] = meta;
            if (meta != null) _cache.Put(meta);
            return meta;
        }

        private void ThrowIfCancelled()
        {
            if (_cancel != null && _cancel()) throw new OperationCanceledException("İmalat taraması ESC ile iptal edildi.");
        }

    }
}
