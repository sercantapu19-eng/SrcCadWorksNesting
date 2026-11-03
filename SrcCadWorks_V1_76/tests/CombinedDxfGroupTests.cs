using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.Services
{
    // Standalone test seam: no SOLIDWORKS installation is needed for the builder.
    internal static class Logger { public static void Info(string text) { } }
}
internal static class CombinedDxfGroupTests
{
    private sealed class Entity
    {
        public string Type;
        public Dictionary<int, string> Values = new Dictionary<int, string>();
        public string Value(int code) { string value; return Values.TryGetValue(code, out value) ? value : ""; }
        public double Number(int code) { return double.Parse(Value(code), CultureInfo.InvariantCulture); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static List<Entity> ReadEntities(string path)
    {
        string[] lines = File.ReadAllText(path, System.Text.Encoding.GetEncoding(1254)).Replace("\r", "").TrimEnd('\n').Split('\n');
        var entities = new List<Entity>();
        bool active = false;
        Entity current = null;
        for (int index = 0; index + 1 < lines.Length; index += 2)
        {
            int code = int.Parse(lines[index].Trim(), CultureInfo.InvariantCulture);
            string value = lines[index + 1].Trim();
            if (!active) { if (code == 2 && value == "ENTITIES") active = true; continue; }
            if (code == 0)
            {
                if (current != null) entities.Add(current);
                if (value == "ENDSEC") break;
                current = new Entity { Type = value };
            }
            else if (current != null) current.Values[code] = value;
        }
        return entities;
    }
    private static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "SrcCadWorksDxfTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var rows = new List<LaserPartRow>();
            var sources = new Dictionary<string, string>();
            double?[] thicknesses = { 3, 2, 10, 2, 2.5, null, 1.2 };
            const string fixture = "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n0\n10\n0\n20\n0\n11\n40\n21\n0\n0\nCIRCLE\n8\n0\n10\n10\n20\n15\n40\n3\n0\nARC\n8\n0\n10\n20\n20\n10\n40\n5\n50\n20\n51\n160\n0\nENDSEC\n0\nEOF\n";
            for (int index = 0; index < thicknesses.Length; index++)
            {
                string code = "P" + index.ToString();
                string source = Path.Combine(dir, code + ".dxf");
                File.WriteAllText(source, fixture);
                sources[code] = source;
                rows.Add(new LaserPartRow { PartCode = code, ThicknessMm = thicknesses[index], Quantity = index + 1, Material = "ST37" });
            }
            string output = Path.Combine(dir, "TopluDXF.dxf");
            var builder = new CombinedDxfBuilder();
            builder.Build(rows, row => sources[row.PartCode], output);
            CombinedDxfBuilder.ValidateForImport(output);
            // Separator is allowed; arbitrary unknown layers must still be rejected.
            string invalidLayerOutput = Path.Combine(dir, "UnknownLayer.dxf");
            var encoding = System.Text.Encoding.GetEncoding(1254);
            File.WriteAllText(invalidLayerOutput, File.ReadAllText(output, encoding).Replace("SRC_SEPARATOR", "SRC_UNEXPECTED"), encoding);
            bool unknownLayerRejected = false;
            try { CombinedDxfBuilder.ValidateForImport(invalidLayerOutput); }
            catch (InvalidOperationException ex) { unknownLayerRejected = ex.Message.Contains("SRC_UNEXPECTED"); }
            Assert(unknownLayerRejected, "Unknown layer validation was bypassed.");
            var entities = ReadEntities(output);
            var headings = entities.Where(entity => entity.Type == "TEXT" && Math.Abs(entity.Number(40) - 8) < 0.00001).OrderByDescending(entity => entity.Number(20)).ToList();
            Assert(headings.Select(entity => entity.Value(1)).SequenceEqual(new[] { "1.2 MM", "2 MM", "2.5 MM", "3 MM", "10 MM", "KALINLIK BILINMIYOR" }), "Numeric top-to-bottom group order failed.");
            var partTexts = entities.Where(entity => entity.Type == "TEXT" && entity.Value(1).StartsWith("PARCA KODU:")).OrderByDescending(entity => entity.Number(20)).ToList();
            Assert(partTexts.Select(entity => entity.Value(1)).SequenceEqual(new[] { "PARCA KODU: P6", "PARCA KODU: P1", "PARCA KODU: P3", "PARCA KODU: P4", "PARCA KODU: P0", "PARCA KODU: P2", "PARCA KODU: P5" }), "Part grouping or within-group order failed.");
            Assert(entities.Count(entity => entity.Type == "LINE" && entity.Value(8) == "SRC_SEPARATOR") == 6, "A separator per group was not preserved.");
            foreach (var entity in entities.Where(item => item.Type == "CIRCLE")) Assert(Math.Abs(entity.Number(40) - 3) < 0.000001, "Circle radius changed.");
            foreach (var entity in entities.Where(item => item.Type == "ARC"))
            {
                Assert(Math.Abs(entity.Number(40) - 5) < 0.000001, "Arc radius changed.");
                Assert(Math.Abs((entity.Number(51) - entity.Number(50) + 360) % 360 - 140) < 0.000001, "Arc sweep changed.");
            }
            foreach (var entity in entities.Where(item => item.Type == "LINE" && item.Value(8) == "SRC_GEOMETRY"))
            {
                double dx = entity.Number(11) - entity.Number(10), dy = entity.Number(21) - entity.Number(20);
                Assert(Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 40) < 0.000001, "Line length changed.");
            }
            Assert(entities.Count(entity => entity.Value(8) == "SRC_GEOMETRY") == 21, "Original geometry count changed.");
            Assert(entities.Count(entity => entity.Type == "TEXT") == 34, "Part metadata or group labels missing.");

