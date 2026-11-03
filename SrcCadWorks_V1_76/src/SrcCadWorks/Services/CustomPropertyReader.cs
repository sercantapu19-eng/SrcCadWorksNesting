using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal static class CustomPropertyReader
    {
        public static string First(ModelDoc2 model, string configuration, params string[] aliases)
        {
            if (model == null || aliases == null) return "";
            var value = ReadFrom(model, configuration, aliases);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            value = ReadFromNormalized(model, configuration, aliases);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            value = ReadFrom(model, "", aliases);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            value = ReadFromNormalized(model, "", aliases);
            return (value ?? "").Trim();
        }


        // Kaynak Hazırla / Montaj Görüntüleme için İşlem Tipi okuması.
        // Bazı eski dosyalarda konfigürasyon seviyesinde genel "İMALAT",
        // belge özel özelliklerinde ise daha özel "TALAŞLI İMALAT"/"LAZER"/"TESTERE"
        // bulunabiliyor. Genel İMALAT değerinin daha özel prosesi ezmesine izin verme.
        public static string OperationType(ModelDoc2 model, string configuration)
        {
            if (model == null) return "";
            string[] aliases = { "İşlem Tipi", "Islem Tipi", "İŞLEM TİPİ", "ISLEM TIPI", "Operation Type", "Process Type" };

            string configValue = ReadScope(model, configuration, aliases);
            string documentValue = string.IsNullOrWhiteSpace(configuration) ? "" : ReadScope(model, "", aliases);

            if (string.IsNullOrWhiteSpace(configValue)) return (documentValue ?? "").Trim();
            if (string.IsNullOrWhiteSpace(documentValue)) return configValue.Trim();

            int configScore = OperationSpecificity(configValue);
            int documentScore = OperationSpecificity(documentValue);
            return documentScore > configScore ? documentValue.Trim() : configValue.Trim();
        }

        private static string ReadScope(ModelDoc2 model, string configuration, string[] aliases)
        {
            string value = ReadFrom(model, configuration, aliases);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            value = ReadFromNormalized(model, configuration, aliases);
            return (value ?? "").Trim();
        }

        private static int OperationSpecificity(string value)
        {
            string v = NormalizeTurkish(value);
            if (v.Contains("TALASLI") || v.Contains("TORNA") || v.Contains("FREZE")) return 100;
            if (v.Contains("TESTERE")) return 90;
            if (v.Contains("LAZER")) return 80;
            if (v.Contains("KAYNAK")) return 80;
            if (v.Contains("HAZIR")) return 70;
            if (v.Contains("MONTAJ")) return 60;
            if (v.Contains("IMALAT")) return 20;
            return string.IsNullOrWhiteSpace(v) ? 0 : 10;
        }

        private static string NormalizeTurkish(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant()
                .Replace("İ", "I").Replace("İ", "I").Replace("ı", "I").Replace("Ş", "S")
                .Replace("Ğ", "G").Replace("Ü", "U").Replace("Ö", "O").Replace("Ç", "C");
        }

        public static bool IsLaserTagged(ModelDoc2 model, string configuration, out string propertyName, out string propertyValue)
        {
            propertyName = "";
            propertyValue = First(model, configuration,
                "İşlem Tipi", "Islem Tipi", "İŞLEM TİPİ", "ISLEM TIPI", "Operation Type", "Process Type");
            // Configuration-specific value wins; document property is the fallback.
            if (!LaserOperation.IsLaserProcessTag(propertyValue)) return false;
            propertyName = "İşlem Tipi";
            return true;
        }

        public static string ProductionDefinition(ModelDoc2 model, string configuration)
        {
            return First(model, configuration,
                "Ürün Tanımı", "Urun Tanimi", "ÜRÜN TANIMI", "Ürün Tanimi",
                "Üretim Tipi", "Uretim Tipi", "İşlem Tipi", "Islem Tipi",
                "Product Definition", "Production Type", "Route", "Rota");
        }

        public static bool FindFirstValueContaining(ModelDoc2 model, string configuration, string needle, out string propertyName, out string propertyValue)
        {
            propertyName = "";
            propertyValue = "";
            if (model == null || string.IsNullOrWhiteSpace(needle)) return false;
            if (FindInManager(model, configuration, needle, out propertyName, out propertyValue)) return true;
            if (!string.IsNullOrWhiteSpace(configuration) && FindInManager(model, "", needle, out propertyName, out propertyValue)) return true;
            return false;
        }

        private static bool FindInManager(ModelDoc2 model, string configuration, string needle, out string propertyName, out string propertyValue)
        {
            propertyName = "";
            propertyValue = "";
            object extensionObj = null, managerObj = null;
            try
            {
                extensionObj = model.Extension;
                dynamic extension = extensionObj;
                managerObj = extension.CustomPropertyManager[configuration ?? ""];
                dynamic manager = managerObj;
                var names = manager.GetNames() as Array;
                if (names == null) return false;
                foreach (object item in names)
                {
                    string name = Convert.ToString(item) ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    try
                    {
                        string raw = "", resolved = "";
                        manager.Get2(name, out raw, out resolved);
                        string candidate = !string.IsNullOrWhiteSpace(resolved) ? resolved : raw;
                        if (!string.IsNullOrWhiteSpace(candidate) && candidate.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            propertyName = name; propertyValue = candidate.Trim(); return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                ComUtil.Release(managerObj);
                ComUtil.Release(extensionObj);
            }
            return false;
        }

        private static string ReadFromNormalized(ModelDoc2 model, string configuration, string[] aliases)
        {
            object extensionObj = null, managerObj = null;
            try
            {
                extensionObj = model.Extension;
                dynamic extension = extensionObj;
                managerObj = extension.CustomPropertyManager[configuration ?? ""];
                dynamic manager = managerObj;
                var names = manager.GetNames() as Array;
                if (names == null) return "";

                foreach (object item in names)
                {
                    string actualName = Convert.ToString(item) ?? "";
                    string normalizedActual = NormalizePropertyName(actualName);
                    foreach (string alias in aliases)
                    {
                        if (!string.Equals(normalizedActual, NormalizePropertyName(alias), StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            string raw = "", resolved = "";
                            manager.Get2(actualName, out raw, out resolved);
                            string candidate = !string.IsNullOrWhiteSpace(resolved) ? resolved : raw;
                            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                ComUtil.Release(managerObj);
                ComUtil.Release(extensionObj);
            }
            return "";
        }

        private static string NormalizePropertyName(string value)
        {
            string v = (value ?? "").Trim().ToUpperInvariant();
            v = v.Replace("Ç", "C").Replace("Ğ", "G").Replace("İ", "I").Replace("İ", "I")
                 .Replace("Ö", "O").Replace("Ş", "S").Replace("Ü", "U");
            v = v.Replace(" ", "").Replace("_", "").Replace("-", "").Replace(".", "");
            return v;
        }

        private static string ReadFrom(ModelDoc2 model, string configuration, string[] aliases)
        {
            object extensionObj = null, managerObj = null;
            try
            {
                extensionObj = model.Extension;
                dynamic extension = extensionObj;
                managerObj = extension.CustomPropertyManager[configuration ?? ""];
                dynamic manager = managerObj;
                foreach (var alias in aliases)
                {
                    try
                    {
                        string raw = "", resolved = "";
                        manager.Get2(alias, out raw, out resolved);
                        string candidate = !string.IsNullOrWhiteSpace(resolved) ? resolved : raw;
                        if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                ComUtil.Release(managerObj);
                ComUtil.Release(extensionObj);
            }
            return "";
        }
    }
}
