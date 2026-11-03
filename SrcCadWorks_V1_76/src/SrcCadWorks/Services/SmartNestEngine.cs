using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SrcCadWorks.Services
{
    /// <summary>Polygon-based 2D nesting core. Coordinates are millimetres.</summary>
    public sealed class SmartNestPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public SmartNestPoint() { }
        public SmartNestPoint(double x, double y) { X = x; Y = y; }
    }

    public sealed class SmartNestPart
    {
        public string Name { get; set; }
        public int Quantity { get; set; }
        public IList<SmartNestPoint> OuterContour { get; set; }
        public IList<IList<SmartNestPoint>> InnerContours { get; set; }
        public bool AllowRotation { get; set; }
        public double RotationStepDegrees { get; set; }
        public SmartNestPart()
        {
            Quantity = 1; AllowRotation = true; RotationStepDegrees = 5.0;
            OuterContour = new List<SmartNestPoint>();
            InnerContours = new List<IList<SmartNestPoint>>();
        }
    }

    public sealed class SmartNestSettings
    {
        public double SheetWidth { get; set; }
        public double SheetHeight { get; set; }
        public double PartGap { get; set; }
        public double EdgeMargin { get; set; }
        public double Kerf { get; set; }
        public SmartNestSettings()
        {
            SheetWidth = 1000; SheetHeight = 2000; PartGap = 5.0; EdgeMargin = 5.0; Kerf = 0.0;
        }
    }

    public sealed class SmartNestPlacement
    {
        public string PartName { get; set; }
        public int SheetIndex { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double RotationDegrees { get; set; }
        public IList<SmartNestPoint> Contour { get; set; }
    }

    public sealed class SmartNestResult
    {
        public IList<SmartNestPlacement> Placements { get; set; }
        public int SheetCount { get; set; }
        public double UtilizationPercent { get; set; }
        public double UsedPartArea { get; set; }
        public double TotalSheetArea { get; set; }
        public SmartNestResult() { Placements = new List<SmartNestPlacement>(); }
    }

    /// <summary>
    /// Deterministic polygon nesting using rotated real outer contours and polygon intersection.
    /// This first engine version intentionally rejects invalid/open contours rather than silently nesting them.
    /// Inner contours are retained for DXF output; collision checks use outer contours conservatively.
    /// </summary>
    public static class SmartNestEngine
    {
        private sealed class Candidate { public IList<SmartNestPoint> P; public double A; public double W; public double H; public double MinX; public double MinY; }

        public static SmartNestResult Nest(IEnumerable<SmartNestPart> source, SmartNestSettings settings)
        {
            return Nest(source, settings, null);
        }

        public static SmartNestResult Nest(IEnumerable<SmartNestPart> source, SmartNestSettings settings, Action<int, int, long> progress)
        {
            Validate(settings);
            var parts = new List<SmartNestPart>();
            foreach (var p in source ?? Enumerable.Empty<SmartNestPart>())
            {
                if (p == null || p.Quantity < 1) continue;
                ValidateContour(p.Name, p.OuterContour);
                for (int i = 0; i < p.Quantity; i++) parts.Add(p);
            }
            parts = parts.OrderByDescending(p => Math.Abs(SignedArea(p.OuterContour))).ToList();
            var result = new SmartNestResult();
            result.TotalSheetArea = settings.SheetWidth * settings.SheetHeight;
            long candidateChecks = 0;
            int totalParts = parts.Count;
            for (int partIndex = 0; partIndex < totalParts; partIndex++)
            {
                var part = parts[partIndex];
                Candidate best = null; int bestSheet = -1; double bestX = 0, bestY = 0;
                if (progress != null) progress(partIndex, totalParts, candidateChecks);
                int existingSheets = Math.Max(1, result.SheetCount);
                for (int s = 0; s < existingSheets; s++)
                {
                    FindBest(part, settings, result.Placements.Where(x => x.SheetIndex == s).ToList(), s, ref best, ref bestSheet, ref bestX, ref bestY, ref candidateChecks, progress, partIndex, totalParts);
                    if (best != null) break;
                }
                if (best == null)
                {
                    int s = result.SheetCount;
                    FindBest(part, settings, new List<SmartNestPlacement>(), s, ref best, ref bestSheet, ref bestX, ref bestY, ref candidateChecks, progress, partIndex, totalParts);
                    if (best == null) throw new InvalidOperationException("Parça sac alanına sığmıyor: " + part.Name);
                    result.SheetCount = Math.Max(result.SheetCount, s + 1);
                }
                result.SheetCount = Math.Max(result.SheetCount, bestSheet + 1);
                result.Placements.Add(new SmartNestPlacement { PartName = part.Name ?? "Parça", SheetIndex = bestSheet, X = bestX, Y = bestY, RotationDegrees = best.A, Contour = Translate(best.P, bestX, bestY) });
                result.UsedPartArea += Math.Abs(SignedArea(part.OuterContour));
                if (progress != null) progress(partIndex + 1, totalParts, candidateChecks);
            }
            result.UtilizationPercent = result.TotalSheetArea * result.SheetCount <= 0 ? 0 : 100.0 * result.UsedPartArea / (result.TotalSheetArea * result.SheetCount);
            return result;
        }

        private static void FindBest(SmartNestPart part, SmartNestSettings cfg, IList<SmartNestPlacement> placed, int sheet,
            ref Candidate best, ref int bestSheet, ref double bestX, ref double bestY,
            ref long candidateChecks, Action<int, int, long> progress, int partIndex, int totalParts)
        {
            // Expand existing parts once per search, not once for every tested position.
            double clearance = cfg.PartGap + cfg.Kerf;
            var expandedPlaced = placed.Select(old => ExpandRadially(old.Contour, clearance / 2.0)).ToList();
            double step = part.AllowRotation ? Math.Max(1, Math.Min(90, part.RotationStepDegrees)) : 360;
            int rotations = part.AllowRotation ? (int)Math.Ceiling(360.0 / step) : 1;
            for (int r = 0; r < rotations; r++)
            {
                double angle = part.AllowRotation ? r * step : 0;
                var rotated = Rotate(part.OuterContour, angle);
                var minX = rotated.Min(q => q.X); var minY = rotated.Min(q => q.Y);
                var normalized = rotated.Select(q => new SmartNestPoint(q.X - minX, q.Y - minY)).ToList();
                double w = normalized.Max(q => q.X), h = normalized.Max(q => q.Y);
                double maxX = cfg.SheetWidth - cfg.EdgeMargin - w;
                double maxY = cfg.SheetHeight - cfg.EdgeMargin - h;
                double searchStep = Math.Max(10, Math.Min(20, clearance <= 0 ? 10 : clearance * 2.0));
                for (double y = cfg.EdgeMargin; y <= maxY + 1e-7; y += searchStep)
                for (double x = cfg.EdgeMargin; x <= maxX + 1e-7; x += searchStep)
                {
                    candidateChecks++;
                    var candidate = Translate(normalized, x, y);
                    var newExpanded = ExpandRadially(candidate, clearance / 2.0);
                    bool hit = false;
                    foreach (var oldExpanded in expandedPlaced)
                    {
                        if (PolygonsIntersect(newExpanded, oldExpanded)) { hit = true; break; }
                    }
                    if (progress != null && candidateChecks % 10000 == 0)
                        progress(partIndex, totalParts, candidateChecks);
                    if (!hit)
                    {
                        best = new Candidate { P = normalized, A = angle, W = w, H = h, MinX = minX, MinY = minY };
                        bestSheet = sheet; bestX = x; bestY = y; return;
                    }
                }
            }
        }

        public static void WriteDxf(string path, SmartNestResult result)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("DXF yolu boş.", "path");
            if (result == null) throw new ArgumentNullException("result");
            if (result.Placements == null || result.Placements.Count == 0)
                throw new InvalidOperationException("DXF'e yazılacak yerleşim geometrisi yok.");

            var all = result.Placements.SelectMany(p => p.Contour ?? new List<SmartNestPoint>()).ToList();
            if (all.Count < 3) throw new InvalidOperationException("Yerleşim konturları boş veya geçersiz.");
            double minX = all.Min(p => p.X), minY = all.Min(p => p.Y);
            double maxX = all.Max(p => p.X), maxY = all.Max(p => p.Y);
            double width = Math.Max(1.0, maxX - minX), height = Math.Max(1.0, maxY - minY);
            double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
            double viewHeight = Math.Max(height, width / 1.6) * 1.15;

            var sb = new StringBuilder();
            Pair(sb, "0", "SECTION"); Pair(sb, "2", "HEADER");
            Pair(sb, "9", "$ACADVER"); Pair(sb, "1", "AC1009");
            Pair(sb, "9", "$INSBASE"); Pair(sb, "10", "0"); Pair(sb, "20", "0"); Pair(sb, "30", "0");
            Pair(sb, "9", "$EXTMIN"); Pair(sb, "10", F(minX)); Pair(sb, "20", F(minY)); Pair(sb, "30", "0");
            Pair(sb, "9", "$EXTMAX"); Pair(sb, "10", F(maxX)); Pair(sb, "20", F(maxY)); Pair(sb, "30", "0");
            Pair(sb, "9", "$VIEWCTR"); Pair(sb, "10", F(cx)); Pair(sb, "20", F(cy));
            Pair(sb, "9", "$VIEWSIZE"); Pair(sb, "40", F(viewHeight));
            Pair(sb, "0", "ENDSEC");

            Pair(sb, "0", "SECTION"); Pair(sb, "2", "TABLES");
            Pair(sb, "0", "TABLE"); Pair(sb, "2", "VPORT"); Pair(sb, "70", "1");
            Pair(sb, "0", "VPORT"); Pair(sb, "2", "*ACTIVE"); Pair(sb, "70", "0");
            Pair(sb, "10", "0.0"); Pair(sb, "20", "0.0"); Pair(sb, "11", "1.0"); Pair(sb, "21", "1.0");
            Pair(sb, "12", F(cx)); Pair(sb, "22", F(cy)); Pair(sb, "13", "0.0"); Pair(sb, "23", "0.0");
            Pair(sb, "14", "10.0"); Pair(sb, "24", "10.0"); Pair(sb, "15", "10.0"); Pair(sb, "25", "10.0");
            Pair(sb, "16", "0.0"); Pair(sb, "26", "0.0"); Pair(sb, "36", "1.0");
            Pair(sb, "17", "0.0"); Pair(sb, "27", "0.0"); Pair(sb, "37", "0.0");
            Pair(sb, "40", F(viewHeight)); Pair(sb, "41", "1.6"); Pair(sb, "42", "50.0");
            Pair(sb, "43", "0.0"); Pair(sb, "44", "0.0"); Pair(sb, "50", "0.0"); Pair(sb, "51", "0.0");
            Pair(sb, "71", "0"); Pair(sb, "72", "100"); Pair(sb, "73", "1"); Pair(sb, "74", "3");
            Pair(sb, "75", "0"); Pair(sb, "76", "0"); Pair(sb, "77", "0"); Pair(sb, "78", "0");
            Pair(sb, "0", "ENDTAB");
            Pair(sb, "0", "TABLE"); Pair(sb, "2", "LAYER"); Pair(sb, "70", "1");
            Pair(sb, "0", "LAYER"); Pair(sb, "2", "0"); Pair(sb, "70", "0"); Pair(sb, "62", "7"); Pair(sb, "6", "CONTINUOUS");
            Pair(sb, "0", "ENDTAB"); Pair(sb, "0", "ENDSEC");

            Pair(sb, "0", "SECTION"); Pair(sb, "2", "ENTITIES");
            int entityCount = 0;
            foreach (var p in result.Placements)
            {
                if (p.Contour == null || p.Contour.Count < 3)
                    throw new InvalidDataException("Geçersiz yerleşim konturu: " + (p.PartName ?? "Parça"));
                Pair(sb, "0", "POLYLINE"); Pair(sb, "8", "0"); Pair(sb, "66", "1"); Pair(sb, "70", "1");
                foreach (var v in p.Contour)
                {
                    Pair(sb, "0", "VERTEX"); Pair(sb, "8", "0");
                    Pair(sb, "10", F(v.X)); Pair(sb, "20", F(v.Y)); Pair(sb, "30", "0");
                    Pair(sb, "70", "0");
                }
                Pair(sb, "0", "SEQEND"); Pair(sb, "8", "0");
                entityCount++;
            }
            if (entityCount == 0) throw new InvalidOperationException("DXF ENTITIES bölümüne geometri yazılmadı.");
            Pair(sb, "0", "ENDSEC"); Pair(sb, "0", "EOF");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = path + ".src_tmp";
            try
            {
                File.WriteAllText(temp, sb.ToString(), Encoding.ASCII);
                string check = File.ReadAllText(temp, Encoding.ASCII);
                if (!check.Contains("AC1009") || !check.Contains("ENTITIES") ||
                    !check.Contains("POLYLINE") || !check.Contains("VERTEX") || !check.Contains("EOF"))
                    throw new InvalidDataException("DXF doğrulaması başarısız: R12 model geometrisi eksik.");
                if (new FileInfo(temp).Length < 128)
                    throw new InvalidDataException("DXF doğrulaması başarısız: çıktı dosyası boş.");
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static void Validate(SmartNestSettings s)
        {
            if (s == null || s.SheetWidth <= 0 || s.SheetHeight <= 0 || s.PartGap < 0 || s.EdgeMargin < 0 || s.Kerf < 0) throw new ArgumentException("Sac ölçüleri, aralık, kenar payı ve kerf geçerli olmalı.");
            if (2 * s.EdgeMargin >= Math.Min(s.SheetWidth, s.SheetHeight)) throw new ArgumentException("Kenar payı sac ölçüsüne göre çok büyük.");
        }
        private static void ValidateContour(string name, IList<SmartNestPoint> p)
        {
            if (p == null || p.Count < 3 || Math.Abs(SignedArea(p)) < 1e-8) throw new ArgumentException("Kapalı ve geçerli dış kontur gerekli: " + (name ?? "isimsiz parça"));
            if (p.Any(q => q == null || double.IsNaN(q.X) || double.IsNaN(q.Y) || double.IsInfinity(q.X) || double.IsInfinity(q.Y))) throw new ArgumentException("Kontur koordinatları geçersiz: " + name);
        }
        private static double SignedArea(IList<SmartNestPoint> p) { double a=0; for(int i=0;i<p.Count;i++){var q=p[i];var n=p[(i+1)%p.Count];a+=q.X*n.Y-n.X*q.Y;} return a/2.0; }
        private static IList<SmartNestPoint> Rotate(IList<SmartNestPoint> p,double deg) { double a=deg*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a); return p.Select(q=>new SmartNestPoint(q.X*c-q.Y*s,q.X*s+q.Y*c)).ToList(); }
        private static IList<SmartNestPoint> Translate(IList<SmartNestPoint> p,double x,double y) { return p.Select(q=>new SmartNestPoint(q.X+x,q.Y+y)).ToList(); }
        private static IList<SmartNestPoint> ExpandRadially(IList<SmartNestPoint> p,double d) { if(d<=0)return p; double cx=p.Average(q=>q.X),cy=p.Average(q=>q.Y); return p.Select(q=>{double dx=q.X-cx,dy=q.Y-cy,l=Math.Sqrt(dx*dx+dy*dy);return new SmartNestPoint(q.X+(l<1e-9?0:dx/l*d),q.Y+(l<1e-9?0:dy/l*d));}).ToList(); }
        private static bool PolygonsIntersect(IList<SmartNestPoint> a,IList<SmartNestPoint> b) { return !Separated(a,b) && !Separated(b,a); }
        private static bool Separated(IList<SmartNestPoint> a,IList<SmartNestPoint> b) { for(int i=0;i<a.Count;i++){var p=a[i];var q=a[(i+1)%a.Count];double ax=-(q.Y-p.Y),ay=q.X-p.X;double amin=double.MaxValue,amax=double.MinValue,bmin=double.MaxValue,bmax=double.MinValue;foreach(var v in a){double z=v.X*ax+v.Y*ay;amin=Math.Min(amin,z);amax=Math.Max(amax,z);}foreach(var v in b){double z=v.X*ax+v.Y*ay;bmin=Math.Min(bmin,z);bmax=Math.Max(bmax,z);}if(amax<=bmin+1e-8||bmax<=amin+1e-8)return true;}return false; }
        private static void Pair(StringBuilder b,string code,string value){b.Append(code).Append("\r\n").Append(value).Append("\r\n");}
        private static string F(double d){return d.ToString("0.########",CultureInfo.InvariantCulture);}
    }
}
