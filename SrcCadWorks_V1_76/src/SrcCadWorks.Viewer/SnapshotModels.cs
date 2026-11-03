using System.Collections.Generic;

namespace SrcCadWorks.Viewer
{
    internal sealed class AssemblyViewerSnapshot
    {
        public string AssemblyPath { get; set; }
        public string AssemblyConfiguration { get; set; }
        public string AssemblyTitle { get; set; }
        public bool IsLoading { get; set; }
        public int ProcessedComponents { get; set; }
        public int TotalComponents { get; set; }
        public int ProgressPercent { get; set; }
        public string StatusText { get; set; }
        public List<AssemblyViewerItem> Items { get; set; } = new List<AssemblyViewerItem>();
        public List<AssemblyViewerItem> TreeItems { get; set; } = new List<AssemblyViewerItem>();
    }

    internal sealed class AssemblyViewerItem
    {
        public string FileName { get; set; }
        public string PartCode { get; set; }
        public int Quantity { get; set; }
        public int TopLevelQuantity { get; set; }
        public string DocumentType { get; set; }
        public string OperationType { get; set; }
        public string ProductDescription { get; set; }
        public string Material { get; set; }
        public string Thickness { get; set; }
        public string ColorCode { get; set; }
        public string WeldProcess { get; set; }
        public string WeldStatus { get; set; }
        public string MassText { get; set; }
        public string Configuration { get; set; }
        public string SourcePath { get; set; }
        public string DrawingPath { get; set; }
        public string SelectId { get; set; }
        public List<string> OccurrenceSelectIds { get; set; } = new List<string>();
        public List<string> TopLevelOccurrenceSelectIds { get; set; } = new List<string>();
        public string NodeId { get; set; }
        public string ParentNodeId { get; set; }
        public int Depth { get; set; }
        public bool HasChildren { get; set; }
        public int HierarchyOrder { get; set; }
    }

    internal sealed class ViewerPreferences
    {
        public List<string> VisibleOptionalColumns { get; set; } = new List<string>();
        public Dictionary<string, int> Widths { get; set; } = new Dictionary<string, int>();
        public int SplitterDistance { get; set; } = 760;
    }
}
