using System;
using System.IO;
using SrcCadWorks.Services;

internal static class ManufacturingIdentityTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Main()
    {
        Check(ManufacturingIdentity.PartCode("ETLC00053213.SLDPRT", "ETLC00053212") == "ETLC00053213", "Mirror must retain own file code");
        Check(ManufacturingIdentity.PartCode("ETLC00053214.SLDPRT", "ETLC00053215") == "ETLC00053214", "Coded filename must win over inherited property");
        Check(ManufacturingIdentity.PartCode("bracket.SLDPRT", "P-42") == "P-42", "Other custom codes must be retained");
        string fastenerFile = Path.Combine("CAD", "M5x10 Havşa İmbus Civata_DIN7991.SLDPRT");
        Check(ManufacturingIdentity.AssemblyPreparationPartCode(fastenerFile, "") == "M5x10 Havşa İmbus Civata_DIN7991", "Imported vendor part-number must not replace the local document name");
        Check(ManufacturingIdentity.AssemblyPreparationPartCode(fastenerFile, "M5X10-LOCAL") == "M5X10-LOCAL", "Explicit company product code must be preserved");
        Check(ManufacturingIdentity.AssemblyPreparationPartCode("DRLC00060018.SLDASM", null) == "DRLC00060018", "Assembly document name must be the fallback identity");
        Check(ManufacturingIdentity.Key("ETLC00053212.SLDPRT", "A") != ManufacturingIdentity.Key("ETLC00053213.SLDPRT", "A"), "Different files must not merge");
        Check(ManufacturingIdentity.Key("part.SLDPRT", "A") != ManufacturingIdentity.Key("part.SLDPRT", "B"), "Different configurations must not merge");
        var directory = Path.Combine(Path.GetTempPath(), "SrcCadIdentity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mirror = Path.Combine(directory, "ETLC00053213.SLDPRT");
            string parentDrawing = Path.Combine(directory, "ETLC00053212.SLDDRW");
            File.WriteAllText(parentDrawing, "parent");
            Check(ManufacturingIdentity.Drawing(mirror, "ETLC00053212", "ETLC00053213", parentDrawing) == "", "Missing mirror drawing must not fall back to parent");
            string ownDrawing = Path.ChangeExtension(mirror, ".SLDDRW");
            File.WriteAllText(ownDrawing, "mirror");
            Check(ManufacturingIdentity.Drawing(mirror, "ETLC00053212", "ETLC00053213", parentDrawing) == ownDrawing, "Own mirror drawing must be used");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: 10 manufacturing identity scenarios.");
    }
}
