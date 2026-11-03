using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Xml.Serialization;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class ProductionRecoveryManager
    {
        private readonly object _sync = new object();
        private ProductionRecoveryState _state;

        public void Begin(LaserProjectInfo info, string operationName, string outputFolder, string templatePath,
            ProductionPackageOptions options, IEnumerable<LaserPartRow> selectedRows, int hangThresholdSeconds,
            IEnumerable<string> recoverySkippedPartCodes = null, ProductionRecoveryState previousState = null)
        {
            lock (_sync)
            {
                ProductionRecoveryStore.ClearHangMarker();
                string runId = previousState != null && !string.IsNullOrWhiteSpace(previousState.RunId)
                    ? previousState.RunId.Trim()
                    : Guid.NewGuid().ToString("N");
                _state = new ProductionRecoveryState
                {
                    RunId = runId,
                    HostProcessId = Process.GetCurrentProcess().Id,
                    OperationName = operationName ?? "Üretim Paketi",
                    ProjectName = info == null ? "" : info.ProjectName,
                    FileName = info == null ? "" : info.FileName,
                    FileCode = info == null ? "" : info.FileCode,
                    OutputFolder = outputFolder ?? "",
                    TemplatePath = templatePath ?? "",
                    StartedUtc = DateTime.UtcNow,
                    LastProgressUtc = DateTime.UtcNow,
                    Stage = "HAZIRLIK",
                    Index = 0,
                    Total = Math.Max(1, selectedRows == null ? 1 : selectedRows.Count()),
                    OverallIndex = 0,
                    OverallTotal = Math.Max(1, selectedRows == null ? 1 : selectedRows.Count()),
                    HangThresholdSeconds = Math.Max(30, hangThresholdSeconds),
                    ExportDxf = options != null && options.ExportDxf,
                    ExportPdf = options != null && options.ExportPdf,
                    ExportExcel = options != null && options.ExportExcel,
                    IncludeBendLines = options != null && options.IncludeBendLines,
                    ExportCombinedDxf = options != null && options.ExportCombinedDxf,
                    ExportCombinedDwg = options != null && options.ExportCombinedDwg,
                    ProcessingSpeed = options == null ? ProcessingSpeedMode.Normal : options.ProcessingSpeed,
                    SelectedPartCodes = selectedRows == null
                        ? new List<string>()
                        : selectedRows.Select(r => r == null ? "" : r.PartCode)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    // Legacy field is retained only for reading older files. New runs use recovery keys.
                    RecoverySkippedPartCodes = new List<string>(),
                    RecoverySkippedPartKeys = recoverySkippedPartCodes == null
                        ? new List<string>()
                        : recoverySkippedPartCodes.Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    SelectedParts = selectedRows == null
                        ? new List<ProductionRecoveryPartState>()
                        : selectedRows.Where(r => r != null).Select(r => new ProductionRecoveryPartState
                        {
                            PartCode = r.PartCode ?? "",
                            SourcePath = r.SourcePath ?? "",
                            Configuration = r.Configuration ?? "",
                            SourceSha256 = OutputProvenanceStore.ComputeFileSha256(r.SourcePath),
                            RecoveryKey = OutputProvenanceStore.BuildRecoveryKey(r)
                        }).ToList(),
                    CompletedDxfPartCodes = previousState == null || previousState.CompletedDxfPartCodes == null
                        ? new List<string>() : new List<string>(previousState.CompletedDxfPartCodes),
                    CompletedPdfPartCodes = previousState == null || previousState.CompletedPdfPartCodes == null
                        ? new List<string>() : new List<string>(previousState.CompletedPdfPartCodes),
                    CompletedCombinedDxf = previousState != null && previousState.CompletedCombinedDxf,
                    CompletedCombinedDwg = previousState != null && previousState.CompletedCombinedDwg,
                    CompletedExcel = previousState != null && previousState.CompletedExcel
                };
                ProductionRecoveryStore.Save(_state);
            }
        }

        public void Touch(PackageProgress p)
        {
            if (p == null) return;
            lock (_sync)
            {
                if (_state == null) return;
                _state.Stage = p.Stage ?? _state.Stage;
                _state.PartCode = p.PartCode ?? "";
                _state.CurrentPartRecoveryKey = "";
                if (!string.IsNullOrWhiteSpace(_state.PartCode) && _state.SelectedParts != null)
                {
                    var identity = _state.SelectedParts.FirstOrDefault(x =>
                        x != null && string.Equals(x.PartCode ?? "", _state.PartCode, StringComparison.OrdinalIgnoreCase));
                    if (identity != null) _state.CurrentPartRecoveryKey = identity.RecoveryKey ?? "";
                }
                _state.Index = p.Index;
                _state.Total = p.Total;
                _state.OverallIndex = p.OverallIndex;
                _state.OverallTotal = p.OverallTotal;
                _state.LastProgressUtc = DateTime.UtcNow;
                _state.HangDetected = false;
                _state.HangDetectedUtc = DateTime.MinValue;
                ProductionRecoveryStore.ClearHangMarker();
                ProductionRecoveryStore.Save(_state);
            }
        }

        public int RestoreCachedOutputs(string outputFolder)
        {
            lock (_sync)
            {
                if (_state == null || string.IsNullOrWhiteSpace(_state.RunId) || string.IsNullOrWhiteSpace(outputFolder)) return 0;
                return ProductionRecoveryStore.RestoreCache(_state.RunId, outputFolder);
            }
        }

        public void MarkOutputCommitted(string stage, string partCode, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            lock (_sync)
            {
                if (_state == null) return;
                string normalizedStage = (stage ?? "").Trim().ToUpperInvariant();
                string code = (partCode ?? "").Trim();
                if (normalizedStage == "DXF" && code.Length > 0)
                    AddUnique(_state.CompletedDxfPartCodes, code);
                else if (normalizedStage == "PDF" && code.Length > 0)
                    AddUnique(_state.CompletedPdfPartCodes, code);
                else if (normalizedStage == "TOPLU_DXF")
                    _state.CompletedCombinedDxf = true;
                else if (normalizedStage == "TOPLU_DWG")
                    _state.CompletedCombinedDwg = true;
                else if (normalizedStage == "EXCEL")
                    _state.CompletedExcel = true;

                _state.LastProgressUtc = DateTime.UtcNow;
                // Explicit interrupted-run ledger owns output reuse.
                ProductionRecoveryStore.Save(_state);
            }
        }

        private static void AddUnique(IList<string> items, string value)
        {
            if (items == null || string.IsNullOrWhiteSpace(value)) return;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase)) return;
            items.Add(value);
        }

        public ProductionRecoveryState Snapshot()
        {
            lock (_sync)
            {
                return ProductionRecoveryStore.Clone(_state);
            }
        }

        public bool TryMarkHung(DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_state == null || _state.HangDetected) return false;
                int threshold = Math.Max(30, _state.HangThresholdSeconds);
                if ((nowUtc - _state.LastProgressUtc).TotalSeconds < threshold) return false;
                _state.HangDetected = true;
                _state.HangDetectedUtc = nowUtc;
                ProductionRecoveryStore.WriteHangMarker(_state, nowUtc);
                ProductionRecoveryStore.Save(_state);
                return true;
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                string runId = _state == null ? "" : (_state.RunId ?? "");
                _state = null;
                ProductionRecoveryStore.Delete();
                ProductionRecoveryStore.DeleteCache(runId);
            }
        }
    }

    internal static class ProductionRecoveryStore
    {
        public static string RecoveryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "recovery", "production_recovery.xml");

        public static string HangMarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "recovery", "production_recovery.hung");


        public static string CacheRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "recovery", "output_cache");

        public static void CacheOutput(string runId, string stage, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;
            string safeStage = string.IsNullOrWhiteSpace(stage) ? "OTHER" : stage.Trim().ToUpperInvariant();
            foreach (char c in Path.GetInvalidFileNameChars()) safeStage = safeStage.Replace(c, '_');
            string dir = Path.Combine(CacheRoot, runId, safeStage);
            Directory.CreateDirectory(dir);
            string destination = Path.Combine(dir, Path.GetFileName(sourcePath));
            string temp = destination + ".tmp";
            File.Copy(sourcePath, temp, true);
            try { File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(sourcePath)); } catch { }
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
        }

        public static int RestoreCache(string runId, string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(outputFolder)) return 0;
            string root = Path.Combine(CacheRoot, runId);
            if (!Directory.Exists(root)) return 0;
            Directory.CreateDirectory(outputFolder);
            int restored = 0;
            foreach (string stageDir in Directory.GetDirectories(root))
            {
                foreach (string cached in Directory.GetFiles(stageDir))
                {
                    if (cached.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        string target = Path.Combine(outputFolder, Path.GetFileName(cached));
                        if (File.Exists(target) && OutputFileValidator.IsUsable(target)) continue;
                        File.Copy(cached, target, true);
                        try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(cached)); } catch { }
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("Kurtarma cache dosyası geri yüklenemedi: " + cached + " | " + ex.Message);
                    }
                }
            }
            return restored;
        }

        public static void DeleteCache(string runId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(runId)) return;
                string dir = Path.Combine(CacheRoot, runId);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { Logger.Info("Üretim kurtarma cache'i silinemedi: " + ex.Message); }
        }

        public static ProductionRecoveryState Load()
        {
            try
            {
                if (!File.Exists(RecoveryPath)) return null;
                ProductionRecoveryState state;
                using (var stream = new FileStream(RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    state = (ProductionRecoveryState)new XmlSerializer(typeof(ProductionRecoveryState)).Deserialize(stream);
                ApplyHangMarker(state);
                return state;
            }
            catch (Exception ex)
            {
                Logger.Info("Üretim kurtarma kaydı okunamadı: " + ex.Message);
                return null;
            }
        }

        public static void Save(ProductionRecoveryState state)
        {
            if (state == null) return;
            try
            {
                string dir = Path.GetDirectoryName(RecoveryPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                string temp = RecoveryPath + ".tmp";
                string backup = RecoveryPath + ".bak";
                using (var stream = File.Create(temp))
                    new XmlSerializer(typeof(ProductionRecoveryState)).Serialize(stream, state);

                if (!File.Exists(RecoveryPath))
                {
                    File.Move(temp, RecoveryPath);
                }
                else
                {
                    try
                    {
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                        File.Replace(temp, RecoveryPath, backup, true);
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    }
                    catch
                    {
                        File.Copy(temp, RecoveryPath, true);
                        File.Delete(temp);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Üretim kurtarma kaydı yazılamadı: " + ex.Message);
            }
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
                if (File.Exists(RecoveryPath + ".tmp")) File.Delete(RecoveryPath + ".tmp");
                if (File.Exists(RecoveryPath + ".bak")) File.Delete(RecoveryPath + ".bak");
                if (File.Exists(HangMarkerPath)) File.Delete(HangMarkerPath);
                if (File.Exists(HangMarkerPath + ".tmp")) File.Delete(HangMarkerPath + ".tmp");
            }
            catch (Exception ex)
            {
                Logger.Info("Üretim kurtarma kaydı silinemedi: " + ex.Message);
            }
        }


        public static void WriteHangMarker(ProductionRecoveryState state, DateTime detectedUtc)
        {
            if (state == null) return;
            try
            {
                string dir = Path.GetDirectoryName(HangMarkerPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                string temp = HangMarkerPath + ".tmp";
                File.WriteAllLines(temp, new[]
                {
                    detectedUtc.ToUniversalTime().ToString("o"),
                    state.LastProgressUtc.ToUniversalTime().ToString("o"),
                    state.Stage ?? "",
                    state.PartCode ?? "",
                    state.Index.ToString(),
                    state.Total.ToString(),
                    Math.Max(30, state.HangThresholdSeconds).ToString()
                });
                if (File.Exists(HangMarkerPath)) File.Delete(HangMarkerPath);
                File.Move(temp, HangMarkerPath);
            }
            catch (Exception ex)
            {
                Logger.Info("Watchdog hang marker yazılamadı: " + ex.Message);
            }
        }

        public static void ClearHangMarker()
        {
            try
            {
                if (File.Exists(HangMarkerPath)) File.Delete(HangMarkerPath);
                if (File.Exists(HangMarkerPath + ".tmp")) File.Delete(HangMarkerPath + ".tmp");
            }
            catch { }
        }

        private static void ApplyHangMarker(ProductionRecoveryState state)
        {
            if (state == null || !File.Exists(HangMarkerPath)) return;
            try
            {
                string[] lines = File.ReadAllLines(HangMarkerPath);
                DateTime detected;
                if (lines.Length > 0 && DateTime.TryParse(lines[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out detected))
                    state.HangDetectedUtc = detected.ToUniversalTime();
                else
                    state.HangDetectedUtc = DateTime.UtcNow;
                state.HangDetected = true;
            }
            catch
            {
                state.HangDetected = true;
                state.HangDetectedUtc = DateTime.UtcNow;
            }
        }

        public static ProductionRecoveryState Clone(ProductionRecoveryState state)
        {
            if (state == null) return null;
            return new ProductionRecoveryState
            {
                Version = state.Version,
                RunId = state.RunId,
                HostProcessId = state.HostProcessId,
                OperationName = state.OperationName,
                ProjectName = state.ProjectName,
                FileName = state.FileName,
                FileCode = state.FileCode,
                OutputFolder = state.OutputFolder,
                TemplatePath = state.TemplatePath,
                StartedUtc = state.StartedUtc,
                LastProgressUtc = state.LastProgressUtc,
                Stage = state.Stage,
                PartCode = state.PartCode,
                Index = state.Index,
                Total = state.Total,
                OverallIndex = state.OverallIndex,
                OverallTotal = state.OverallTotal,
                HangDetected = state.HangDetected,
                HangDetectedUtc = state.HangDetectedUtc,
                HangThresholdSeconds = state.HangThresholdSeconds,
                ExportDxf = state.ExportDxf,
                ExportPdf = state.ExportPdf,
                ExportExcel = state.ExportExcel,
                IncludeBendLines = state.IncludeBendLines,
                ExportCombinedDxf = state.ExportCombinedDxf,
                ExportCombinedDwg = state.ExportCombinedDwg,
                SelectedPartCodes = state.SelectedPartCodes == null ? new List<string>() : new List<string>(state.SelectedPartCodes),
                RecoverySkippedPartCodes = state.RecoverySkippedPartCodes == null ? new List<string>() : new List<string>(state.RecoverySkippedPartCodes),
                RecoverySkippedPartKeys = state.RecoverySkippedPartKeys == null ? new List<string>() : new List<string>(state.RecoverySkippedPartKeys),
                CurrentPartRecoveryKey = state.CurrentPartRecoveryKey,
                SelectedParts = state.SelectedParts == null ? new List<ProductionRecoveryPartState>() :
                    state.SelectedParts.Select(x => x == null ? null : new ProductionRecoveryPartState
                    {
                        PartCode = x.PartCode,
                        SourcePath = x.SourcePath,
                        Configuration = x.Configuration,
                        SourceSha256 = x.SourceSha256,
                        RecoveryKey = x.RecoveryKey
                    }).Where(x => x != null).ToList(),
                CompletedDxfPartCodes = state.CompletedDxfPartCodes == null ? new List<string>() : new List<string>(state.CompletedDxfPartCodes),
                CompletedPdfPartCodes = state.CompletedPdfPartCodes == null ? new List<string>() : new List<string>(state.CompletedPdfPartCodes),
                CompletedCombinedDxf = state.CompletedCombinedDxf,
                CompletedCombinedDwg = state.CompletedCombinedDwg,
                CompletedExcel = state.CompletedExcel
            };
        }
    }
}
