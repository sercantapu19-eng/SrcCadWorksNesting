using System;
using System.IO;
using SrcCadWorks.Services;

internal static class DxfContourReaderExtendedTests
{
    private static int passed, failed;
    private static string Dxf(string entities)
    {
        string p = Path.Combine(Path.GetTempPath(), "SrcCadWorks_ExtDxf_" + Guid.NewGuid().ToString("N") + ".dxf");
        File.WriteAllText(p, "0\r\nSECTION\r\n2\r\nENTITIES\r\n" + entities + "0\r\nENDSEC\r\n0\r\nEOF\r\n");
        return p;
    }
    private static void Area(string name, string entities, double expected, double tolerance)
    {
        string p = Dxf(entities);
        try {
            var c = DxfContourReader.ReadLargestClosedContour(p);
            double a = 0;
            for (int i=0;i<c.Count;i++) { var x=c[i]; var y=c[(i+1)%c.Count]; a += x.X*y.Y-y.X*x.Y; }
            a = Math.Abs(a/2.0);
            if (Math.Abs(a-expected)>tolerance) throw new Exception("area expected "+expected+", got "+a);
            passed++; Console.WriteLine("PASS "+name);
        } catch(Exception e) { failed++; Console.WriteLine("FAIL "+name+": "+e.Message); }
        finally { try { File.Delete(p); } catch {} }
    }
    private static void RejectOpen()
    {
        string p=Dxf("0\r\nLINE\r\n10\r\n0\r\n20\r\n0\r\n11\r\n10\r\n21\r\n0\r\n");
        try { try { DxfContourReader.ReadLargestClosedContour(p); throw new Exception("open line accepted"); }
            catch(InvalidDataException) { passed++; Console.WriteLine("PASS open geometry rejected"); } }
        catch(Exception e) { failed++; Console.WriteLine("FAIL open geometry rejected: "+e.Message); }
        finally { try { File.Delete(p); } catch {} }
    }
    public static int Main()
    {
        Area("full ELLIPSE", "0\r\nELLIPSE\r\n10\r\n0\r\n20\r\n0\r\n11\r\n10\r\n21\r\n0\r\n40\r\n0.5\r\n41\r\n0\r\n42\r\n6.283185307179586\r\n", Math.PI*50, 1.0);
        Area("closed SPLINE fit points", "0\r\nSPLINE\r\n70\r\n1\r\n11\r\n0\r\n21\r\n0\r\n11\r\n30\r\n21\r\n0\r\n11\r\n30\r\n21\r\n20\r\n11\r\n0\r\n21\r\n20\r\n", 600, 1e-6);
        Area("LWPOLYLINE semicircle bulge", "0\r\nLWPOLYLINE\r\n70\r\n1\r\n90\r\n2\r\n10\r\n-10\r\n20\r\n0\r\n42\r\n1\r\n10\r\n10\r\n20\r\n0\r\n", Math.PI*50, 1.0);
        RejectOpen();
        Console.WriteLine("Extended DXF regression: "+passed+"/4 PASS, "+failed+" FAIL");
        return failed==0 && passed==4 ? 0 : 1;
    }
}