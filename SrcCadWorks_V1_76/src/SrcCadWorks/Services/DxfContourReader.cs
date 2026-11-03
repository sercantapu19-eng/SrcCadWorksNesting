using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SrcCadWorks.Services;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// Reads a usable outer loop from the ENTITIES section of a flat-pattern DXF.
    /// Supports closed LWPOLYLINE/POLYLINE (including bulge arcs), CIRCLE, ELLIPSE,
    /// and closed chains made from LINE/ARC/fit-point SPLINE entities.
    /// </summary>
    internal static class DxfContourReader
    {
        private sealed class Pair { public int Code; public string Value; }
        private sealed class Entity { public string Type; public List<Pair> Data = new List<Pair>(); }
        private sealed class Vertex { public SmartNestPoint Point; public double Bulge; }
        private sealed class Segment { public List<SmartNestPoint> Points; }

        public static IList<SmartNestPoint> ReadLargestClosedContour(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("DXF dosyası bulunamadı.", path);

            var lines = File.ReadAllLines(path);
            if (lines.Length < 4) throw new InvalidDataException("DXF dosyası boş veya geçersiz.");
            var pairs = ParsePairs(lines);
            var entities = ReadEntities(pairs);
            if (entities.Count == 0)
                throw new InvalidDataException("DXF ENTITIES bölümü bulunamadı veya boş.");

            var closed = new List<IList<SmartNestPoint>>();
            var openSegments = new List<Segment>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                Increment(counts, e.Type);
                if (e.Type == "LWPOLYLINE")
                {
                    bool isClosed;
                    var vertices = ReadVertices(e.Data, true, out isClosed);
                    if (!isClosed && vertices.Count >= 3 && Distance(vertices[0].Point, vertices[vertices.Count - 1].Point) <= 0.02)
                        isClosed = true;
                    if (isClosed && vertices.Count >= 3)
                        AddValid(closed, BuildPolyline(vertices, true));
                }
                else if (e.Type == "POLYLINE")
                {
                    bool isClosed;
                    int flags = IntValue(e.Data, 70, 0);
                    isClosed = (flags & 1) != 0;
                    var vertices = new List<Vertex>();
                    int j = i + 1;
                    while (j < entities.Count && entities[j].Type == "VERTEX")
                    {
                        Increment(counts, "VERTEX");
                        bool unused;
                        var parsed = ReadVertices(entities[j].Data, false, out unused);
                        if (parsed.Count > 0) vertices.Add(parsed[0]);
                        j++;
                    }
                    if (j < entities.Count && entities[j].Type == "SEQEND") { Increment(counts, "SEQEND"); i = j; }
                    else if (j > i + 1) i = j - 1;
                    if (!isClosed && vertices.Count >= 3 && Distance(vertices[0].Point, vertices[vertices.Count - 1].Point) <= 0.02)
                        isClosed = true;
                    if (isClosed && vertices.Count >= 3) AddValid(closed, BuildPolyline(vertices, true));
                }
                else if (e.Type == "CIRCLE")
                {
                    double cx = DoubleValue(e.Data, 10, double.NaN);
                    double cy = DoubleValue(e.Data, 20, double.NaN);
                    double r = DoubleValue(e.Data, 40, double.NaN);
                    if (IsFinite(cx) && IsFinite(cy) && IsFinite(r) && r > 0)
                        AddValid(closed, SampleCircle(cx, cy, r, 72));
                }
                else if (e.Type == "LINE")
                {
                    var a = new SmartNestPoint(DoubleValue(e.Data, 10, double.NaN), DoubleValue(e.Data, 20, double.NaN));
                    var b = new SmartNestPoint(DoubleValue(e.Data, 11, double.NaN), DoubleValue(e.Data, 21, double.NaN));
                    if (IsPoint(a) && IsPoint(b) && Distance(a, b) > 1e-9)
                        openSegments.Add(new Segment { Points = new List<SmartNestPoint> { a, b } });
                }
                else if (e.Type == "ARC")
                {
                    var arc = ReadArc(e.Data);
                    if (arc != null) openSegments.Add(new Segment { Points = arc });
                }
                else if (e.Type == "ELLIPSE")
                {
                    bool isClosed;
                    var ellipse = ReadEllipse(e.Data, out isClosed);
                    if (ellipse != null && ellipse.Count >= 3)
                    {
                        if (isClosed) AddValid(closed, ellipse);
                        else openSegments.Add(new Segment { Points = ellipse });
                    }
                }
                else if (e.Type == "SPLINE")
                {
                    bool isClosed;
                    var spline = ReadSplineFitPoints(e.Data, out isClosed);
                    if (spline != null && spline.Count >= 2)
                    {
                        if (isClosed && spline.Count >= 3) AddValid(closed, spline);
                        else openSegments.Add(new Segment { Points = spline });
                    }
                }
            }

            if (openSegments.Count > 0)
            {
                double tolerance = EstimateJoinTolerance(openSegments);
                foreach (var loop in ChainClosedLoops(openSegments, tolerance)) AddValid(closed, loop);
            }

            var valid = closed
                .Where(p => p != null && p.Count >= 3 && Math.Abs(PolygonArea(p)) > 1e-8)
                .OrderByDescending(p => Math.Abs(PolygonArea(p)))
                .ToList();
            if (valid.Count == 0)
            {
                string summary = string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(10)
                    .Select(kv => kv.Key + "=" + kv.Value));
                throw new InvalidDataException("DXF içinde kapalı dış kontur oluşturulamadı. Okunan entity türleri: " +
                    (string.IsNullOrWhiteSpace(summary) ? "yok" : summary) +
                    ". Desteklenenler: kapalı POLYLINE/LWPOLYLINE, CIRCLE ve uçları birleşen LINE/ARC/ELLIPSE/SPLINE-fit zincirleri.");
            }
            return valid[0];
        }

        private static List<Pair> ParsePairs(string[] lines)
        {
            var result = new List<Pair>();
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                int code;
                if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) continue;
                result.Add(new Pair { Code = code, Value = lines[i + 1].Trim() });
            }
            return result;
        }

        private static List<Entity> ReadEntities(List<Pair> pairs)
        {
            int section = -1;
            for (int i = 0; i + 1 < pairs.Count; i++)
            {
                if (pairs[i].Code == 0 && pairs[i].Value.Equals("SECTION", StringComparison.OrdinalIgnoreCase) &&
                    pairs[i + 1].Code == 2 && pairs[i + 1].Value.Equals("ENTITIES", StringComparison.OrdinalIgnoreCase))
                { section = i + 2; break; }
            }
            var result = new List<Entity>();
            if (section < 0) return result;
            Entity current = null;
            for (int i = section; i < pairs.Count; i++)
            {
                var p = pairs[i];
                if (p.Code == 0)
                {
                    if (current != null) result.Add(current);
                    if (p.Value.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase)) break;
                    current = new Entity { Type = p.Value.ToUpperInvariant() };
                }
                else if (current != null) current.Data.Add(p);
            }
            if (current != null && !current.Type.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase)) result.Add(current);
            return result;
        }

        private static List<Vertex> ReadVertices(List<Pair> data, bool lightweight, out bool closed)
        {
            closed = lightweight && (IntValue(data, 70, 0) & 1) != 0;
            var vertices = new List<Vertex>();
            Vertex current = null;
            foreach (var p in data)
            {
                if (p.Code == 10)
                {
                    double x;
                    if (!TryDouble(p.Value, out x)) continue;
                    current = new Vertex { Point = new SmartNestPoint(x, double.NaN), Bulge = 0 };
                    vertices.Add(current);
                }
                else if (p.Code == 20 && current != null)
                {
                    double y;
                    if (TryDouble(p.Value, out y)) current.Point.Y = y;
                }
                else if (p.Code == 42 && current != null)
                {
                    double b;
                    if (TryDouble(p.Value, out b)) current.Bulge = b;
                }
            }
            return vertices.Where(v => IsPoint(v.Point)).ToList();
        }

        private static List<SmartNestPoint> BuildPolyline(IList<Vertex> vertices, bool closed)
        {
            var result = new List<SmartNestPoint>();
            if (vertices == null || vertices.Count < 2) return result;
            int segments = closed ? vertices.Count : vertices.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                if (result.Count == 0) result.Add(Clone(a.Point));
                AppendBulgeSegment(result, a.Point, b.Point, a.Bulge);
            }
            if (closed && result.Count > 1 && Distance(result[0], result[result.Count - 1]) < 1e-7)
                result.RemoveAt(result.Count - 1);
            return result;
        }

        private static void AppendBulgeSegment(List<SmartNestPoint> output, SmartNestPoint a, SmartNestPoint b, double bulge)
        {
            if (Math.Abs(bulge) < 1e-10)
            {
                output.Add(Clone(b));
                return;
            }
            double chord = Distance(a, b);
            if (chord < 1e-10) return;
            double sweep = 4.0 * Math.Atan(bulge);
            double offset = chord * (1.0 - bulge * bulge) / (4.0 * bulge);
            double midX = (a.X + b.X) / 2.0, midY = (a.Y + b.Y) / 2.0;
            double leftX = -(b.Y - a.Y) / chord, leftY = (b.X - a.X) / chord;
            double cx = midX + leftX * offset, cy = midY + leftY * offset;
            double radius = Distance(a, new SmartNestPoint(cx, cy));
            double start = Math.Atan2(a.Y - cy, a.X - cx);
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 36.0)));
            for (int k = 1; k <= steps; k++)
            {
                if (k == steps) { output.Add(Clone(b)); continue; }
                double angle = start + sweep * k / steps;
                output.Add(new SmartNestPoint(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
        }

        private static List<SmartNestPoint> ReadArc(List<Pair> data)
        {
            double cx = DoubleValue(data, 10, double.NaN), cy = DoubleValue(data, 20, double.NaN);
            double r = DoubleValue(data, 40, double.NaN);
            double a0 = DoubleValue(data, 50, double.NaN), a1 = DoubleValue(data, 51, double.NaN);
            if (!IsFinite(cx) || !IsFinite(cy) || !IsFinite(r) || r <= 0 || !IsFinite(a0) || !IsFinite(a1)) return null;
            double sweep = a1 - a0;
            while (sweep <= 0) sweep += 360.0;
            int steps = Math.Max(2, (int)Math.Ceiling(sweep / 5.0));
            var points = new List<SmartNestPoint>();
            for (int i = 0; i <= steps; i++)
            {
                double angle = (a0 + sweep * i / steps) * Math.PI / 180.0;
                points.Add(new SmartNestPoint(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
            }
            return points;
        }

        private static List<SmartNestPoint> SampleCircle(double cx, double cy, double radius, int count)
        {
            var points = new List<SmartNestPoint>();
            for (int i = 0; i < count; i++)
            {
                double a = 2.0 * Math.PI * i / count;
                points.Add(new SmartNestPoint(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
            }
            return points;
        }

        private static List<SmartNestPoint> ReadEllipse(List<Pair> data, out bool closed)
        {
            double cx = DoubleValue(data, 10, double.NaN), cy = DoubleValue(data, 20, double.NaN);
            double mx = DoubleValue(data, 11, double.NaN), my = DoubleValue(data, 21, double.NaN);
            double ratio = DoubleValue(data, 40, double.NaN);
            double start = DoubleValue(data, 41, 0), end = DoubleValue(data, 42, 2.0 * Math.PI);
            closed = Math.Abs(Math.Abs(end - start) - 2.0 * Math.PI) < 1e-4 || Math.Abs(end - start) < 1e-4;
            if (!IsFinite(cx) || !IsFinite(cy) || !IsFinite(mx) || !IsFinite(my) || !IsFinite(ratio) || ratio <= 0) return null;
            double major = Math.Sqrt(mx * mx + my * my);
            if (major < 1e-10) return null;
            double minor = major * ratio;
            int steps = Math.Max(12, (int)Math.Ceiling(Math.Abs(end - start) / (Math.PI / 36.0)));
            var points = new List<SmartNestPoint>();
            for (int i = 0; i <= steps; i++)
            {
                double t = start + (end - start) * i / steps;
                double x = major * Math.Cos(t), y = minor * Math.Sin(t);
                points.Add(new SmartNestPoint(cx + x * mx / major - y * my / major,
                    cy + x * my / major + y * mx / major));
            }
            return points;
        }

        private static List<SmartNestPoint> ReadSplineFitPoints(List<Pair> data, out bool closed)
        {
            closed = (IntValue(data, 70, 0) & 1) != 0;
            var fit = new List<SmartNestPoint>();
            double? x = null;
            foreach (var p in data)
            {
                if (p.Code == 11)
                {
                    double v; if (TryDouble(p.Value, out v)) x = v;
                }
                else if (p.Code == 21 && x.HasValue)
                {
                    double y; if (TryDouble(p.Value, out y)) { fit.Add(new SmartNestPoint(x.Value, y)); x = null; }
                }
            }
            if (fit.Count < 2) return null;
            if (closed && Distance(fit[0], fit[fit.Count - 1]) < 1e-7) fit.RemoveAt(fit.Count - 1);
            return fit;
        }

        private static List<IList<SmartNestPoint>> ChainClosedLoops(List<Segment> segments, double tolerance)
        {
            var loops = new List<IList<SmartNestPoint>>();
            var used = new bool[segments.Count];
            for (int seed = 0; seed < segments.Count; seed++)
            {
                if (used[seed] || segments[seed].Points == null || segments[seed].Points.Count < 2) continue;
                var chain = segments[seed].Points.Select(Clone).ToList();
                used[seed] = true;
                for (int guard = 0; guard < segments.Count + 2; guard++)
                {
                    if (Distance(chain[0], chain[chain.Count - 1]) <= tolerance) break;
                    int best = -1; bool reverse = false; double bestDistance = double.MaxValue;
                    var tail = chain[chain.Count - 1];
                    for (int j = 0; j < segments.Count; j++)
                    {
                        if (used[j]) continue;
                        var p = segments[j].Points;
                        double d0 = Distance(tail, p[0]);
                        double d1 = Distance(tail, p[p.Count - 1]);
                        if (d0 < bestDistance && d0 <= tolerance) { best = j; reverse = false; bestDistance = d0; }
                        if (d1 < bestDistance && d1 <= tolerance) { best = j; reverse = true; bestDistance = d1; }
                    }
                    if (best < 0) break;
                    used[best] = true;
                    var next = segments[best].Points;
                    if (reverse)
                    {
                        for (int k = next.Count - 2; k >= 0; k--) chain.Add(Clone(next[k]));
                    }
                    else
                    {
                        for (int k = 1; k < next.Count; k++) chain.Add(Clone(next[k]));
                    }
                }
                if (chain.Count >= 4 && Distance(chain[0], chain[chain.Count - 1]) <= tolerance)
                {
                    chain.RemoveAt(chain.Count - 1);
                    loops.Add(chain);
                }
            }
            return loops;
        }

        private static double EstimateJoinTolerance(List<Segment> segments)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in segments)
            {
                if (s.Points == null) continue;
                foreach (var p in s.Points)
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
            }
            double span = minX == double.MaxValue ? 0 : Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            return Math.Max(0.02, span * 1e-8);
        }

        private static void AddValid(List<IList<SmartNestPoint>> target, IList<SmartNestPoint> points)
        {
            if (points == null) return;
            var compact = new List<SmartNestPoint>();
            foreach (var p in points)
                if (IsPoint(p) && (compact.Count == 0 || Distance(compact[compact.Count - 1], p) > 1e-8)) compact.Add(Clone(p));
            if (compact.Count > 1 && Distance(compact[0], compact[compact.Count - 1]) < 1e-7) compact.RemoveAt(compact.Count - 1);
            if (compact.Count >= 3 && Math.Abs(PolygonArea(compact)) > 1e-8) target.Add(compact);
        }

        private static double DoubleValue(List<Pair> data, int code, double fallback)
        {
            foreach (var p in data) if (p.Code == code) { double v; if (TryDouble(p.Value, out v)) return v; }
            return fallback;
        }
        private static int IntValue(List<Pair> data, int code, int fallback)
        {
            foreach (var p in data) if (p.Code == code) { int v; if (int.TryParse(p.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v; }
            return fallback;
        }
        private static bool TryDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool IsPoint(SmartNestPoint p) { return p != null && IsFinite(p.X) && IsFinite(p.Y); }
        private static SmartNestPoint Clone(SmartNestPoint p) { return new SmartNestPoint(p.X, p.Y); }
        private static double Distance(SmartNestPoint a, SmartNestPoint b)
        { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
        private static double PolygonArea(IList<SmartNestPoint> p)
        { double a = 0; for (int i = 0; i < p.Count; i++) { var q = p[i]; var n = p[(i + 1) % p.Count]; a += q.X * n.Y - n.X * q.Y; } return a / 2.0; }
        private static void Increment(Dictionary<string, int> dict, string key)
        { if (!dict.ContainsKey(key)) dict[key] = 0; dict[key]++; }
    }
}
