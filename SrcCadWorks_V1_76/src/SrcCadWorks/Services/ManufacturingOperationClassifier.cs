using System;

namespace SrcCadWorks.Services
{
    internal static class ManufacturingOperationClassifier
    {
        public static bool IsSaw(string value)
        {
            string v = Normalize(value);
            return v.Contains("TESTERE");
        }

        public static bool IsMachining(string value)
        {
            string v = Normalize(value);
            return v.Contains("TALASLI");
        }

        public static string Normalize(string value)
        {
            string v = (value ?? "").Trim().ToUpperInvariant();
            return v.Replace("İ", "I").Replace("ı", "I").Replace("Ş", "S").Replace("Ğ", "G")
                    .Replace("Ü", "U").Replace("Ö", "O").Replace("Ç", "C");
        }
    }
}
