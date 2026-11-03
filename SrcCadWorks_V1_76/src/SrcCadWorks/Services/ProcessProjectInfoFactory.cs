using System.IO;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal static class ProcessProjectInfoFactory
    {
        public static ProcessProjectInfo FromActiveAssembly(SldWorks swApp)
        {
            var model = swApp == null ? null : swApp.ActiveDoc as ModelDoc2;
            if (model == null) return new ProcessProjectInfo();

            string cfg = "";
            try { cfg = model.ConfigurationManager.ActiveConfiguration.Name; } catch { }
            string baseName = "";
            try { baseName = Path.GetFileNameWithoutExtension(model.GetPathName()); } catch { }
            if (string.IsNullOrWhiteSpace(baseName))
            {
                try { baseName = Path.GetFileNameWithoutExtension(model.GetTitle()); } catch { }
            }

            return new ProcessProjectInfo
            {
                ProjectName = CustomPropertyReader.First(model, cfg, "Proje Adı", "Proje Adi", "Project", "Project Name"),
                FileName = FirstOrFallback(CustomPropertyReader.First(model, cfg, "Dosya Adı", "Dosya Adi", "Ürün Tanımı", "Urun Tanimi", "Description", "Tanım", "Tanim"), baseName),
                FileCode = FirstOrFallback(CustomPropertyReader.First(model, cfg, "Dosya Kodu", "Doküman No", "Dokuman No", "Document Number", "Number"), baseName),
                Designer = CustomPropertyReader.First(model, cfg, "Tasarlayan", "TASARLAYAN", "Designer", "Design By", "Çizen", "Cizen"),
                Preparer = CustomPropertyReader.First(model, cfg, "Hazırlayan", "Hazirlayan", "PreparedBy", "Prepared By", "Çizen", "Cizen"),
                Approver = CustomPropertyReader.First(model, cfg, "Onaylayan", "ApprovedBy", "Approved By", "Kontrol Eden", "Kontrol"),
                RequestReason = FirstOrFallback(CustomPropertyReader.First(model, cfg, "Talep Sebebi", "Talep Nedeni", "Request Reason"), "YENİ ÜRETİM"),
                Note = ""
            };
        }

        private static string FirstOrFallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? (fallback ?? "") : value.Trim();
        }
    }
}
