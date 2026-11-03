using System;
using System.Collections.Generic;

namespace SrcCadWorks.Models
{
    public sealed class ProductionPackageResult
    {
        public int DxfCreated { get; set; }
        public int DxfSkipped { get; set; }
        public int PdfCreated { get; set; }
        public int PdfSkipped { get; set; }
        public int PdfMissingDrawing { get; set; }
        public List<string> DxfSuccessfulPartCodes { get; } = new List<string>();
        public List<string> DxfFailedPartCodes { get; } = new List<string>();
        public List<string> PdfSuccessfulPartCodes { get; } = new List<string>();
        public List<string> PdfFailedPartCodes { get; } = new List<string>();
        public List<string> PdfMissingDrawingPartCodes { get; } = new List<string>();
        public List<string> RecoverySkippedPartCodes { get; } = new List<string>();
        public string CombinedDxfPath { get; set; }
        public string CombinedDwgPath { get; set; }
        public bool CombinedDxfCreated { get; set; }
        public bool CombinedDwgCreated { get; set; }
        public string ExcelPath { get; set; }
        public bool ExcelCreated { get; set; }
        public string ErrorReportPath { get; set; }
        public int SelectedPartCount { get; set; }
        public TimeSpan ProductionDuration { get; set; }
        public TimeSpan DxfDuration { get; set; }
        public TimeSpan PdfDuration { get; set; }
        public TimeSpan ExcelDuration { get; set; }
        public TimeSpan CombinedDxfDuration { get; set; }
        public TimeSpan CombinedDwgDuration { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    public sealed class PackageProgress
    {
        public string Stage { get; set; }
        public int Index { get; set; }
        public int Total { get; set; }
        public string PartCode { get; set; }
        public string Message { get; set; }
        public int OverallIndex { get; set; }
        public int OverallTotal { get; set; }
        public string ResourceText { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? EstimatedRemaining { get; set; }
        public bool ReusedOutput { get; set; }
    }

    public enum ProcessingSpeedMode
    {
        Normal = 0,
        Safe = 1,
        Fast = 2
    }

    public sealed class ProductionPackageOptions
    {
        public bool ExportDxf { get; set; } = true;
        public bool ExportPdf { get; set; } = true;
        public bool ExportExcel { get; set; } = true;
        public bool IncludeBendLines { get; set; } = true;
        public bool ExportCombinedDxf { get; set; } = true;
        public bool ExportCombinedDwg { get; set; } = false;
        public ProcessingSpeedMode ProcessingSpeed { get; set; } = ProcessingSpeedMode.Normal;
    }
}
