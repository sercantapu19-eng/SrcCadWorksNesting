using System.Collections.Generic;

namespace SrcCadWorks.Models
{
    public sealed class ProcessProjectInfo
    {
        public string ProjectName { get; set; }
        public string FileName { get; set; }
        public string FileCode { get; set; }
        public string Designer { get; set; }
        public string Preparer { get; set; }
        public string Approver { get; set; }
        public string RequestReason { get; set; }
        public string Note { get; set; }
    }

    public sealed class ManufacturingRow
    {
        public List<string> OccurrenceSelectIds { get; } = new List<string>();
        public string Sequence { get; set; }
        public string PartCode { get; set; }
        public int Quantity { get; set; }
        public string ProfileType { get; set; }
        public string ProductDescription { get; set; }
        public string ColorCode { get; set; }
        public string Route { get; set; }
        public string SourcePath { get; set; }
        public string Configuration { get; set; }
        public string DrawingPath { get; set; }
    }

    public sealed class ManufacturingScanResult
    {
        public List<ManufacturingRow> Rows { get; } = new List<ManufacturingRow>();
        public List<string> Warnings { get; } = new List<string>();
        public int OccurrenceCount { get; set; }
        public int SawOccurrenceCount { get; set; }
    }

    public sealed class MachiningRow
    {
        public string Sequence { get; set; }
        public bool IsRoot { get; set; }
        public int Depth { get; set; }
        public bool StepSelected { get; set; }
        public string RootPartCode { get; set; }
        public string FolderName { get; set; }
        public string PartCode { get; set; }
        public int Quantity { get; set; }
        public string ProductDescription { get; set; }
        public string ColorCode { get; set; }
        public string SourcePath { get; set; }
        public string Configuration { get; set; }
        public string DrawingPath { get; set; }
    }

    public sealed class MachiningScanResult
    {
        public List<MachiningRow> Rows { get; } = new List<MachiningRow>();
        public List<MachiningRow> RootRows { get; } = new List<MachiningRow>();
        public List<string> Warnings { get; } = new List<string>();
        public int OccurrenceCount { get; set; }
        public int RootCount { get; set; }
        public int PdfItemCount { get; set; }
    }
}