            // SmartNest uses the same production R12 importer path as Toplu DXF.
            // Two sheets with identical local coordinates must be separated in model space.
            string nestOutput = Path.Combine(dir, "SMARTNEST_TEST.dxf");
            var nest = new SmartNestResult
            {
                SheetCount = 2,
                SheetWidth = 100,
                SheetHeight = 200,
                Placements = new List<SmartNestPlacement>
                {
                    new SmartNestPlacement
                    {
                        PartName = "NEST_A", SourceDxfPath = sources["P0"], SheetIndex = 0,
                        X = 5, Y = 5, RotationDegrees = 0,
                        Contour = new List<SmartNestPoint> { new SmartNestPoint(5,5), new SmartNestPoint(45,5), new SmartNestPoint(45,20), new SmartNestPoint(5,20) }
                    },
                    new SmartNestPlacement
                    {
                        PartName = "NEST_B", SourceDxfPath = sources["P0"], SheetIndex = 1,
                        X = 5, Y = 5, RotationDegrees = 0,
                        Contour = new List<SmartNestPoint> { new SmartNestPoint(5,5), new SmartNestPoint(45,5), new SmartNestPoint(45,20), new SmartNestPoint(5,20) }
                    }
                }
            };
            new CombinedDxfBuilder().BuildNesting(nest, nestOutput);
            CombinedDxfBuilder.ValidateForImport(nestOutput);
            var nestEntities = ReadEntities(nestOutput);
            var firstSheetLine = nestEntities.First(entity => entity.Type == "LINE" && entity.Value(8) == "SMARTNEST_SHEET_1_GEOMETRY");
            var secondSheetLine = nestEntities.First(entity => entity.Type == "LINE" && entity.Value(8) == "SMARTNEST_SHEET_2_GEOMETRY");
            Assert(Math.Abs((secondSheetLine.Number(10) - firstSheetLine.Number(10)) - 205.0) < 0.000001,
                "SmartNest sheets were not tiled apart in the combined DXF.");
            DxfColorNormalizer.NormalizeProductionColors(nestOutput);
            DxfColorNormalizer.ValidateNativeDxf(nestOutput);
            Console.WriteLine("PASS: SmartNest R12 validation, two-sheet separation, and production color normalization.");

            byte[] validOutput = File.ReadAllBytes(output);
            File.WriteAllText(sources["P0"], "0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n");
            bool rejected = false;
            try { builder.Build(rows, row => sources[row.PartCode], output); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && validOutput.SequenceEqual(File.ReadAllBytes(output)), "Invalid input replaced a valid output.");
            Console.WriteLine("PASS: groups, decimal thickness, unknown thickness, numeric order, separators, geometry dimensions/counts, metadata and empty-input protection.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
