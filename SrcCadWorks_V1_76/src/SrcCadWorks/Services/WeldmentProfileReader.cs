using System;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.Services
{
    internal sealed class SawPartDetails
    {
        public string ProfileType { get; set; }
        public bool HasHoleWizard { get; set; }
    }

    internal sealed class WeldmentProfileReader
    {
        private readonly SldWorks _swApp;
        public WeldmentProfileReader(SldWorks swApp) { _swApp = swApp; }

        public string Read(string path, string configuration, Func<bool> shouldCancel = null)
        {
            var details = ReadSawDetails(path, configuration, shouldCancel);
            return details == null ? "" : (details.ProfileType ?? "");
        }

        // TESTERE üretiminde profil ve matkap rotası aynı belge açılışında okunur.
        // Her Hole Wizard özelliği (vida boşluğu, havşa, V-dişli/tapped vb.) MATKAP işlemidir.
        public SawPartDetails ReadSawDetails(string path, string configuration, Func<bool> shouldCancel = null)
        {
            var result = new SawPartDetails { ProfileType = "", HasHoleWizard = false };
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;
            ModelDoc2 model = null;
            bool openedByUs = false;
            string originalConfiguration = "";
            bool switchedConfiguration = false;
            try
            {
                if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException();
                try { model = _swApp.GetOpenDocumentByName(path) as ModelDoc2; } catch { }
                if (model == null)
                {
                    int errors = 0, warnings = 0;
                    model = _swApp.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        configuration ?? "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = model != null;
                    if (openedByUs) { try { ((dynamic)model).Visible = false; } catch { } }
                }
                if (model == null) return result;

                try { originalConfiguration = model.ConfigurationManager.ActiveConfiguration.Name ?? ""; } catch { }
                if (!openedByUs && !string.IsNullOrWhiteSpace(configuration) &&
                    !string.Equals(originalConfiguration, configuration, StringComparison.OrdinalIgnoreCase))
                {
                    try { switchedConfiguration = model.ShowConfiguration2(configuration); } catch { }
                }

                return ReadSawDetailsFromModel(model);
            }
            finally
            {
                if (model != null && switchedConfiguration && !string.IsNullOrWhiteSpace(originalConfiguration))
                {
                    try { model.ShowConfiguration2(originalConfiguration); } catch { }
                }
                if (openedByUs && model != null)
                {
                    try { ComUtil.CloseOpenedBySrc(_swApp, model, path); } catch { }
                    ComUtil.Release(model);
                }
            }
        }

        internal SawPartDetails ReadSawDetailsFromModel(ModelDoc2 model)
        {
            string profile = ReadStructuralMember(model);
            if (string.IsNullOrWhiteSpace(profile)) profile = ReadCutListFallback(model);
            return new SawPartDetails { ProfileType = profile ?? "", HasHoleWizard = HasHoleWizardFeature(model) };
        }

        private static bool HasHoleWizardFeature(ModelDoc2 model)
        {
            if (model == null) return false;
            Feature feature = null;
            try { feature = model.FirstFeature() as Feature; } catch { }
            int safety = 0;
            while (feature != null && safety++ < 10000)
            {
                Feature next = null;
                try
                {
                    string type = feature.GetTypeName2() ?? "";
                    if (string.Equals(type, "HoleWzd", StringComparison.OrdinalIgnoreCase)) return true;
                    try { next = feature.GetNextFeature() as Feature; } catch { next = null; }
                }
                finally
                {
                    ComUtil.Release(feature);
                }
                feature = next;
            }
            ComUtil.Release(feature);
            return false;
        }

        private static string ReadStructuralMember(ModelDoc2 model)
        {
            Feature feature = null;
            try { feature = model.FirstFeature() as Feature; } catch { }
            int safety = 0;
            while (feature != null && safety++ < 5000)
            {
                object definition = null;
                try
                {
                    string type = "";
                    try { type = feature.GetTypeName2() ?? ""; } catch { }
                    string upper = type.ToUpperInvariant();
                    if (upper.Contains("WELDMEMBER") || upper.Contains("STRUCTURALMEMBER") || upper.Contains("STRUCTURAL MEMBER"))
                    {
                        definition = feature.GetDefinition();
                        if (definition != null)
                        {
                            dynamic data = definition;
                            string path = "";
                            string cfg = "";
                            try { path = Convert.ToString(data.WeldmentProfilePath) ?? ""; } catch { }
                            try { cfg = Convert.ToString(data.ConfigurationName) ?? ""; } catch { }
                            string value = BuildProfileName(path, cfg);
                            if (!string.IsNullOrWhiteSpace(value)) return value;
                        }
                    }
                }
                catch { }
                finally { ComUtil.Release(definition); }

                Feature next = null;
                try { next = feature.GetNextFeature() as Feature; } catch { }
                ComUtil.Release(feature);
                feature = next;
            }
            ComUtil.Release(feature);
            return "";
        }

        private static string BuildProfileName(string profilePath, string configurationName)
        {
            string baseName = "";
            try { baseName = Path.GetFileNameWithoutExtension(profilePath ?? "") ?? ""; } catch { }
            baseName = Cleanup(baseName);
            string cfg = Cleanup(configurationName);
            if (string.Equals(cfg, "DEFAULT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cfg, "VARSAYILAN", StringComparison.OrdinalIgnoreCase)) cfg = "";

            if (!string.IsNullOrWhiteSpace(cfg))
            {
                if (string.IsNullOrWhiteSpace(baseName)) return cfg;
                if (baseName.IndexOf(cfg, StringComparison.OrdinalIgnoreCase) >= 0) return baseName;
                return (cfg + " " + baseName).Trim();
            }
            return baseName;
        }

        private static string ReadCutListFallback(ModelDoc2 model)
        {
            Feature feature = null;
            try { feature = model.FirstFeature() as Feature; } catch { }
            int safety = 0;
            while (feature != null && safety++ < 5000)
            {
                object mgr = null;
                try
                {
                    string type = feature.GetTypeName2() ?? "";
                    if (string.Equals(type, "CutListFolder", StringComparison.OrdinalIgnoreCase))
                    {
                        try { mgr = ((dynamic)feature).CustomPropertyManager; } catch { mgr = null; }
                        if (mgr != null)
                        {
                            string[] names = { "Profil Tipi", "Profil", "Profile", "Description", "Açıklama", "Aciklama" };
                            foreach (string name in names)
                            {
                                try
                                {
                                    string raw = "", resolved = "";
                                    ((dynamic)mgr).Get2(name, out raw, out resolved);
                                    string v = !string.IsNullOrWhiteSpace(resolved) ? resolved : raw;
                                    if (!string.IsNullOrWhiteSpace(v)) return Cleanup(v);
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
                finally { ComUtil.Release(mgr); }

                Feature next = null;
                try { next = feature.GetNextFeature() as Feature; } catch { }
                ComUtil.Release(feature);
                feature = next;
            }
            ComUtil.Release(feature);
            return "";
        }

        private static string Cleanup(string value)
        {
            string v = (value ?? "").Trim();
            v = v.Replace("_", " ");
            while (v.Contains("  ")) v = v.Replace("  ", " ");
            return v;
        }
    }
}
