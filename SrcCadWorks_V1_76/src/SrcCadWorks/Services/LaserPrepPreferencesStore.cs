using System;
using System.IO;
using System.Xml;

namespace SrcCadWorks.Services
{
    internal sealed class LaserPrepPreferences
    {
        public bool ExportDxf { get; set; } = true;
        public bool BendLines { get; set; } = true;
        public bool ExportPdf { get; set; } = true;
        public bool ExportExcel { get; set; } = true;
        public bool ExportCombinedDxf { get; set; } = true;
        public bool ExportCombinedDwg { get; set; } = false;
        public bool OnlyLaserTagged { get; set; } = true;
        public bool OnlyWeldTagged { get; set; } = true;
        public bool IncludeHidden { get; set; } = true;
        public bool IncludePassive { get; set; } = false;
        public bool HideProcessed { get; set; } = false;
        public bool KeepOnTop { get; set; } = true;
        public string GridColumnOrder { get; set; } = "";
        public string GridColumnWidths { get; set; } = "";
        public int GridLayoutVersion { get; set; } = 0;
        public string ProcessingSpeed { get; set; } = "Normal";
    }

    internal static class LaserPrepPreferencesStore
    {
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "laser_prep_preferences.xml");

        public static LaserPrepPreferences Load()
        {
            var p = new LaserPrepPreferences();
            try
            {
                if (!File.Exists(SettingsPath)) return p;
                var doc = new XmlDocument();
                doc.Load(SettingsPath);
                var root = doc.DocumentElement;
                if (root == null) return p;

                p.ExportDxf = ReadBool(root, "ExportDxf", p.ExportDxf);
                p.BendLines = ReadBool(root, "BendLines", p.BendLines);
                p.ExportPdf = ReadBool(root, "ExportPdf", p.ExportPdf);
                p.ExportExcel = ReadBool(root, "ExportExcel", p.ExportExcel);
                p.ExportCombinedDxf = ReadBool(root, "ExportCombinedDxf", p.ExportCombinedDxf);
                p.ExportCombinedDwg = ReadBool(root, "ExportCombinedDwg", p.ExportCombinedDwg);
                p.OnlyLaserTagged = ReadBool(root, "OnlyLaserTagged", p.OnlyLaserTagged);
                p.OnlyWeldTagged = ReadBool(root, "OnlyWeldTagged", p.OnlyWeldTagged);
                p.IncludeHidden = ReadBool(root, "IncludeHidden", p.IncludeHidden);
                p.IncludePassive = ReadBool(root, "IncludePassive", p.IncludePassive);
                p.HideProcessed = ReadBool(root, "HideProcessed", p.HideProcessed);
                p.KeepOnTop = ReadBool(root, "KeepOnTop", p.KeepOnTop);
                p.GridColumnOrder = ReadString(root, "GridColumnOrder", p.GridColumnOrder);
                p.GridColumnWidths = ReadString(root, "GridColumnWidths", p.GridColumnWidths);
                p.GridLayoutVersion = ReadInt(root, "GridLayoutVersion", p.GridLayoutVersion);
                p.ProcessingSpeed = ReadString(root, "ProcessingSpeed", p.ProcessingSpeed);
            }
            catch (Exception ex)
            {
                Logger.Info("Lazer hazırlama tercihleri okunamadı; varsayılanlar kullanılacak: " + ex.Message);
            }
            return p;
        }

        public static void Save(LaserPrepPreferences p)
        {
            if (p == null) return;
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                var doc = new XmlDocument();
                var root = doc.CreateElement("LaserPrepPreferences");
                doc.AppendChild(root);
                WriteBool(doc, root, "ExportDxf", p.ExportDxf);
                WriteBool(doc, root, "BendLines", p.BendLines);
                WriteBool(doc, root, "ExportPdf", p.ExportPdf);
                WriteBool(doc, root, "ExportExcel", p.ExportExcel);
                WriteBool(doc, root, "ExportCombinedDxf", p.ExportCombinedDxf);
                WriteBool(doc, root, "ExportCombinedDwg", p.ExportCombinedDwg);
                WriteBool(doc, root, "OnlyLaserTagged", p.OnlyLaserTagged);
                WriteBool(doc, root, "OnlyWeldTagged", p.OnlyWeldTagged);
                WriteBool(doc, root, "IncludeHidden", p.IncludeHidden);
                WriteBool(doc, root, "IncludePassive", p.IncludePassive);
                WriteBool(doc, root, "HideProcessed", p.HideProcessed);
                WriteBool(doc, root, "KeepOnTop", p.KeepOnTop);
                WriteString(doc, root, "GridColumnOrder", p.GridColumnOrder);
                WriteString(doc, root, "GridColumnWidths", p.GridColumnWidths);
                WriteInt(doc, root, "GridLayoutVersion", p.GridLayoutVersion);
                WriteString(doc, root, "ProcessingSpeed", p.ProcessingSpeed);

                string temp = SettingsPath + ".tmp";
                doc.Save(temp);
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(temp, SettingsPath);
            }
            catch (Exception ex)
            {
                Logger.Info("Lazer hazırlama tercihleri kaydedilemedi: " + ex.Message);
            }
        }


        private static int ReadInt(XmlElement root, string name, int fallback)
        {
            var node = root.SelectSingleNode(name);
            int value;
            return node != null && int.TryParse(node.InnerText, out value) ? value : fallback;
        }

        private static void WriteInt(XmlDocument doc, XmlElement root, string name, int value)
        {
            var node = doc.CreateElement(name);
            node.InnerText = value.ToString();
            root.AppendChild(node);
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
