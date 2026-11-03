using System;
using System.Collections.Generic;
using System.Linq;
using SrcCadWorks.Models;

internal static class LaserOperationTests
{
    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void VerifyPropertyScope()
    {
        var model = new SolidWorks.Interop.sldworks.ModelDoc2();
        var document = model.Extension.CustomPropertyManager[""];
        var configuration = model.Extension.CustomPropertyManager["Default"];
        string name, value;
        document.Values["Ürün Tanımı"] = "LAZER";
        document.Values["LAZER"] = "VAR";
        document.Values["Kaynak İşlemi"] = "VAR";
        document.Values["Kaynak"] = "KAYNAK";
        Assert(!SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "Unrelated property matched.");
        Assert(!LaserOperation.IsWeldProcessTag(value), "Weld process flag was mistaken for Operation Type.");
        document.Values["İşlem Tipi"] = "LAZER";
        Assert(SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "Document fallback failed.");
        configuration.Values["İşlem Tipi"] = "TESTERE";
        Assert(!SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "Configuration override ignored.");
        Assert(!LaserOperation.IsWeldProcessTag(value), "TESTERE with welding flag matched KAYNAK.");
        configuration.Values["İşlem Tipi"] = "  kaynak  ";
        Assert(!SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "KAYNAK matched LAZER.");
        Assert(LaserOperation.IsWeldProcessTag(value), "Resolved configuration KAYNAK value was lost.");
        configuration.Values["İşlem Tipi"] = "  lazer  ";
        Assert(SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "Trim/case handling failed.");
        configuration.Values.Clear();
        configuration.Values[" islem_tipi "] = "LAZER";
        document.Values["İşlem Tipi"] = "TESTERE";
        Assert(SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "Normalized property name failed.");
        configuration.Values.Clear();
        document.Values["İşlem Tipi"] = "KAYNAK";
        Assert(!SrcCadWorks.Services.CustomPropertyReader.IsLaserTagged(model, "Default", out name, out value), "Document KAYNAK matched LAZER.");
        Assert(LaserOperation.IsWeldProcessTag(value), "Document KAYNAK fallback failed.");
    }

    private static void VerifyProcessFilters()
    {
        string[] processTypes = { "LAZER", "  kaynak ", "TESTERE", "TALAŞLI İMALAT", "", null, "KAYNAKLI PARÇA", "LAZER+KAYNAK" };
        // Columns: neither, laser only, weld only, both.
        bool[,] expected = {
            { true, true, false, true },
            { true, false, true, true },
            { true, false, false, false },
            { true, false, false, false },
            { true, false, false, false },
            { true, false, false, false },
            { true, false, false, false },
            { true, false, false, false }
        };
        for (int row = 0; row < processTypes.Length; row++)
            for (int selection = 0; selection < 4; selection++)
                Assert(LaserOperation.MatchesProcessFilter(
                    LaserOperation.IsLaserProcessTag(processTypes[row]), LaserOperation.IsWeldProcessTag(processTypes[row]),
                    (selection & 1) != 0, (selection & 2) != 0) == expected[row, selection],
                    "Process filter mismatch: " + processTypes[row] + " / selection " + selection);
        var weldRow = new LaserPartRow { IsWeldTagged = true };
        Assert(weldRow.ControlText.StartsWith("KAYNAK ✓"), "Weld row status missing.");
    }

