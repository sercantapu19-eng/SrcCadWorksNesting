using System;
using System.IO;

namespace SrcCadWorks.Services
{
    internal static class PreviewModePreference
    {
        private static bool _lastDrawing;
        private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "preview_mode.txt");

        public static bool PreferDrawing
        {
            get
            {
                try
                {
                    if (File.Exists(SettingsPath))
                        _lastDrawing = string.Equals(File.ReadAllText(SettingsPath).Trim(), "Drawing", StringComparison.Ordinal);
                }
                catch { }
                return _lastDrawing;
            }
        }

        public static void SelectDrawing(bool drawing)
        {
            _lastDrawing = drawing;
            string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(temp, drawing ? "Drawing" : "Model");
                if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, null);
                else File.Move(temp, SettingsPath);
            }
            catch { }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }
}
