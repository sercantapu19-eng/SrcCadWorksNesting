using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SrcCadWorks.Services
{
    internal static class DxfColorNormalizer
    {
        private sealed class Pair
        {
            public string Code;
            public string Value;
            public Pair(string code, string value) { Code = code; Value = value; }
        }

        public static void NormalizeProductionColors(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Renk normalizasyonu için DXF bulunamadı.", path);

            Encoding encoding = Encoding.GetEncoding(1254);
            string[] lines = File.ReadAllLines(path, encoding);
            if (lines.Length < 20)
                throw new InvalidOperationException("DXF renk normalizasyonu: dosya çok kısa.");

            var pairs = new List<Pair>(lines.Length / 2 + 16);
            for (int i = 0; i + 1 < lines.Length; i += 2)
                pairs.Add(new Pair(lines[i], lines[i + 1]));

            bool inEntities = false;
            bool inLayerTable = false;
            int changedRecords = 0;

            for (int i = 0; i < pairs.Count;)
            {
                int code = ParseCode(pairs[i].Code);
                string value = pairs[i].Value.Trim();

                if (code == 0 && string.Equals(value, "SECTION", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < pairs.Count && ParseCode(pairs[i + 1].Code) == 2)
                    {
                        string section = pairs[i + 1].Value.Trim();
                        inEntities = string.Equals(section, "ENTITIES", StringComparison.OrdinalIgnoreCase);
                    }
                    i++;
                    continue;
                }

                if (code == 0 && string.Equals(value, "ENDSEC", StringComparison.OrdinalIgnoreCase))
                {
                    inEntities = false;
                    inLayerTable = false;
                    i++;
                    continue;
                }

                if (code == 0 && string.Equals(value, "TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    inLayerTable = i + 1 < pairs.Count &&
                        ParseCode(pairs[i + 1].Code) == 2 &&
                        string.Equals(pairs[i + 1].Value.Trim(), "LAYER", StringComparison.OrdinalIgnoreCase);
                    i++;
                    continue;
                }

                if (code == 0 && string.Equals(value, "ENDTAB", StringComparison.OrdinalIgnoreCase))
                {
                    inLayerTable = false;
                    i++;
                    continue;
                }

                if (code != 0)
                {
                    i++;
                    continue;
                }

                int end = i + 1;
                while (end < pairs.Count && ParseCode(pairs[end].Code) != 0) end++;

                if (inLayerTable && string.Equals(value, "LAYER", StringComparison.OrdinalIgnoreCase))
                {
                    string layer = FindValue(pairs, i + 1, end, 2);
                    int color;
                    if (TryProductionColor(layer, out color))
                    {
                        SetOrInsertColor(pairs, i + 1, ref end, color, false);
                        if (string.Equals(layer, "SRC_SEPARATOR", StringComparison.OrdinalIgnoreCase))
                            SetSeparatorStyle(pairs, i + 1, ref end);
                        changedRecords++;
                    }
                }
                else if (inEntities)
                {
                    string layer = FindValue(pairs, i + 1, end, 8);
                    int color;
                    if (TryProductionColor(layer, out color))
                    {
                        // ACI 62 must win. TrueColor (420/430) is removed only on our own
                        // SRC_* entities; every other entity/section remains byte-logically intact.
                        SetOrInsertColor(pairs, i + 1, ref end, color, true);
                        if (string.Equals(layer, "SRC_SEPARATOR", StringComparison.OrdinalIgnoreCase))
                            SetSeparatorStyle(pairs, i + 1, ref end);
                        changedRecords++;
                    }
                }

                i = end;
            }

            if (changedRecords <= 0)
                throw new InvalidOperationException("DXF renk normalizasyonu: SRC_* layer/entity bulunamadı.");

            string temp = path + ".src_color.tmp";
            using (var writer = new StreamWriter(temp, false, encoding))
            {
                for (int i = 0; i < pairs.Count; i++)
                {
                    writer.WriteLine(pairs[i].Code);
                    writer.WriteLine(pairs[i].Value);
                }
            }

            ValidateNativeDxf(temp);

            string backup = path + ".src_color_backup";
            try
            {
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Replace(temp, path, backup, true);
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
            catch
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }

            ValidateNativeDxf(path);
            Logger.Info("SOLIDWORKS TopluDXF renkleri normalize edildi. Kayıt=" + changedRecords);
        }

        public static void ValidateNativeDxf(string path)
        {
            string text = File.ReadAllText(path, Encoding.GetEncoding(1254));
            if (text.IndexOf("$ACADVER", StringComparison.OrdinalIgnoreCase) < 0 ||
                text.IndexOf("ENTITIES", StringComparison.OrdinalIgnoreCase) < 0 ||
                text.IndexOf("EOF", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("SOLIDWORKS DXF doğrulama başarısız: temel DXF bölümleri eksik.");

            if (text.IndexOf("SRC_GEOMETRY", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("SMARTNEST_SHEET_1_GEOMETRY", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("SOLIDWORKS DXF doğrulama başarısız: kesim geometrisi layer'ı bulunamadı.");
        }

        private static void SetOrInsertColor(List<Pair> pairs, int start, ref int end, int color, bool removeTrueColor)
        {
            int colorIndex = -1;
            int layerIndex = -1;

            for (int i = start; i < end; i++)
            {
                int code = ParseCode(pairs[i].Code);
                if (code == 8 || code == 2) layerIndex = i;
                if (code == 62) colorIndex = i;
            }

            if (removeTrueColor)
            {
                for (int i = end - 1; i >= start; i--)
                {
                    int code = ParseCode(pairs[i].Code);
                    if (code == 420 || code == 430)
                    {
                        pairs.RemoveAt(i);
                        end--;
                        if (colorIndex > i) colorIndex--;
                        if (layerIndex > i) layerIndex--;
                    }
                }
            }

            if (colorIndex >= 0)
            {
                pairs[colorIndex].Value = color.ToString();
                return;
            }

            int insertAt = layerIndex >= 0 ? layerIndex + 1 : start;
            pairs.Insert(insertAt, new Pair("62", color.ToString()));
            end++;
        }

        private static void SetSeparatorStyle(List<Pair> pairs, int start, ref int end)
        {
            // Applied only to final SOLIDWORKS DXF, never the R12 import file.
            int insert = end;
            for (int i = start; i < end; i++)
                if (ParseCode(pairs[i].Code) == 100 && pairs[i].Value.Trim() == "AcDbLine") { insert = i; break; }
            for (int i = end - 1; i >= start; i--)
            {
                int code = ParseCode(pairs[i].Code);
                if (code == 370 || code == 420 || code == 430)
                {
                    pairs.RemoveAt(i); end--;
                    if (i < insert) insert--;
                }
            }
            pairs.Insert(insert++, new Pair("370", "50")); // 0.50 mm
            pairs.Insert(insert, new Pair("420", "16777215")); // RGB white
            end += 2;
        }

        private static string FindValue(List<Pair> pairs, int start, int end, int wantedCode)
        {
            for (int i = start; i < end; i++)
                if (ParseCode(pairs[i].Code) == wantedCode)
                    return pairs[i].Value.Trim();
            return "";
        }

        private static bool TryProductionColor(string layer, out int color)
        {
            color = 7;
            if (string.Equals(layer, "SRC_SEPARATOR", StringComparison.OrdinalIgnoreCase)) return true;
            string normalized = layer ?? "";
            if (normalized.StartsWith("SMARTNEST_SHEET_", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.EndsWith("_GEOMETRY", StringComparison.OrdinalIgnoreCase)) normalized = "SRC_GEOMETRY";
                else if (normalized.EndsWith("_BEND", StringComparison.OrdinalIgnoreCase)) normalized = "SRC_BEND";
                else if (normalized.EndsWith("_CENTER", StringComparison.OrdinalIgnoreCase)) normalized = "SRC_CENTER";
            }
            if (string.Equals(normalized, "SRC_GEOMETRY", StringComparison.OrdinalIgnoreCase))
            {
                color = 7; // white on dark AutoCAD background
                return true;
            }
            if (string.Equals(normalized, "SRC_BEND", StringComparison.OrdinalIgnoreCase))
            {
                color = 3;
                return true;
            }
            if (string.Equals(normalized, "SRC_CENTER", StringComparison.OrdinalIgnoreCase))
            {
                color = 4;
                return true;
            }
            if (string.Equals(normalized, "SRC_INFO", StringComparison.OrdinalIgnoreCase))
            {
                color = 1;
                return true;
            }
            return false;
        }

        private static int ParseCode(string value)
        {
            int code;
            return int.TryParse((value ?? "").Trim(), out code) ? code : int.MinValue;
        }
    }
}
