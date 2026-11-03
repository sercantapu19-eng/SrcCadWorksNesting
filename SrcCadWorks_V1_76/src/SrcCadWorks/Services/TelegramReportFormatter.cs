using System;
using System.Linq;
using System.Text;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal static class TelegramReportFormatter
    {
        public static string FormatStarted(OperationProgressSnapshot snapshot, bool statusEnabled)
        {
            if (snapshot == null) return "🔧 SrcCadWorks – İşlem başladı";
            var sb = new StringBuilder();
            sb.Append("🔧 ").Append(snapshot.OperationName).AppendLine(" – İşlem Başladı");
            sb.AppendLine();
            sb.Append("📦 Toplam parça: ").Append(Math.Max(0, snapshot.Total)).AppendLine();
            sb.Append("⚙️ İlk aşama: ").Append(FriendlyStage(snapshot.Stage)).AppendLine();
            if (statusEnabled)
            {
                sb.AppendLine();
                sb.Append("📲 Durum, Son Rapor, Duraklat ve Devam Et düğmelerini kullanabilirsiniz. /durum, /rapor, /duraklat ve /devam komutları da aktiftir.");
            }
            return sb.ToString();
        }

        public static string FormatStatus(OperationProgressSnapshot snapshot)
        {
            if (snapshot == null) return "📊 SrcCadWorks – Anlık durum bilgisi henüz hazır değil.";
            var sb = new StringBuilder();
            sb.Append("📊 ").Append(snapshot.OperationName).AppendLine(" – Anlık Durum");
            sb.AppendLine();
            if (snapshot.IsCompleted)
            {
                sb.Append("Durum: ").Append(string.IsNullOrWhiteSpace(snapshot.FinalStatus) ? "Tamamlandı" : snapshot.FinalStatus).AppendLine();
                sb.Append("⏱️ Toplam süre: ").Append(FormatDuration(snapshot.Elapsed));
                return sb.ToString();
            }

            sb.Append("⚙️ İşlem: ").Append(FriendlyStage(snapshot.Stage)).AppendLine();
            if (snapshot.Total > 0)
            {
                sb.Append("📍 Parça: ").Append(Math.Max(0, snapshot.Index)).Append('/').Append(snapshot.Total);
                if (!string.IsNullOrWhiteSpace(snapshot.PartCode)) sb.Append(" – ").Append(snapshot.PartCode);
                sb.AppendLine();
            }
            if (snapshot.OverallTotal > 0)
            {
                int percent = (int)Math.Round(Math.Max(0, snapshot.OverallIndex) * 100.0 / Math.Max(1, snapshot.OverallTotal));
                percent = Math.Max(0, Math.Min(100, percent));
                sb.Append("📦 Genel ilerleme: ").Append(Math.Max(0, snapshot.OverallIndex)).Append('/').Append(snapshot.OverallTotal)
                    .Append(" (%").Append(percent).Append(')').AppendLine();
            }
            sb.Append("⏱️ Geçen süre: ").Append(FormatDuration(snapshot.Elapsed));
            if (snapshot.EstimatedRemaining.HasValue && snapshot.EstimatedRemaining.Value.TotalSeconds > 0)
                sb.AppendLine().Append("⌛ Tahmini kalan: ").Append(FormatDuration(snapshot.EstimatedRemaining.Value));
            return sb.ToString();
        }

        public static string FormatCompleted(OperationReport report)
        {
            if (report == null) return "🔧 SrcCadWorks – İşlem tamamlandı.";
            var sb = new StringBuilder();
            sb.Append("🔧 ").Append(report.OperationName).AppendLine(" – İşlem Tamamlandı");
            sb.AppendLine();
            sb.Append("📦 Toplam parça: ").Append(report.TotalItems).AppendLine();

            if (report.Metrics.Count > 0)
            {
                sb.AppendLine();
                foreach (var metric in report.Metrics)
                    sb.Append(string.IsNullOrWhiteSpace(metric.Icon) ? "•" : metric.Icon).Append(' ')
                        .Append(metric.Label).Append(": ").Append(metric.Value).AppendLine();
            }

            foreach (var group in report.FailureGroups.Where(g => g != null && g.ItemCodes.Count > 0))
            {
                sb.AppendLine();
                sb.Append(group.Title).AppendLine();
                sb.AppendLine();
                foreach (string code in group.ItemCodes.Where(x => !string.IsNullOrWhiteSpace(x)))
                    sb.Append("- ").Append(code.Trim()).AppendLine();
            }

            if (report.Notes.Count > 0)
            {
                sb.AppendLine();
                foreach (string note in report.Notes.Where(x => !string.IsNullOrWhiteSpace(x)))
                    sb.Append("ℹ️ ").Append(note.Trim()).AppendLine();
            }

            sb.AppendLine();
            sb.Append("⏱️ İşlem süresi: ").Append(FormatDuration(report.Duration)).AppendLine();
            sb.AppendLine();
            sb.Append("Durum: ").Append(string.IsNullOrWhiteSpace(report.StatusIcon) ? "" : report.StatusIcon + " ")
                .Append(string.IsNullOrWhiteSpace(report.StatusText) ? "Tamamlandı" : report.StatusText);
            return sb.ToString().TrimEnd();
        }

        public static string FormatCancelled(OperationProgressSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.Append("⛔ ").Append(snapshot == null ? "SrcCadWorks" : snapshot.OperationName).AppendLine(" – İşlem İptal Edildi");
            if (snapshot != null)
            {
                sb.AppendLine();
                sb.Append("⚙️ Son işlem: ").Append(FriendlyStage(snapshot.Stage)).AppendLine();
                if (!string.IsNullOrWhiteSpace(snapshot.PartCode)) sb.Append("📍 Son parça: ").Append(snapshot.PartCode).AppendLine();
                sb.Append("⏱️ Geçen süre: ").Append(FormatDuration(snapshot.Elapsed));
            }
            return sb.ToString().TrimEnd();
        }

        public static string FormatFailed(OperationProgressSnapshot snapshot, string error)
        {
            var sb = new StringBuilder();
            sb.Append("❌ ").Append(snapshot == null ? "SrcCadWorks" : snapshot.OperationName).AppendLine(" – İşlem Hatası");
            if (snapshot != null)
            {
                sb.AppendLine();
                sb.Append("⚙️ Son işlem: ").Append(FriendlyStage(snapshot.Stage)).AppendLine();
                if (!string.IsNullOrWhiteSpace(snapshot.PartCode)) sb.Append("📍 Son parça: ").Append(snapshot.PartCode).AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(error)) sb.Append("⚠️ Hata: ").Append(error.Trim());
            return sb.ToString().TrimEnd();
        }

        private static string FriendlyStage(string stage)
        {
            string s = (stage ?? "").Trim().ToUpperInvariant();
            switch (s)
            {
                case "DXF": return "DXF oluşturma";
                case "PDF": return "Teknik resim PDF oluşturma";
                case "EXCEL": return "Lazer Talep Formu";
                case "TOPLU_DXF": return "Toplu DXF oluşturma";
                case "TOPLU_DXF_KAYNAK": return "Toplu DXF/DWG parça hazırlığı";
                case "TOPLU_DWG": return "Toplu DWG oluşturma";
                case "TARAMA": return "Montaj tarama";
                case "TAMAM": return "Tamamlandı";
                case "HAZIRLIK": return "Hazırlık";
                default: return string.IsNullOrWhiteSpace(stage) ? "Hazırlık" : stage.Replace('_', ' ');
            }
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return string.Format("{0:00}:{1:00}:{2:00}", (int)value.TotalHours, value.Minutes, value.Seconds);
            return string.Format("{0:00}:{1:00}", (int)value.TotalMinutes, value.Seconds);
        }
    }
}
