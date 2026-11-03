using System;
using System.IO;
using System.Xml;

namespace SrcCadWorks.Services
{
    internal sealed class AssemblyPrepPreferences
    {
        public string TemplatePath { get; set; } = "";
    }

    internal static class AssemblyPrepPreferencesStore
    {
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "assembly_prep_preferences.xml");

        public static AssemblyPrepPreferences Load()
        {
            var preferences = new AssemblyPrepPreferences();
            try
            {
                if (!File.Exists(SettingsPath)) return preferences;
                var doc = new XmlDocument();
                doc.Load(SettingsPath);
                var root = doc.DocumentElement;
                if (root == null) return preferences;
                var node = root.SelectSingleNode("TemplatePath");
                if (node != null) preferences.TemplatePath = node.InnerText ?? "";
            }
            catch (Exception ex)
            {
                Logger.Info("Montaj Hazırla tercihleri okunamadı; varsayılan şablon kullanılacak: " + ex.Message);
            }
            return preferences;
        }

        public static void Save(AssemblyPrepPreferences preferences)
        {
            if (preferences == null) return;
            string temp = null;
            try
            {
                string directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var doc = new XmlDocument();
                var root = doc.CreateElement("AssemblyPrepPreferences");
                doc.AppendChild(root);
                var template = doc.CreateElement("TemplatePath");
                template.InnerText = preferences.TemplatePath ?? "";
                root.AppendChild(template);

                temp = SettingsPath + ".tmp";
                doc.Save(temp);
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(temp, SettingsPath);
            }
            catch (Exception ex)
            {
                try { if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp)) File.Delete(temp); } catch { }
                Logger.Info("Montaj Hazırla tercihleri kaydedilemedi: " + ex.Message);
            }
        }
    }
}
