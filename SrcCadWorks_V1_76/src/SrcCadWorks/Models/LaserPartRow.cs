using System;
using System.Linq;
using System.ComponentModel;
using System.Collections.Generic;

namespace SrcCadWorks.Models
{
    public sealed class LaserPartRow : INotifyPropertyChanged
    {
        private bool _selected = true;
        private int _rowNumber;
        private string _material;
        private string _operation;
        private string _colorCode;
        private string _weldProcess;
        private string _description;
        private int _quantity;

        public int RowNumber { get => _rowNumber; set { _rowNumber = value; OnChanged(nameof(RowNumber)); } }
        public bool Selected { get => _selected; set { _selected = value; OnChanged(nameof(Selected)); } }
        public string PartCode { get; set; }
        public int BaseQuantity { get; set; }
        public int Quantity { get => _quantity; set { _quantity = value; OnChanged(nameof(Quantity)); } }
        public double? ThicknessMm { get; set; }
        public string Material { get => _material; set { _material = value; OnChanged(nameof(Material)); } }
        public string Operation { get => _operation; set { _operation = value; OnChanged(nameof(Operation)); } }
        public string ColorCode { get => _colorCode; set { _colorCode = value; OnChanged(nameof(ColorCode)); } }
        public string WeldProcess { get => _weldProcess; set { _weldProcess = value; OnChanged(nameof(WeldProcess)); } }
        public string Description { get => _description; set { _description = value; OnChanged(nameof(Description)); } }
        public string SourcePath { get; set; }
        public string Configuration { get; set; }
        public bool HasBends { get; set; }
        public bool HasTappedHoles { get; set; }
        public bool HasCountersink { get; set; }
        public int TappedHoleWizardCount { get; set; }
        public string TappedHoleTypes { get; set; }
        public int FlatPatternCount { get; set; }
        public string Error { get; set; }
        public bool IsLaserTagged { get; set; }
        public bool IsWeldTagged { get; set; }
        public string LaserTagProperty { get; set; }
        public string DrawingPath { get; set; }
        public string ProductionDefinition { get; set; }
        // V1.11.21: Tarama sırasında alınan SOLIDWORKS component seçim kimlikleri.
        // Excel-only "İşlenenleri gizle" için dev montajı yeniden GetComponents(false) ile taramadan
        // sadece ilgili occurrence'ları doğrudan seçip toplu gizlemek amacıyla kullanılır.
        public List<string> OccurrenceSelectIds { get; set; } = new List<string>();

        // Original identities remain available for hiding every configuration occurrence.
        public List<LaserPartRow> SourceOccurrences { get; set; } = new List<LaserPartRow>();

        public static List<LaserPartRow> GroupByPartCode(IEnumerable<LaserPartRow> rows)
        {
            var result = new List<LaserPartRow>();
            foreach (var row in rows) AddByPartCode(result, row);
            return result;
        }

        public static LaserPartRow AddByPartCode(List<LaserPartRow> rows, LaserPartRow source)
        {
            var current = string.IsNullOrWhiteSpace(source.PartCode) ? null : rows.FirstOrDefault(r =>
                string.Equals(r.PartCode?.Trim(), source.PartCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (current == null)
            {
                current = (LaserPartRow)source.MemberwiseClone();
                current.PropertyChanged = null;
                current.PartCode = source.PartCode?.Trim();
                current.OccurrenceSelectIds = new List<string>(source.OccurrenceSelectIds ?? new List<string>());
                current.SourceOccurrences = new List<LaserPartRow>();
                rows.Add(current);
            }
            else
            {
                // The same part code may first be encountered in an untagged configuration.
                if (source.IsLaserTagged && !current.IsLaserTagged)
                {
                    current.IsLaserTagged = true;
                    current.LaserTagProperty = source.LaserTagProperty;
                    current.OnChanged(nameof(ControlText));
                }
                if (source.IsWeldTagged && !current.IsWeldTagged)
                {
                    current.IsWeldTagged = true;
                    current.OnChanged(nameof(ControlText));
                }
                // One code remains one row even when HAVSA is found in a later occurrence.
                if (source.HasCountersink && !current.HasCountersink)
                {
                    current.HasCountersink = true;
                    current.Operation = LaserOperation.WithCountersink(current.Operation);
                    current.OnChanged(nameof(CountersinkText));
                    current.OnChanged(nameof(ControlText));
                }
                current.BaseQuantity = checked(current.BaseQuantity + source.BaseQuantity);
                current.Quantity = checked(current.Quantity + source.Quantity);
                current.OccurrenceSelectIds = current.OccurrenceSelectIds.Concat(source.OccurrenceSelectIds ?? new List<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (source.HasBlockingError && !string.Equals(current.Error, source.Error, StringComparison.Ordinal))
                    current.Error = string.IsNullOrWhiteSpace(current.Error) ? source.Error : current.Error + " | " + source.Error;
                if (!current.HasDrawing && source.HasDrawing) current.DrawingPath = source.DrawingPath;
            }
            if (source.SourceOccurrences != null && source.SourceOccurrences.Count > 0)
                current.SourceOccurrences.AddRange(source.SourceOccurrences);
            else
                current.SourceOccurrences.Add(new LaserPartRow {
                    PartCode = source.PartCode,
                    BaseQuantity = source.BaseQuantity,
                    Quantity = source.Quantity,
                    ThicknessMm = source.ThicknessMm,
                    Material = source.Material,
                    Operation = source.Operation,
                    SourcePath = source.SourcePath,
                    Configuration = source.Configuration,
                    HasBends = source.HasBends,
                    HasTappedHoles = source.HasTappedHoles,
                    HasCountersink = source.HasCountersink,
                    FlatPatternCount = source.FlatPatternCount,
                    Error = source.Error,
                    IsLaserTagged = source.IsLaserTagged,
                    IsWeldTagged = source.IsWeldTagged,
                    LaserTagProperty = source.LaserTagProperty,
                    OccurrenceSelectIds = new List<string>(source.OccurrenceSelectIds ?? new List<string>())
                });
            return current;
        }

        public string ThicknessText => ThicknessMm.HasValue ? ThicknessMm.Value.ToString("0.###") + " MM" : "";
        public bool HasBlockingError => !string.IsNullOrWhiteSpace(Error);
        public bool HasDrawing => !string.IsNullOrWhiteSpace(DrawingPath);
        public string CountersinkText => HasCountersink ? "HAVŞA ✓" : "";
        public string TappedHoleText => HasTappedHoles ? "KILAVUZ ✓ (" + TappedHoleWizardCount + ")" : "";

        public string ControlText
        {
            get
            {
                if (HasBlockingError) return Error;
                string process = IsLaserTagged
                    ? (IsWeldTagged ? "LAZER ✓ / KAYNAK ✓" : "LAZER ✓")
                    : (IsWeldTagged ? "KAYNAK ✓" : "LAZER / KAYNAK etiketi yok");
                string drawing = HasDrawing ? "Teknik resim ✓" : "Teknik resim yok";
                string tap = HasTappedHoles ? "Kılavuz ✓" : "Kılavuz yok";
                // Ürün tanımı ayrı sütunda gösterilir; Kontrol sütununda tekrar edilmez.
                return process + "  •  " + drawing + "  •  " + tap + (HasCountersink ? "  •  Havşa ✓" : "");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
