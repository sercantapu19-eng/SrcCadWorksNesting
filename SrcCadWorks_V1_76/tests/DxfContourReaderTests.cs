using System;
using System.IO;
using SrcCadWorks.Services;

internal static class DxfContourReaderTests
{
    private static int _passed;

    private static void AssertArea(string name, string entities, double expectedArea, double tolerance)
    {
        string path = Path.Combine(Path.GetTempPath(), "SrcCadWorks_DxfContour_" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            File.WriteAllText(path, "0\r\nSECTION\r\n2\r\nENTITIES\r\n" + entities + "0\r\nENDSEC\r\n0\r\nEOF\r\n");
            var contour = DxfContourReader.ReadLargestClosedContour(path);
            double area = 0;
            for (int i = 0; i < contour.Count; i++)
            {
                var a = contour[i]; var b = contour[(i + 1) % contour.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            area = Math.Abs(area / 2.0);
            if (Math.Abs(area - expectedArea) > tolerance)
                throw new Exception(name + " alanı beklenenden farklı. Beklenen=" + expectedArea + ", Gerçek=" + area);
            _passed++;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    public static int Main()
    {
        // Modern closed polyline.
        AssertArea("LWPOLYLINE", "0\r\nLWPOLYLINE\r\n70\r\n1\r\n90\r\n4\r\n10\r\n0\r\n20\r\n0\r\n10\r\n100\r\n20\r\n0\r\n10\r\n100\r\n20\r\n50\r\n10\r\n0\r\n20\r\n50\r\n", 5000, 1e-6);

        // Legacy POLYLINE/VERTEX/SEQEND.
        AssertArea("POLYLINE", "0\r\nPOLYLINE\r\n70\r\n1\r\n0\r\nVERTEX\r\n10\r\n0\r\n20\r\n0\r\n0\r\nVERTEX\r\n10\r\n40\r\n20\r\n0\r\n0\r\nVERTEX\r\n10\r\n40\r\n20\r\n20\r\n0\r\nVERTEX\r\n10\r\n0\r\n20\r\n20\r\n0\r\nSEQEND\r\n", 800, 1e-6);

        // Four separate LINE entities chained at their endpoints.
        AssertArea("LINE chain", "0\r\nLINE\r\n10\r\n0\r\n20\r\n0\r\n11\r\n80\r\n21\r\n0\r\n0\r\nLINE\r\n10\r\n80\r\n20\r\n0\r\n11\r\n80\r\n21\r\n40\r\n0\r\nLINE\r\n10\r\n80\r\n20\r\n40\r\n11\r\n0\r\n21\r\n40\r\n0\r\nLINE\r\n10\r\n0\r\n20\r\n40\r\n11\r\n0\r\n21\r\n0\r\n", 3200, 1e-6);

        // Semicircular ARC plus its diameter LINE.
        AssertArea("ARC + LINE", "0\r\nARC\r\n10\r\n0\r\n20\r\n0\r\n40\r\n10\r\n50\r\n0\r\n51\r\n180\r\n0\r\nLINE\r\n10\r\n-10\r\n20\r\n0\r\n11\r\n10\r\n21\r\n0\r\n", Math.PI * 50, 1.0);

        // Circle entity.
        AssertArea("CIRCLE", "0\r\nCIRCLE\r\n10\r\n25\r\n20\r\n15\r\n40\r\n5\r\n", Math.PI * 25, 1.0);

        Console.WriteLine("DxfContourReader tests: " + _passed + "/5 PASS");
        return _passed == 5 ? 0 : 1;
    }
}
