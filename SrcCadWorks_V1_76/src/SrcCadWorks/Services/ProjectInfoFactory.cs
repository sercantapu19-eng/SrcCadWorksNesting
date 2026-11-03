using System.IO;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal static class ProjectInfoFactory
    {
        public static LaserProjectInfo FromActiveAssembly(SldWorks swApp)
        {
            var model = swApp.ActiveDoc as ModelDoc2;
            if (model == null) return new LaserProjectInfo();
            string cfg = "";
            try { cfg = model.ConfigurationManager.ActiveConfiguration.Name; } catch { }
            string baseName = Path.GetFileNameWithoutExtension(model.GetPathName());
            if (string.IsNullOrWhiteSpace(baseName)) baseName = Path.GetFileNameWithoutExtension(model.GetTitle());

            return new LaserProjectInfo
            {
                ProjectName = CustomPropertyReader.First(model, cfg, "Proje Adı", "Proje Adi", "Project", "Project Name"),
                FileName = FirstOrFallback(CustomPropertyReader.First(model, cfg, "Dosya Adı", "Dosya Adi", "Description", "Tanım", "Tanim"), baseName),
                FileCode = FirstOrFallback(CustomPropertyReader.First(model, cfg, "Dosya Kodu", "Doküman No", "Dokuman No", "Document Number", "Number"), baseName),
                Designer = CustomPropertyReader.First(model, cfg, "Tasarlayan", "Designer", "DesignedBy", "Designed By"),
                Preparer = CustomPropertyReader.First(model, cfg, "Çizen", "ÇİZEN", "Cizen", "CIZEN", "DrawnBy", "Drawn By", "Hazırlayan", "Hazirlayan", "PreparedBy", "Prepared By"),
                RequestReason = "",
                Note = ""
            };
        }

        private static string FirstOrFallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? (fallback ?? "") : value;
    }
}
