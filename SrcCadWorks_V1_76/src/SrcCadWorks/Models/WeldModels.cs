using System.Collections.Generic;
using System.IO;

namespace SrcCadWorks.Models
{
    public sealed class WeldProjectInfo
    {
        public string ProjectName { get; set; }
        public string FileName { get; set; }
        public string FileCode { get; set; }
        public string Preparer { get; set; }
        public string Approver { get; set; }
        public string Note { get; set; }
    }

    public sealed class WeldFormRow
    {
        public string Sequence { get; set; }
        public bool IsWeldRoot { get; set; }
        // IsTopLevelWeld: montaj çarpanının uygulanacağı birinci seviye kaynak satırı.
        // İç içe kaynak satırları IsWeldRoot=true kalır (PDF üretimi için) ama çarpan almaz.
        public bool IsTopLevelWeld { get; set; }
        // Aktif ana montajdaki tüm occurrence'ların sayısı; reçete satırı adetinden ayrıdır.
        public int TotalQuantity { get; set; }
        public int Depth { get; set; }
        public string PartCode { get; set; }
        public string ProductDescription { get; set; }
        public int Quantity { get; set; }
        public string MassText { get; set; }
        public string ColorCode { get; set; }
        public string OperationType { get; set; }
        public string SourcePath { get; set; }
        public string Configuration { get; set; }
        private string _drawingPath;
        private string _drawingStatus = "YOK";
        public string DrawingPath
        {
            get { return _drawingPath; }
            set
            {
                _drawingPath = value ?? "";
                // Resolve once per scan row, rather than calling File.Exists on every repaint.
                _drawingStatus = !string.IsNullOrWhiteSpace(_drawingPath) && File.Exists(_drawingPath) ? "VAR" : "YOK";
            }
        }
        public string DrawingStatus { get { return _drawingStatus; } }
        public string SelectId { get; set; }
        public List<string> OccurrenceSelectIds { get; set; } = new List<string>();
        public string ParentPartCode { get; set; }
        public string FileCode { get; set; }
    }

    public sealed class WeldScanResult
    {
        public List<WeldFormRow> Rows { get; } = new List<WeldFormRow>();
        public List<WeldFormRow> WeldRoots { get; } = new List<WeldFormRow>();
        public List<string> Warnings { get; } = new List<string>();
        public int OccurrenceCount { get; set; }
        public int UniqueWeldCount { get; set; }
        public int MetadataCacheHits { get; set; }
        public int MetadataCacheMisses { get; set; }
        public int LaserCacheHits { get; set; }
    }
}
