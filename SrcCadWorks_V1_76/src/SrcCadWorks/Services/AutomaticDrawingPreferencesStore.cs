using System;
using System.IO;
using System.Xml;

namespace SrcCadWorks.Services
{
    internal sealed class AutomaticDrawingPreferences
    {
        public string DrawingTemplatePath { get; set; } = "";
        public bool ImportModelAnnotations { get; set; } = true;
        public bool AutoDimensionFallback { get; set; } = true;
        public bool KeepAboveSolidWorks { get; set; } = true;
    }

    internal static class AutomaticDrawingPreferencesStore
    {
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "automatic_drawing_preferences.xml");

        public static AutomaticDrawingPreferences Load()
        {
            var p = new AutomaticDrawingPreferences();
            try
            {
                if (!File.Exists(SettingsPath)) return p;
                var doc = new XmlDocument();
                doc.Load(SettingsPath);
                var root = doc.DocumentElement;
                if (root == null) return p;

                p.DrawingTemplatePath = ReadString(root, "DrawingTemplatePath", p.DrawingTemplatePath);
                p.ImportModelAnnotations = ReadBool(root, "ImportModelAnnotations", p.ImportModelAnnotations);
                p.AutoDimensionFallback = ReadBool(root, "AutoDimensionFallback", p.AutoDimensionFallback);
                p.KeepAboveSolidWorks = ReadBool(root, "KeepAboveSolidWorks", p.KeepAboveSolidWorks);
            }
            catch (Exception ex)
            {
                Logger.Info("Otomatik teknik resim tercihleri okunamadı; varsayılanlar kullanılacak: " + ex.Message);
            }
            return p;
        }

        public static void Save(AutomaticDrawingPreferences p)
        {
            if (p == null) return;
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                var doc = new XmlDocument();
                var root = doc.CreateElement("AutomaticDrawingPreferences");
                doc.AppendChild(root);
                WriteString(doc, root, "DrawingTemplatePath", p.DrawingTemplatePath);
                WriteBool(doc, root, "ImportModelAnnotations", p.ImportModelAnnotations);
                WriteBool(doc, root, "AutoDimensionFallback", p.AutoDimensionFallback);
                WriteBool(doc, root, "KeepAboveSolidWorks", p.KeepAboveSolidWorks);

                string temp = SettingsPath + ".tmp";
                doc.Save(temp);
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(temp, SettingsPath);
            }
            catch (Exception ex)
            {
                Logger.Info("Otomatik teknik resim tercihleri kaydedilemedi: " + ex.Message);
            }
        }

        private static bool ReadBool(XmlElement root, string name, bool fallback)
        {
            var node = root.SelectSingleNode(name);
            bool value;
            return node != null && bool.TryParse(node.InnerText, out value) ? value : fallback;
        }

        private static string ReadString(XmlElement root, string name, string fallback)
        {
            var node = root.SelectSingleNode(name);
            return node != null ? node.InnerText ?? fallback : fallback;
        }

        private static void WriteBool(XmlDocument doc, XmlElement root, string name, bool value)
        {
            var node = doc.CreateElement(name);
            node.InnerText = value.ToString();
            root.AppendChild(node);
        }

        private static void WriteString(XmlDocument doc, XmlElement root, string name, string value)
        {
            var node = doc.CreateElement(name);
            node.InnerText = value ?? string.Empty;
            root.AppendChild(node);
        }
    }
}