    private static int Main()
    {
        try
        {
            foreach (string tag in new[] { "LAZER", "lazer", "  LaZeR  " })
                Assert(LaserOperation.IsLaserProcessTag(tag), "Valid laser tag rejected.");
            foreach (string tag in new[] { null, "", "TALAŞLI İMALAT", "LAZER DEĞİL", "LAZERLİ PARÇA", "LAZER KESİM APARATI" })
                Assert(!LaserOperation.IsLaserProcessTag(tag), "Non-laser process accepted.");
            VerifyPropertyScope();
            VerifyProcessFilters();
            string[] expected = {
                "KESİM", "KESİM+BÜKÜM", "KESİM+KILAVUZ", "KESİM+BÜKÜM+KILAVUZ",
                "KESİM+HAVŞA", "KESİM+BÜKÜM+HAVŞA", "KESİM+KILAVUZ+HAVŞA", "KESİM+BÜKÜM+KILAVUZ+HAVŞA"
            };
            for (int flags = 0; flags < 8; flags++)
            {
                string operation = LaserOperation.Build((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
                Assert(operation == expected[flags], "Wrong route: " + flags);
                Assert(LaserOperation.Choices.Contains(operation), "Route absent from menu: " + operation);
            }
            string[] recessed = { "swCounterSunk", "swCounterSunkDrilled", "swCounterBored", "swCounterBoredDrilled",
                "swCounterDrilled", "swCounterSinkThru", "swCounterBoreBlind", "swCounterBoreThru",
                "swHoleBlindCounterSinkTop", "swHoleThruCounterSinkBottom", "swTapBlindCosmeticThreadCounterSinkTop" };
            foreach (string type in recessed)
            {
                Assert(LaserOperation.IsCountersinkHoleType(type), "Recess missed: " + type);
                Assert(!LaserOperation.IsCountersinkHoleType(type, true), "Suppressed recess counted: " + type);
            }
            foreach (string type in new[] { null, "", "swSimple", "swSimpleDrilled", "swHoleThru", "swTapBlind", "swPipeTapThru", "Chamfer", "Havşa1" })
                Assert(!LaserOperation.IsCountersinkHoleType(type), "False havsa: " + type);

            var rows = new List<LaserPartRow>();
            LaserPartRow.AddByPartCode(rows, new LaserPartRow {
                PartCode = "ETLC1", SourcePath = "a.SLDPRT", Configuration = "A", Quantity = 5, BaseQuantity = 5,
                Operation = "KESİM+BÜKÜM+KILAVUZ", OccurrenceSelectIds = new List<string> { "parent1" }
            });
            LaserPartRow.AddByPartCode(rows, new LaserPartRow {
                PartCode = "etlc1", SourcePath = "a.SLDPRT", Configuration = "B", Quantity = 3, BaseQuantity = 3,
                HasCountersink = true, Operation = "KESİM+HAVŞA", IsLaserTagged = true, LaserTagProperty = "İşlem Tipi", OccurrenceSelectIds = new List<string> { "parent2" }
            });
            Assert(rows[0].IsLaserTagged, "Later tagged configuration was omitted from laser filter.");
            Assert(rows.Count == 1 && rows[0].Quantity == 8, "Same-code quantities changed.");
            Assert(rows[0].Operation == "KESİM+BÜKÜM+KILAVUZ+HAVŞA" && rows[0].HasCountersink, "Later occurrence lost HAVSA.");
            Assert(rows[0].SourceOccurrences.Count == 2 && rows[0].OccurrenceSelectIds.Count == 2, "Hide identities lost.");
            Assert(LaserOperation.WithCountersink(rows[0].Operation) == rows[0].Operation, "HAVSA duplicated.");
            Assert(rows[0].ControlText.Contains("Havşa") && rows[0].CountersinkText.Contains("HAVŞA"), "Detection UI missing.");
            LaserPartRow.AddByPartCode(rows, new LaserPartRow {
                PartCode = "ETLC1", SourcePath = "a.SLDPRT", Configuration = "C", Quantity = 2, BaseQuantity = 2,
                IsWeldTagged = true, OccurrenceSelectIds = new List<string> { "parent3" }
            });
            Assert(rows.Count == 1 && rows[0].Quantity == 10 && rows[0].IsLaserTagged && rows[0].IsWeldTagged,
                "Later KAYNAK configuration lost its tag or duplicated a code.");
            Assert(rows[0].SourceOccurrences.Count == 3 && rows[0].OccurrenceSelectIds.Count == 3, "Weld merge lost hide identities.");
            Assert(rows[0].ControlText.Contains("LAZER ✓") && rows[0].ControlText.Contains("KAYNAK ✓"), "Combined tags missing.");
            Assert(LaserOperation.MatchesProcessFilter(rows[0].IsLaserTagged, rows[0].IsWeldTagged, false, true),
                "Merged code absent from weld-only filter.");
            Assert(LaserOperation.MatchesProcessFilter(rows[0].IsLaserTagged, rows[0].IsWeldTagged, false, false),
                "Disabled tag filters excluded a merged sheet-metal code.");
            rows[0].Operation = "KESİM"; // Explicit manual override survives production regrouping.
            Assert(LaserPartRow.GroupByPartCode(rows)[0].Operation == "KESİM", "Manual choice overwritten.");
            Console.WriteLine("PASS: LAZER/KAYNAK process filters, property scope, 8 routes, hole types, suppression classification, same-code merge, hide identities and manual choice.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

// COM substitutes exercise the real property reader without SOLIDWORKS installed.
namespace SolidWorks.Interop.sldworks
{
    public class ModelDoc2 { public FakeExtension Extension { get; } = new FakeExtension(); }
    public class FakeExtension { public FakeManagers CustomPropertyManager { get; } = new FakeManagers(); }
    public class FakeManagers
    {
        private readonly Dictionary<string, FakeManager> _items = new Dictionary<string, FakeManager>();
        public FakeManager this[string key]
        {
            get
            {
                FakeManager value;
                if (!_items.TryGetValue(key, out value)) _items[key] = value = new FakeManager();
                return value;
            }
        }
    }
    public class FakeManager
    {
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();
        public string[] GetNames() { return Values.Keys.ToArray(); }
        public void Get2(string name, out string raw, out string resolved)
        {
            Values.TryGetValue(name, out raw);
            resolved = raw;
        }
    }
}
namespace SrcCadWorks.Services { internal static class ComUtil { public static void Release(object value) { } } }
