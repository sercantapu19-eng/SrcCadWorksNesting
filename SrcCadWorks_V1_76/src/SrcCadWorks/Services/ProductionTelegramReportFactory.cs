using System;
using System.Collections.Generic;
using System.Linq;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal static class ProductionTelegramReportFactory
    {
        public static OperationReport Create(string operationName, ProductionPackageResult result, ProductionPackageOptions options)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (options == null) options = new ProductionPackageOptions();

            var report = new OperationReport
            {
                OperationName = string.IsNullOrWhiteSpace(operationName) ? "Üretim Paketi" : operationName.Trim(),
                TotalItems = result.SelectedPartCount,
                Duration = result.ProductionDuration
            };

            if (options.ExportDxf)
            {
                report.Metrics.Add(new OperationMetric { Icon = "📐", Label = "DXF alınan", Value = result.DxfSuccessfulPartCodes.Count.ToString() });
                report.Metrics.Add(new OperationMetric { Icon = "⚠️", Label = "DXF alınamayan", Value = result.DxfFailedPartCodes.Count.ToString() });
                AddFailureGroup(report, "DXF alınamayan parçalar:", result.DxfFailedPartCodes);
            }

            if (options.ExportPdf)
            {
                report.Metrics.Add(new OperationMetric { Icon = "📄", Label = "PDF alınan", Value = result.PdfSuccessfulPartCodes.Count.ToString() });
                report.Metrics.Add(new OperationMetric { Icon = "⚠️", Label = "PDF alınamayan", Value = result.PdfFailedPartCodes.Count.ToString() });
                AddFailureGroup(report, "PDF alınamayan parçalar:", result.PdfFailedPartCodes);
            }

            if (options.ExportCombinedDxf)
                report.Metrics.Add(new OperationMetric { Icon = "🧩", Label = "Toplu DXF", Value = result.CombinedDxfCreated ? "oluşturuldu" : "oluşturulamadı" });
            if (options.ExportCombinedDwg)
                report.Metrics.Add(new OperationMetric { Icon = "📐", Label = "Toplu DWG", Value = result.CombinedDwgCreated ? "oluşturuldu" : "oluşturulamadı" });
            if (options.ExportExcel)
                report.Metrics.Add(new OperationMetric { Icon = "📊", Label = "Lazer Excel", Value = string.IsNullOrWhiteSpace(result.ExcelPath) ? "oluşturulamadı" : "oluşturuldu" });

            if (result.PdfMissingDrawingPartCodes.Count > 0)
                report.Notes.Add("Teknik resim dosyası bulunmayan parça: " + result.PdfMissingDrawingPartCodes.Count);

            if (result.RecoverySkippedPartCodes.Count > 0)
            {
                report.Metrics.Add(new OperationMetric { Icon = "🛟", Label = "Kurtarmada atlanan", Value = result.RecoverySkippedPartCodes.Count.ToString() });
                AddFailureGroup(report, "Kurtarmada atlanan parçalar:", result.RecoverySkippedPartCodes);
                report.Notes.Add("Bu parçalar önceki koşuda takılma şüphesi nedeniyle bu devam koşusunda atlandı.");
            }

            bool hasFailure = result.Errors.Count > 0 || result.DxfFailedPartCodes.Count > 0 || result.PdfFailedPartCodes.Count > 0 || result.RecoverySkippedPartCodes.Count > 0 ||
                (options.ExportCombinedDxf && !result.CombinedDxfCreated) || (options.ExportCombinedDwg && !result.CombinedDwgCreated) ||
                (options.ExportExcel && string.IsNullOrWhiteSpace(result.ExcelPath));
            bool hasSuccess = result.DxfSuccessfulPartCodes.Count > 0 || result.PdfSuccessfulPartCodes.Count > 0 ||
                result.CombinedDxfCreated || result.CombinedDwgCreated || !string.IsNullOrWhiteSpace(result.ExcelPath);

            if (!hasFailure)
            {
                report.StatusIcon = "✅";
                report.StatusText = "Tamamlandı";
            }
            else if (hasSuccess)
            {
                report.StatusIcon = "⚠️";
                report.StatusText = "Kısmen tamamlandı";
            }
            else
            {
                report.StatusIcon = "❌";
                report.StatusText = "Tamamlanamadı";
            }
            return report;
        }

        private static void AddFailureGroup(OperationReport report, string title, IEnumerable<string> codes)
        {
            var clean = (codes ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (clean.Count == 0) return;
            var group = new OperationFailureGroup { Title = title };
            group.ItemCodes.AddRange(clean);
            report.FailureGroups.Add(group);
        }
    }
}
