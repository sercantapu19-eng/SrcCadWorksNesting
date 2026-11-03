using System;
using System.IO;
using System.Text.RegularExpressions;

namespace SrcCadWorks.Services
{
    internal static class ManufacturingIdentity
    {
        public static string Key(string path, string configuration)
        {
            return (path ?? "").Trim() + "|" + (configuration ?? "").Trim();
        }

        public static string PartCode(string path, string propertyCode)
        {
            string fileCode = Path.GetFileNameWithoutExtension(path) ?? "";
            // ETLC files are numbered independently, including mirrored parts.
            // An inherited property must not rename a mirror to its parent code.
            if (Regex.IsMatch(fileCode, @"^ETLC\d+$", RegexOptions.IgnoreCase)) return fileCode;
            return string.IsNullOrWhiteSpace(propertyCode) ? fileCode : propertyCode.Trim();
        }

        // Montaj Hazırla's canonical identity for STEP-derived/catalog components.
        // Only a company-specific Ürün Kodu / Parça Kodu value is allowed to override
        // the document's own file stem; generic vendor Part Number/Number is not.
        public static string AssemblyPreparationPartCode(string path, string companyPartCode)
        {
            if (!string.IsNullOrWhiteSpace(companyPartCode)) return companyPartCode.Trim();
            return Path.GetFileNameWithoutExtension(path) ?? "";
        }

        public static string Drawing(string path, string propertyCode, string resolvedCode, string metadataDrawing)
        {
            if (string.Equals(propertyCode, resolvedCode, StringComparison.OrdinalIgnoreCase))
                return metadataDrawing ?? "";
            string ownDrawing = Path.ChangeExtension(path, ".SLDDRW");
            // Never substitute the source part's drawing when a mirror has no drawing.
            return File.Exists(ownDrawing) ? ownDrawing : "";
        }
    }
}
