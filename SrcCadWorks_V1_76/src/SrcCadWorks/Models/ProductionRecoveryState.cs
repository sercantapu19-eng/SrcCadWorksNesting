using System;
using System.Collections.Generic;

namespace SrcCadWorks.Models
{
    [Serializable]
    public sealed class ProductionRecoveryState
    {
        public string Version { get; set; } = "1.12.70";
        public string RunId { get; set; }
        public int HostProcessId { get; set; }
        public string OperationName { get; set; }
        public string ProjectName { get; set; }
        public string FileName { get; set; }
        public string FileCode { get; set; }
        public string OutputFolder { get; set; }
        public string TemplatePath { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime LastProgressUtc { get; set; }
        public string Stage { get; set; }
        public string PartCode { get; set; }
        public int Index { get; set; }
        public int Total { get; set; }
        public int OverallIndex { get; set; }
        public int OverallTotal { get; set; }
        public bool HangDetected { get; set; }
        public DateTime HangDetectedUtc { get; set; }
        public int HangThresholdSeconds { get; set; } = 120;

        public bool ExportDxf { get; set; }
        public bool ExportPdf { get; set; }
        public bool ExportExcel { get; set; }
        public bool IncludeBendLines { get; set; }
        public bool ExportCombinedDxf { get; set; }
        public bool ExportCombinedDwg { get; set; }
        public ProcessingSpeedMode ProcessingSpeed { get; set; } = ProcessingSpeedMode.Normal;

        public List<string> SelectedPartCodes { get; set; } = new List<string>();
        public List<string> RecoverySkippedPartCodes { get; set; } = new List<string>();
        // V1.12.70+: part code alone is unsafe. Keys contain code+path+configuration+SHA256.
        public List<string> RecoverySkippedPartKeys { get; set; } = new List<string>();
        public string CurrentPartRecoveryKey { get; set; }
        public List<ProductionRecoveryPartState> SelectedParts { get; set; } = new List<ProductionRecoveryPartState>();
        public List<string> CompletedDxfPartCodes { get; set; } = new List<string>();
        public List<string> CompletedPdfPartCodes { get; set; } = new List<string>();
        public bool CompletedCombinedDxf { get; set; }
        public bool CompletedCombinedDwg { get; set; }
        public bool CompletedExcel { get; set; }

        public ProductionPackageOptions ToOptions()
        {
            return new ProductionPackageOptions
            {
                ExportDxf = ExportDxf,
                ExportPdf = ExportPdf,
                ExportExcel = ExportExcel,
                IncludeBendLines = IncludeBendLines,
                ExportCombinedDxf = ExportCombinedDxf,
                ExportCombinedDwg = ExportCombinedDwg,
                ProcessingSpeed = ProcessingSpeed
            };
        }
    }

    [Serializable]
    public sealed class ProductionRecoveryPartState
    {
        public string PartCode { get; set; }
        public string SourcePath { get; set; }
        public string Configuration { get; set; }
        public string SourceSha256 { get; set; }
        public string RecoveryKey { get; set; }
    }
}
