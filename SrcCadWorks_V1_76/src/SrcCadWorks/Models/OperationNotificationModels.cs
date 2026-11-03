using System;
using System.Collections.Generic;

namespace SrcCadWorks.Models
{
    public sealed class OperationProgressSnapshot
    {
        public string OperationName { get; set; }
        public string Stage { get; set; }
        public int Index { get; set; }
        public int Total { get; set; }
        public string PartCode { get; set; }
        public int OverallIndex { get; set; }
        public int OverallTotal { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? EstimatedRemaining { get; set; }
        public bool IsCompleted { get; set; }
        public string FinalStatus { get; set; }
    }

    public sealed class OperationMetric
    {
        public string Icon { get; set; }
        public string Label { get; set; }
        public string Value { get; set; }
    }

    public sealed class OperationFailureGroup
    {
        public string Title { get; set; }
        public List<string> ItemCodes { get; } = new List<string>();
    }

    public sealed class OperationReport
    {
        public string OperationName { get; set; }
        public int TotalItems { get; set; }
        public TimeSpan Duration { get; set; }
        public string StatusText { get; set; }
        public string StatusIcon { get; set; }
        public List<OperationMetric> Metrics { get; } = new List<OperationMetric>();
        public List<OperationFailureGroup> FailureGroups { get; } = new List<OperationFailureGroup>();
        public List<string> Notes { get; } = new List<string>();
    }
}
