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
        public string SourceDxfPath { get; set; }
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
        public string SourceDxfPath { get; set; }
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
        public double SheetWidth { get; set; }
        public double SheetHeight { get; set; }
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
            result.SheetWidth = settings.SheetWidth;
            result.SheetHeight = settings.SheetHeight;
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
                result.Placements.Add(new SmartNestPlacement { PartName = part.Name ?? "Parça", SourceDxfPath = part.SourceDxfPath, SheetIndex = bestSheet, X = bestX, Y = bestY, RotationDegrees = best.A, Contour = Translate(best.P, bestX, bestY) });
                result.UsedPartArea += Math.Abs(SignedArea(part.OuterContour));
                if (progress != null) progress(partIndex + 1, totalParts, candidateChecks);
            }
            result.UtilizationPercent = result.TotalSheetArea * result.SheetCount <= 0 ? 0 : 100.0 * result.UsedPartArea / (result.TotalSheetArea * result.SheetCount);
            return result;
        }

        private sealed class Bounds
        {
            public double MinX, MinY, MaxX, MaxY;
        }

        private static void FindBest(SmartNestPart part, SmartNestSettings cfg, IList<SmartNestPlacement> placed, int sheet,
            ref Candidate best, ref int bestSheet, ref double bestX, ref double bestY,
            ref long candidateChecks, Action<int, int, long> progress, int partIndex, int totalParts)
        {
            // Candidate-based bottom-left search: test meaningful alignments against already
            // placed part bounds instead of scanning every 10-20 mm point on the whole sheet.
            double clearance = cfg.PartGap + cfg.Kerf;
            var expandedPlaced = new List<IList<SmartNestPoint>>(placed.Count);
            var placedBounds = new List<Bounds>(placed.Count);
            foreach (var old in placed)
            {
                var expanded = ExpandRadially(old.Contour, clearance / 2.0);
                expandedPlaced.Add(expanded);
                placedBounds.Add(GetBounds(expanded));
            }

            double step = part.AllowRotation ? Math.Max(1, Math.Min(90, part.RotationStepDegrees)) : 360;
            int rotations = part.AllowRotation ? Math.Max(1, (int)Math.Ceiling(360.0 / step)) : 1;
            for (int r = 0; r < rotations; r++)
            {
                double angle = part.AllowRotation ? r * step : 0;
                var rotated = Rotate(part.OuterContour, angle);
                double minX = rotated.Min(q => q.X), minY = rotated.Min(q => q.Y);
                var normalized = rotated.Select(q => new SmartNestPoint(q.X - minX, q.Y - minY)).ToList();
                double w = normalized.Max(q => q.X), h = normalized.Max(q => q.Y);
                double maxX = cfg.SheetWidth - cfg.EdgeMargin - w;
                double maxY = cfg.SheetHeight - cfg.EdgeMargin - h;
                if (maxX < cfg.EdgeMargin - 1e-7 || maxY < cfg.EdgeMargin - 1e-7) continue;

                var candidateXs = new SortedSet<double> { cfg.EdgeMargin };
                var candidateYs = new SortedSet<double> { cfg.EdgeMargin };
                foreach (var b in placedBounds)
                {
                    AddCandidate(candidateXs, b.MinX, cfg.EdgeMargin, maxX);
                    AddCandidate(candidateXs, b.MaxX + clearance, cfg.EdgeMargin, maxX);
                    AddCandidate(candidateXs, b.MinX - w - clearance, cfg.EdgeMargin, maxX);
                    AddCandidate(candidateXs, b.MaxX - w, cfg.EdgeMargin, maxX);
                    AddCandidate(candidateYs, b.MinY, cfg.EdgeMargin, maxY);
                    AddCandidate(candidateYs, b.MaxY + clearance, cfg.EdgeMargin, maxY);
                    AddCandidate(candidateYs, b.MinY - h - clearance, cfg.EdgeMargin, maxY);
                    AddCandidate(candidateYs, b.MaxY - h, cfg.EdgeMargin, maxY);
                }

                var expandedNormalized = ExpandRadially(normalized, clearance / 2.0);
                var expandedCandidateBounds = GetBounds(expandedNormalized);
                bool foundForRotation = false;
                foreach (double y in candidateYs)
                {
                    foreach (double x in candidateXs)
                    {
                        candidateChecks++;
                        var candidateBounds = new Bounds
                        {
                            MinX = x + expandedCandidateBounds.MinX,
                            MinY = y + expandedCandidateBounds.MinY,
                            MaxX = x + expandedCandidateBounds.MaxX,
                            MaxY = y + expandedCandidateBounds.MaxY
                        };
                        bool hit = false;
                        for (int i = 0; i < expandedPlaced.Count; i++)
                        {
                            if (BoundsOverlap(candidateBounds, placedBounds[i]) &&
                                PolygonsIntersect(Translate(expandedNormalized, x, y), expandedPlaced[i]))
                            {
                                hit = true;
                                break;
                            }
                        }
                        if (progress != null && candidateChecks % 1000 == 0)
                            progress(partIndex, totalParts, candidateChecks);
                        if (hit) continue;

                        if (best == null || y < bestY - 1e-7 ||
                            (Math.Abs(y - bestY) <= 1e-7 && x < bestX - 1e-7) ||
                            (Math.Abs(y - bestY) <= 1e-7 && Math.Abs(x - bestX) <= 1e-7 &&
                             (best == null || h < best.H - 1e-7 ||
                              (Math.Abs(h - best.H) <= 1e-7 && w < best.W - 1e-7))))
                        {
                            best = new Candidate { P = normalized, A = angle, W = w, H = h, MinX = minX, MinY = minY };
                            bestSheet = sheet;
                            bestX = x;
                            bestY = y;
                        }
                        foundForRotation = true;
                        break;
                    }
                    if (foundForRotation) break;
                }
            }
        }

        private static void AddCandidate(SortedSet<double> values, double value, double min, double max)
        {
            if (value >= min - 1e-7 && value <= max + 1e-7)
                values.Add(Math.Max(min, Math.Min(max, value)));
        }

        private static Bounds GetBounds(IList<SmartNestPoint> polygon)
        {
            return new Bounds
            {
                MinX = polygon.Min(p => p.X), MinY = polygon.Min(p => p.Y),
                MaxX = polygon.Max(p => p.X), MaxY = polygon.Max(p => p.Y)
            };
        }

        private static bool BoundsOverlap(Bounds a, Bounds b)
        {
            return a.MinX < b.MaxX - 1e-7 && a.MaxX > b.MinX + 1e-7 &&
                   a.MinY < b.MaxY - 1e-7 && a.MaxY > b.MinY + 1e-7;
        }

        public static void WriteDxf(string path, SmartNestResult result)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("DXF yolu boş.", "path");
            if (result == null) throw new ArgumentNullException("result");
            // SmartNest uses the same production-grade R12 entity pipeline as Laser Prep.
            new CombinedDxfBuilder().BuildNesting(result, path);
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
