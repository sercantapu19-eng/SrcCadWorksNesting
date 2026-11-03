using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// Builds one production-friendly combined DXF from the individual SOLIDWORKS flat-pattern DXF files.
    /// The output is deliberately AutoCAD R12 ASCII: no handles, owners, subclass markers, Paper Space
    /// references or SOLIDWORKS-specific linetype/table metadata are carried across. This makes the file
    /// much more tolerant for AutoCAD/DraftSight and, critically, for SOLIDWORKS LoadFile4 -> DWG conversion.
    /// </summary>
    internal sealed class CombinedDxfBuilder
    {
        private sealed class DxfPair
        {
            public int Code;
            public string Value;
        }

        private sealed class DxfEntity
        {
            public string Type;
            public List<DxfPair> Pairs = new List<DxfPair>();
        }

        private sealed class SourceGeometry
        {
            public LaserPartRow Row;
            public List<DxfEntity> Entities;
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public double Width => Math.Max(1.0, MaxX - MinX);
            public double Height => Math.Max(1.0, MaxY - MinY);
            public double LayoutWidth => Math.Max(Width, CombinedDxfBuilder.EstimateInfoWidth(Row));
        }

        private sealed class Placement
        {
            public SourceGeometry Geometry;
            public double X;
            public double Y;
        }

        private sealed class ThicknessSection
        {
            public string Title;
            public double HeaderY;
            public double SeparatorY;
            public double RightX;
        }

        private sealed class PolyVertex
        {
            public double X;
            public double Y;
            public double Z;
            public double? StartWidth;
            public double? EndWidth;
            public double? Bulge;
        }

        private sealed class DxfPoint
        {
            public double X;
            public double Y;
            public double Z;
        }

        private sealed class HomogeneousPoint
        {
            public double X;
            public double Y;
            public double Z;
            public double W;
        }

        private static readonly HashSet<string> R12EntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LINE", "ARC", "CIRCLE", "POINT", "TEXT", "POLYLINE", "VERTEX", "SEQEND", "3DFACE", "SOLID", "TRACE", "LWPOLYLINE"
        };

        private static readonly HashSet<string> OutputLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SRC_GEOMETRY", "SRC_BEND", "SRC_CENTER", "SRC_INFO", "SRC_SEPARATOR"
        };

        private static readonly HashSet<string> OutputLinetypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CONTINUOUS", "DASHED", "CENTER"
        };

        private const double LeftMargin = 25.0;
        private const double TopMargin = 25.0;
        private const double VerticalGap = 28.0;
        private const double InfoBandHeight = 24.0;
        private const double InfoLineStep = 5.0;
        private const double InfoTextHeight = 3.4;
        private const double GroupTextHeight = 8.0;
        private const double GroupHeaderBand = 30.0;
        private const double GroupGap = 24.0;
        // SPLINE is not an AutoCAD R12 entity. Source SOLIDWORKS splines are evaluated
        // as their real NURBS curve and tessellated to an R12 POLYLINE. 0.02 mm chord
        // tolerance keeps laser geometry visually/production-wise faithful while avoiding
        // unsupported SPLINE entities in the combined interchange file.
        private const double SplineChordTolerance = 0.02;
        private const int SplineMaxDepth = 14;

        public void Build(IList<LaserPartRow> rows, Func<LaserPartRow, string> dxfPathResolver, string outputPath)
        {
            if (rows == null || rows.Count == 0) throw new InvalidOperationException("Toplu DXF için parça bulunamadı.");
            if (dxfPathResolver == null) throw new ArgumentNullException(nameof(dxfPathResolver));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Toplu DXF çıktı yolu boş.", nameof(outputPath));

            var geometries = new List<SourceGeometry>();
            foreach (var row in rows.Where(r => r != null).OrderBy(r => r.PartCode, StringComparer.OrdinalIgnoreCase))
            {
                string path = dxfPathResolver(row);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("Toplu DXF kaynağı bulunamadı: " + (row.PartCode ?? ""), path);

                geometries.Add(ReadGeometry(path, row));
            }

            if (geometries.Count == 0)
                throw new InvalidOperationException("Toplu DXF için okunabilir parça geometrisi bulunamadı.");

            List<ThicknessSection> sections;
            var placements = BuildPlacements(geometries, out sections);
            double minX = 0.0;
            double minY = sections.Min(section => section.SeparatorY) - TopMargin;
            double maxX = Math.Max(placements.Max(placement => placement.X + placement.Geometry.LayoutWidth), sections.Max(section => section.RightX)) + 30.0;
            double maxY = sections.Max(section => section.HeaderY) + GroupTextHeight + TopMargin;

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            string tempPath = outputPath + ".src_tmp";
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);

                var sb = new StringBuilder(1024 * 1024);
                AppendHeaderAndTables(sb, minX, minY, maxX, maxY);

                int geometryEntityCount = 0;
                int infoEntityCount = 0;
                foreach (var p in placements)
                {
                    double dx = p.X - p.Geometry.MinX;
                    double dy = p.Y - p.Geometry.MinY;
                    geometryEntityCount += AppendTranslatedEntities(sb, p.Geometry, dx, dy);
                    infoEntityCount += AppendPartInfo(sb, p.Geometry.Row, p.X, p.Y - InfoBandHeight + 3.0);
                }

                if (geometryEntityCount <= 0)
                    throw new InvalidOperationException("Toplu DXF oluşturuldu ancak Model Space'e yazılacak geometri bulunamadı.");

                // Group annotations stay on the existing information layer. They never
                // replace or alter the individual part cutting/bending entities.
                foreach (var section in sections)
                {
                    AppendText(sb, LeftMargin, section.HeaderY, section.Title, GroupTextHeight);
                    AppendGroupSeparator(sb, LeftMargin - 10.0, section.RightX, section.SeparatorY);
                    infoEntityCount++;
                }

                sb.AppendLine("0");
                sb.AppendLine("ENDSEC");
                sb.AppendLine("0");
                sb.AppendLine("EOF");

                Encoding enc;
                try { enc = Encoding.GetEncoding(1254); }
                catch { enc = Encoding.Default; }
                File.WriteAllText(tempPath, sb.ToString(), enc);

                ValidateForImport(tempPath, geometries.Count, geometryEntityCount, infoEntityCount);

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tempPath, outputPath);

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 128)
                    throw new InvalidOperationException("Toplu DXF dosyası oluşturulamadı veya boş kaldı.");

                Logger.Info("Toplu DXF temiz R12 Model Space oluşturuldu. Parça=" + geometries.Count +
                    ", geometri=" + geometryEntityCount + ", bilgi=" + infoEntityCount + ", dosya=" + outputPath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// Writes SMARTNEST placements through the same production R12 parser, entity
        /// normalization, style mapping and active viewport used by the Laser Prep bulk DXF.
        /// Source entities are rotated as entities (ARC angles included), not flattened to polygons.
        /// </summary>
        public void BuildNesting(SmartNestResult result, string outputPath)
        {
            if (result == null || result.Placements == null || result.Placements.Count == 0)
                throw new InvalidOperationException("Nesting yerleşimi boş.");
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("DXF çıktı yolu boş.", nameof(outputPath));

            var allPoints = result.Placements.SelectMany(p => p.Contour ?? new List<SmartNestPoint>()).ToList();
            if (allPoints.Count < 3) throw new InvalidOperationException("Nesting konturları geçersiz.");
            double minX = allPoints.Min(p => p.X), minY = allPoints.Min(p => p.Y);
            double maxX = allPoints.Max(p => p.X), maxY = allPoints.Max(p => p.Y);
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string tempPath = outputPath + ".src_tmp";
            try
            {
                var sb = new StringBuilder(1024 * 1024);
                AppendHeaderAndTables(sb, minX, minY, maxX, maxY, Math.Max(1, result.SheetCount));
                int entityCount = 0;
                foreach (var placement in result.Placements)
                {
                    if (string.IsNullOrWhiteSpace(placement.SourceDxfPath) || !File.Exists(placement.SourceDxfPath))
                        throw new FileNotFoundException("Yerleşen parçanın kaynak DXF'i bulunamadı: " + placement.PartName, placement.SourceDxfPath);

                    var row = new LaserPartRow { PartCode = placement.PartName ?? "PARCA", Quantity = 1 };
                    // Do not normalize orientation here: the nest contour and source entities
                    // must share the exact same original coordinate frame.
                    var geometry = ReadGeometry(placement.SourceDxfPath, row, false);
                    RotateEntities(geometry.Entities, placement.RotationDegrees);
                    double gMinX = double.PositiveInfinity, gMinY = double.PositiveInfinity;
                    double gMaxX = double.NegativeInfinity, gMaxY = double.NegativeInfinity;
                    ScanExtents(geometry.Entities, ref gMinX, ref gMinY, ref gMaxX, ref gMaxY);
                    if (double.IsInfinity(gMinX) || double.IsInfinity(gMinY))
                        throw new InvalidDataException("Kaynak DXF geometrisinin sınırları okunamadı: " + placement.PartName);
                    double dx = placement.X - gMinX;
                    double dy = placement.Y - gMinY;
                    string layerPrefix = "SMARTNEST_SHEET_" + (placement.SheetIndex + 1).ToString(CultureInfo.InvariantCulture);
                    int written = AppendTranslatedEntities(sb, geometry, dx, dy, layerPrefix);
                    if (written <= 0) throw new InvalidDataException("Parça için üretim geometrisi yazılamadı: " + placement.PartName);
                    entityCount += written;
                }
                if (entityCount <= 0) throw new InvalidOperationException("DXF içine geometri yazılmadı.");
                Add(sb, 0, "ENDSEC"); Add(sb, 0, "EOF");
                Encoding enc;
                try { enc = Encoding.GetEncoding(1254); } catch { enc = Encoding.Default; }
                File.WriteAllText(tempPath, sb.ToString(), enc);
                string check = File.ReadAllText(tempPath, enc);
                if (!check.Contains("AC1009") || !check.Contains("ENTITIES") || !check.Contains("SMARTNEST_SHEET_") || !check.Contains("EOF"))
                    throw new InvalidDataException("SMARTNEST DXF R12 doğrulaması başarısız.");
                if (new FileInfo(tempPath).Length < 128) throw new InvalidDataException("SMARTNEST DXF çıktısı boş.");
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tempPath, outputPath);
                Logger.Info("SMARTNEST üretim DXF oluşturuldu. Sac=" + result.SheetCount + ", yerleşim=" + result.Placements.Count + ", entity=" + entityCount);
            }
            finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
        }

        /// <summary>
        /// DWG conversion calls this before LoadFile4 so invalid combined DXF files fail with a precise
        /// SrcCadWorks error instead of a generic SOLIDWORKS Hata=1.
        /// </summary>
        public static void ValidateForImport(string path)
        {
            ValidateForImport(path, 0, 0, 0);
        }

        private static void ValidateForImport(string path, int expectedParts, int expectedGeometry, int expectedInfo)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Toplu DXF ön doğrulama dosyası bulunamadı.", path);

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 128) throw new InvalidOperationException("Toplu DXF ön doğrulama: dosya boş veya çok küçük.");
            string text = DetectText(bytes);
            var lines = NormalizeLines(text);
            if (lines.Count < 20 || lines.Count % 2 != 0)
                throw new InvalidOperationException("Toplu DXF ön doğrulama: DXF grup kodu/değer çiftleri bozuk.");

            bool hasR12 = false;
            bool hasEntities = false;
            bool hasEof = false;
            int geometryCount = 0;
            int infoCount = 0;
            string currentEntity = null;
            bool previousWasSection = false;

            for (int i = 0; i + 1 < lines.Count; i += 2)
            {
                int code;
                if (!int.TryParse(lines[i].Trim(), out code))
                    throw new InvalidOperationException("Toplu DXF ön doğrulama: geçersiz grup kodu: '" + lines[i] + "'.");
                string value = (lines[i + 1] ?? "").Trim();

                if (code == 1 && string.Equals(value, "AC1009", StringComparison.OrdinalIgnoreCase)) hasR12 = true;
                if (previousWasSection && code == 2 && string.Equals(value, "ENTITIES", StringComparison.OrdinalIgnoreCase)) hasEntities = true;
                previousWasSection = false;
                if (code == 0)
                {
                    if (string.Equals(value, "EOF", StringComparison.OrdinalIgnoreCase)) hasEof = true;
                    if (string.Equals(value, "SECTION", StringComparison.OrdinalIgnoreCase)) previousWasSection = true;
                    currentEntity = value.ToUpperInvariant();
                    if (R12EntityTypes.Contains(currentEntity))
                    {
                        if (string.Equals(currentEntity, "TEXT", StringComparison.OrdinalIgnoreCase)) infoCount++;
                        else if (!string.Equals(currentEntity, "VERTEX", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(currentEntity, "SEQEND", StringComparison.OrdinalIgnoreCase)) geometryCount++;
                    }
                }

                // These are exactly the references that caused the previous LoadFile4 Hata=1 path.
                if (code == 5 || code == 105 || code == 100 || code == 102 || (code >= 330 && code <= 369) || code == 410)
                    throw new InvalidOperationException("Toplu DXF ön doğrulama: R12 çıktısında yasak metadata grup kodu bulundu: " + code + ".");

                if (code == 67 && value == "1")
                    throw new InvalidOperationException("Toplu DXF ön doğrulama: Paper Space entity bulundu.");

                if (code == 6 && !OutputLinetypes.Contains(value))
                    throw new InvalidOperationException("Toplu DXF ön doğrulama: tanımsız çizgi tipi bulundu: " + value + ".");

                if (code == 8 && currentEntity != "LAYER" && !string.IsNullOrWhiteSpace(value) && !OutputLayers.Contains(value))
                    throw new InvalidOperationException("Toplu DXF ön doğrulama: beklenmeyen layer bulundu: " + value + ".");
            }

            if (!hasR12) throw new InvalidOperationException("Toplu DXF ön doğrulama: AC1009/R12 başlığı bulunamadı.");
            if (text.IndexOf("$VIEWCTR", StringComparison.OrdinalIgnoreCase) < 0 ||
                text.IndexOf("$VIEWSIZE", StringComparison.OrdinalIgnoreCase) < 0 ||
                text.IndexOf("*ACTIVE", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Toplu DXF ön doğrulama: aktif model görünümü/VPORT eksik.");
            if (!hasEntities) throw new InvalidOperationException("Toplu DXF ön doğrulama: ENTITIES bölümü bulunamadı.");
            if (!hasEof) throw new InvalidOperationException("Toplu DXF ön doğrulama: EOF bulunamadı.");
            if (geometryCount <= 0) throw new InvalidOperationException("Toplu DXF ön doğrulama: geometri entity'si yok.");
            if (expectedGeometry > 0 && geometryCount < expectedGeometry)
                throw new InvalidOperationException("Toplu DXF ön doğrulama: beklenen geometri sayısı korunmadı. Beklenen=" + expectedGeometry + ", bulunan=" + geometryCount + ".");
            if (expectedParts > 0 && expectedInfo > 0 && infoCount < expectedParts * 4)
                throw new InvalidOperationException("Toplu DXF ön doğrulama: parça bilgi satırları eksik. Parça=" + expectedParts + ", TEXT=" + infoCount + ".");
        }

        private static double? ThicknessKey(LaserPartRow row)
        {
            double value = row?.ThicknessMm ?? double.NaN;
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return null;
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private static List<Placement> BuildPlacements(IList<SourceGeometry> geometries, out List<ThicknessSection> sections)
        {
            var result = new List<Placement>(geometries.Count);
            sections = new List<ThicknessSection>();
            double cursorTop = 0.0;
            var groups = geometries.GroupBy(geometry => ThicknessKey(geometry.Row))
                .OrderBy(group => group.Key.HasValue ? 0 : 1).ThenBy(group => group.Key);
            foreach (var group in groups)
            {
                string title = group.Key.HasValue
                    ? group.Key.Value.ToString("0.###", CultureInfo.InvariantCulture) + " MM"
                    : "KALINLIK BILINMIYOR";
                var section = new ThicknessSection
                {
                    Title = title, HeaderY = cursorTop,
                    RightX = LeftMargin + Math.Max(100.0, title.Length * GroupTextHeight * 0.65) + 10.0
                };
                cursorTop -= GroupHeaderBand;
                foreach (var geometry in group.OrderBy(item => item.Row.PartCode, StringComparer.OrdinalIgnoreCase))
                {
                    double y = cursorTop - geometry.Height;
                    result.Add(new Placement { Geometry = geometry, X = LeftMargin, Y = y });
                    section.RightX = Math.Max(section.RightX, LeftMargin + geometry.LayoutWidth + 10.0);
                    cursorTop = y - InfoBandHeight - VerticalGap;
                }
                section.SeparatorY = cursorTop + VerticalGap * 0.5;
                sections.Add(section);
                cursorTop = section.SeparatorY - GroupGap;
            }
            return result;
        }

        private static void AppendGroupSeparator(StringBuilder sb, double left, double right, double y)
        {
            Add(sb, 0, "LINE"); Add(sb, 8, "SRC_SEPARATOR"); Add(sb, 6, "CONTINUOUS"); Add(sb, 62, "7");
            Add(sb, 10, Format(left)); Add(sb, 20, Format(y)); Add(sb, 30, "0.0");
            Add(sb, 11, Format(right)); Add(sb, 21, Format(y)); Add(sb, 31, "0.0");
        }

        private static SourceGeometry ReadGeometry(string path, LaserPartRow row, bool normalizeOrientation = true)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 22)
            {
                string sig = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 22));
                if (sig.IndexOf("AutoCAD Binary DXF", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidOperationException("Toplu DXF yalnız ASCII DXF kaynaklarını birleştirebilir. Parça: " + row.PartCode);
            }

            string text = DetectText(bytes);
            var lines = NormalizeLines(text);
            int entitiesStart = FindEntitiesStart(lines);
            if (entitiesStart < 0) throw new InvalidOperationException("DXF ENTITIES bölümü bulunamadı: " + row.PartCode);
            int entitiesEnd = FindEntitiesEnd(lines, entitiesStart);
            if (entitiesEnd < 0) throw new InvalidOperationException("DXF ENTITIES sonu bulunamadı: " + row.PartCode);

            List<DxfEntity> entities = ParseEntities(lines, entitiesStart, entitiesEnd, row);
            if (entities.Count == 0) throw new InvalidOperationException("DXF içinde entity bulunamadı: " + row.PartCode);

            foreach (var entity in entities)
            {
                if (!R12EntityTypes.Contains(entity.Type))
                    throw new InvalidOperationException("Toplu DXF güvenli R12 birleştirme bu geometri tipini desteklemiyor: " +
                        entity.Type + " | Parça: " + row.PartCode + ". Bireysel DXF korunmuştur; geometri kaybı olmaması için Toplu DXF durduruldu.");
            }

            // Eski/istenen toplu DWG görünümündeki gibi her parçanın ana eksenini
            // yataya çevir. Böylece uzun parçalar dikey/çapraz dağılmaz; tek sütunda
            // okunaklı ve üretim dostu bir görünüm oluşur.
            if (normalizeOrientation) NormalizeHorizontalOrientation(entities);

            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            ScanExtents(entities, ref minX, ref minY, ref maxX, ref maxY);
            if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
                throw new InvalidOperationException("DXF içinde ölçülebilir geometri bulunamadı: " + row.PartCode);

            return new SourceGeometry
            {
                Row = row,
                Entities = entities,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY
            };
        }

        private static List<DxfEntity> ParseEntities(IList<string> lines, int start, int end, LaserPartRow row)
        {
            var result = new List<DxfEntity>();
            DxfEntity current = null;
            for (int i = start; i + 1 < end; i += 2)
            {
                int code;
                if (!int.TryParse(lines[i].Trim(), out code)) continue;
                string value = lines[i + 1] ?? "";

                if (code == 0)
                {
                    if (current != null && !IsPaperSpace(current)) result.Add(current);
                    current = new DxfEntity { Type = value.Trim().ToUpperInvariant() };
                    continue;
                }

                if (current != null) current.Pairs.Add(new DxfPair { Code = code, Value = value });
            }
            if (current != null && !IsPaperSpace(current)) result.Add(current);

            // LWPOLYLINE and SPLINE are valid in newer DXF revisions but not in R12.
            // LWPOLYLINE is converted losslessly. SPLINE is evaluated as NURBS and
            // adaptively tessellated to POLYLINE/VERTEX/SEQEND within 0.02 mm chord error.
            var converted = new List<DxfEntity>();
            foreach (var entity in result)
            {
                if (string.Equals(entity.Type, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase))
                    converted.AddRange(ConvertLwPolyline(entity, row));
                else if (string.Equals(entity.Type, "SPLINE", StringComparison.OrdinalIgnoreCase))
                    converted.AddRange(ConvertSplineToPolyline(entity, row));
                else
                    converted.Add(entity);
            }
            return converted;
        }

        private static IEnumerable<DxfEntity> ConvertLwPolyline(DxfEntity source, LaserPartRow row)
        {
            var vertices = new List<PolyVertex>();
            PolyVertex current = null;
            double elevation = GetDouble(source, 38, 0.0);
            foreach (var pair in source.Pairs)
            {
                if (pair.Code == 10)
                {
                    double x;
                    if (!TryParseDouble(pair.Value, out x)) continue;
                    current = new PolyVertex { X = x, Y = 0.0, Z = elevation };
                    vertices.Add(current);
                }
                else if (current != null && pair.Code == 20)
                {
                    double y; if (TryParseDouble(pair.Value, out y)) current.Y = y;
                }
                else if (current != null && pair.Code == 40)
                {
                    double v; if (TryParseDouble(pair.Value, out v)) current.StartWidth = v;
                }
                else if (current != null && pair.Code == 41)
                {
                    double v; if (TryParseDouble(pair.Value, out v)) current.EndWidth = v;
                }
                else if (current != null && pair.Code == 42)
                {
                    double v; if (TryParseDouble(pair.Value, out v)) current.Bulge = v;
                }
            }

            if (vertices.Count == 0)
                throw new InvalidOperationException("LWPOLYLINE vertex içermez. Parça: " + (row?.PartCode ?? ""));

            int flags = GetInt(source, 70, 0) & 1; // preserve closed flag only
            string sourceLinetype = GetString(source, 6);
            string sourceLayer = GetString(source, 8);

            var poly = new DxfEntity { Type = "POLYLINE" };
            poly.Pairs.Add(new DxfPair { Code = 8, Value = sourceLayer });
            poly.Pairs.Add(new DxfPair { Code = 6, Value = sourceLinetype });
            poly.Pairs.Add(new DxfPair { Code = 66, Value = "1" });
            poly.Pairs.Add(new DxfPair { Code = 10, Value = "0" });
            poly.Pairs.Add(new DxfPair { Code = 20, Value = "0" });
            poly.Pairs.Add(new DxfPair { Code = 30, Value = Format(elevation) });
            poly.Pairs.Add(new DxfPair { Code = 70, Value = flags.ToString(CultureInfo.InvariantCulture) });
            yield return poly;

            foreach (var v in vertices)
            {
                var vertex = new DxfEntity { Type = "VERTEX" };
                vertex.Pairs.Add(new DxfPair { Code = 8, Value = sourceLayer });
                vertex.Pairs.Add(new DxfPair { Code = 6, Value = sourceLinetype });
                vertex.Pairs.Add(new DxfPair { Code = 10, Value = Format(v.X) });
                vertex.Pairs.Add(new DxfPair { Code = 20, Value = Format(v.Y) });
                vertex.Pairs.Add(new DxfPair { Code = 30, Value = Format(v.Z) });
                if (v.StartWidth.HasValue) vertex.Pairs.Add(new DxfPair { Code = 40, Value = Format(v.StartWidth.Value) });
                if (v.EndWidth.HasValue) vertex.Pairs.Add(new DxfPair { Code = 41, Value = Format(v.EndWidth.Value) });
                if (v.Bulge.HasValue) vertex.Pairs.Add(new DxfPair { Code = 42, Value = Format(v.Bulge.Value) });
                yield return vertex;
            }

            var seq = new DxfEntity { Type = "SEQEND" };
            seq.Pairs.Add(new DxfPair { Code = 8, Value = sourceLayer });
            seq.Pairs.Add(new DxfPair { Code = 6, Value = sourceLinetype });
            yield return seq;
        }

        private static IEnumerable<DxfEntity> ConvertSplineToPolyline(DxfEntity source, LaserPartRow row)
        {
            int degree = GetInt(source, 71, 3);
            var knots = source.Pairs
                .Where(p => p.Code == 40)
                .Select(p => ParseDoubleOrNaN(p.Value))
                .Where(v => !double.IsNaN(v))
                .ToList();
            var controls = ReadSplineControlPoints(source);
            var weights = source.Pairs
                .Where(p => p.Code == 41)
                .Select(p => ParseDoubleOrNaN(p.Value))
                .Where(v => !double.IsNaN(v))
                .ToList();

            if (degree < 1 || controls.Count < degree + 1)
                throw new InvalidOperationException("SPLINE derece/kontrol noktası geçersiz. Parça: " + (row?.PartCode ?? ""));

            int n = controls.Count - 1;
            int requiredKnots = n + degree + 2;
            if (knots.Count < requiredKnots)
                throw new InvalidOperationException("SPLINE knot vektörü eksik. Parça: " + (row?.PartCode ?? "") +
                    " | Derece=" + degree + " Kontrol=" + controls.Count + " Knot=" + knots.Count);

            while (weights.Count < controls.Count) weights.Add(1.0);
            if (weights.Count > controls.Count) weights = weights.Take(controls.Count).ToList();
            for (int i = 0; i < weights.Count; i++)
                if (Math.Abs(weights[i]) < 1e-12) weights[i] = 1.0;

            double domainStart = knots[degree];
            double domainEnd = knots[n + 1];
            if (!(domainEnd > domainStart))
                throw new InvalidOperationException("SPLINE parametre aralığı geçersiz. Parça: " + (row?.PartCode ?? ""));

            var sampled = new List<DxfPoint>();
            for (int span = degree; span <= n; span++)
            {
                double a = knots[span];
                double b = knots[span + 1];
                if (b - a <= 1e-12) continue;

                // Four seed intervals per knot span avoid missing S-shaped/inflection curves
                // where a single midpoint can lie very close to the end-point chord.
                const int seeds = 4;
                for (int seed = 0; seed < seeds; seed++)
                {
                    double u0 = a + (b - a) * seed / seeds;
                    double u1 = a + (b - a) * (seed + 1) / seeds;
                    var p0 = EvaluateNurbs(controls, weights, knots, degree, u0);
                    var p1 = EvaluateNurbs(controls, weights, knots, degree, u1);
                    if (sampled.Count == 0 || Distance(sampled[sampled.Count - 1], p0) > 1e-8)
                        sampled.Add(p0);
                    AppendAdaptiveSplinePoints(sampled, controls, weights, knots, degree, u0, u1, p0, p1, 0);
                }
            }

            if (sampled.Count < 2)
                throw new InvalidOperationException("SPLINE örneklenemedi. Parça: " + (row?.PartCode ?? ""));

            int flags = GetInt(source, 70, 0);
            bool closed = (flags & 1) != 0 || (flags & 2) != 0;
            if (closed && sampled.Count > 2 && Distance(sampled[0], sampled[sampled.Count - 1]) < 1e-7)
                sampled.RemoveAt(sampled.Count - 1);

            string sourceLinetype = GetString(source, 6);
            string sourceLayer = GetString(source, 8);

            var poly = new DxfEntity { Type = "POLYLINE" };
            poly.Pairs.Add(new DxfPair { Code = 8, Value = sourceLayer });
            poly.Pairs.Add(new DxfPair { Code = 6, Value = sourceLinetype });
            poly.Pairs.Add(new DxfPair { Code = 66, Value = "1" });
            poly.Pairs.Add(new DxfPair { Code = 10, Value = "0" });
            poly.Pairs.Add(new DxfPair { Code = 20, Value = "0" });
            poly.Pairs.Add(new DxfPair { Code = 30, Value = "0" });
            poly.Pairs.Add(new DxfPair { Code = 70, Value = (closed ? 1 : 0).ToString(CultureInfo.InvariantCulture) });
            yield return poly;

            foreach (var point in sampled)
            {
                var vertex = new DxfEntity { Type = "VERTEX" };
                vertex.Pairs.Add(new DxfPair { Code = 8, Value = sourceLayer });
                vertex.Pairs.Add(new DxfPair { Code = 6, Value = sourceLinetype });
                vertex.Pairs.Add(new DxfPair { Code = 10, Value = Format(point.X) });
                vertex.Pairs.Add(new DxfPair { Code = 20, Value = Format(point.Y) });
                vertex.Pairs.Add(new DxfPair { Code = 30, Value = Format(point.Z) });
                yield return vertex;
            }

            var seq = new DxfEntity { Type = "SEQEND" };
            seq.Pairs.Add(new DxfPair { Code = 8, Value = sourceLayer });
            seq.Pairs.Add(new DxfPair { Code = 6, Value = sourceLinetype });
            yield return seq;
        }

        private static List<DxfPoint> ReadSplineControlPoints(DxfEntity source)
        {
            var result = new List<DxfPoint>();
            DxfPoint current = null;
            foreach (var pair in source.Pairs)
            {
                if (pair.Code == 10)
                {
                    double x;
                    if (!TryParseDouble(pair.Value, out x)) continue;
                    current = new DxfPoint { X = x, Y = 0.0, Z = 0.0 };
                    result.Add(current);
                }
                else if (current != null && pair.Code == 20)
                {
                    double y; if (TryParseDouble(pair.Value, out y)) current.Y = y;
                }
                else if (current != null && pair.Code == 30)
                {
                    double z; if (TryParseDouble(pair.Value, out z)) current.Z = z;
                }
            }
            return result;
        }

        private static DxfPoint EvaluateNurbs(IList<DxfPoint> controls, IList<double> weights,
            IList<double> knots, int degree, double u)
        {
            int n = controls.Count - 1;
            int span = FindKnotSpan(n, degree, u, knots);
            var d = new HomogeneousPoint[degree + 1];
            for (int j = 0; j <= degree; j++)
            {
                int index = span - degree + j;
                double w = index >= 0 && index < weights.Count ? weights[index] : 1.0;
                var cp = controls[index];
                d[j] = new HomogeneousPoint { X = cp.X * w, Y = cp.Y * w, Z = cp.Z * w, W = w };
            }

            for (int r = 1; r <= degree; r++)
            {
                for (int j = degree; j >= r; j--)
                {
                    int left = span - degree + j;
                    int right = span + 1 + j - r;
                    double denom = knots[right] - knots[left];
                    double alpha = Math.Abs(denom) < 1e-14 ? 0.0 : (u - knots[left]) / denom;
                    d[j] = Blend(d[j - 1], d[j], alpha);
                }
            }

            var h = d[degree];
            double wFinal = Math.Abs(h.W) < 1e-14 ? 1.0 : h.W;
            return new DxfPoint { X = h.X / wFinal, Y = h.Y / wFinal, Z = h.Z / wFinal };
        }

        private static int FindKnotSpan(int n, int degree, double u, IList<double> knots)
        {
            if (u >= knots[n + 1] - 1e-12) return n;
            if (u <= knots[degree] + 1e-12) return degree;

            int low = degree;
            int high = n + 1;
            int mid = (low + high) / 2;
            while (u < knots[mid] || u >= knots[mid + 1])
            {
                if (u < knots[mid]) high = mid;
                else low = mid;
                mid = (low + high) / 2;
            }
            return mid;
        }

        private static HomogeneousPoint Blend(HomogeneousPoint a, HomogeneousPoint b, double alpha)
        {
            double beta = 1.0 - alpha;
            return new HomogeneousPoint
            {
                X = beta * a.X + alpha * b.X,
                Y = beta * a.Y + alpha * b.Y,
                Z = beta * a.Z + alpha * b.Z,
                W = beta * a.W + alpha * b.W
            };
        }

        private static void AppendAdaptiveSplinePoints(List<DxfPoint> output, IList<DxfPoint> controls,
            IList<double> weights, IList<double> knots, int degree, double u0, double u1,
            DxfPoint p0, DxfPoint p1, int depth)
        {
            double um = (u0 + u1) * 0.5;
            var pm = EvaluateNurbs(controls, weights, knots, degree, um);
            double deviation = DistancePointToSegment(pm, p0, p1);

            if (depth < SplineMaxDepth && deviation > SplineChordTolerance)
            {
                AppendAdaptiveSplinePoints(output, controls, weights, knots, degree, u0, um, p0, pm, depth + 1);
                AppendAdaptiveSplinePoints(output, controls, weights, knots, degree, um, u1, pm, p1, depth + 1);
                return;
            }

            if (output.Count == 0 || Distance(output[output.Count - 1], p1) > 1e-9)
                output.Add(p1);
        }

        private static double DistancePointToSegment(DxfPoint p, DxfPoint a, DxfPoint b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double lenSq = vx * vx + vy * vy;
            if (lenSq <= 1e-20) return Distance(p, a);
            double t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / lenSq;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            double px = a.X + t * vx;
            double py = a.Y + t * vy;
            double dx = p.X - px;
            double dy = p.Y - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Distance(DxfPoint a, DxfPoint b)
        {
            if (a == null || b == null) return double.PositiveInfinity;
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double ParseDoubleOrNaN(string value)
        {
            double parsed;
            return TryParseDouble(value, out parsed) ? parsed : double.NaN;
        }

        private static bool IsPaperSpace(DxfEntity entity)
        {
            return entity != null && entity.Pairs.Any(p => p.Code == 67 && (p.Value ?? "").Trim() == "1");
        }

        private static void NormalizeHorizontalOrientation(IList<DxfEntity> entities)
        {
            if (entities == null || entities.Count == 0) return;

            double bestLengthSq = 0.0;
            double bestAngle = 0.0;
            foreach (var entity in entities)
            {
                if (entity == null || !string.Equals(entity.Type, "LINE", StringComparison.OrdinalIgnoreCase)) continue;
                double x1 = GetDouble(entity, 10, double.NaN);
                double y1 = GetDouble(entity, 20, double.NaN);
                double x2 = GetDouble(entity, 11, double.NaN);
                double y2 = GetDouble(entity, 21, double.NaN);
                if (double.IsNaN(x1) || double.IsNaN(y1) || double.IsNaN(x2) || double.IsNaN(y2)) continue;
                double dx = x2 - x1;
                double dy = y2 - y1;
                double lenSq = dx * dx + dy * dy;
                if (lenSq <= bestLengthSq) continue;
                bestLengthSq = lenSq;
                bestAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            }

            if (bestLengthSq <= 0.000001) return;

            // Açı yönünü 0 dereceye en kısa dönüş olacak şekilde normalize et.
            while (bestAngle > 90.0) bestAngle -= 180.0;
            while (bestAngle < -90.0) bestAngle += 180.0;
            if (Math.Abs(bestAngle) < 0.25) return;

            RotateEntities(entities, -bestAngle);
        }

        private static void RotateEntities(IList<DxfEntity> entities, double angleDegrees)
        {
            double rad = angleDegrees * Math.PI / 180.0;
            double c = Math.Cos(rad);
            double sn = Math.Sin(rad);

            foreach (var entity in entities)
            {
                if (entity == null) continue;

                for (int xCode = 10; xCode <= 18; xCode++)
                {
                    var xPair = entity.Pairs.FirstOrDefault(p => p.Code == xCode);
                    var yPair = entity.Pairs.FirstOrDefault(p => p.Code == xCode + 10);
                    if (xPair == null || yPair == null) continue;
                    double x, y;
                    if (!TryParseDouble(xPair.Value, out x) || !TryParseDouble(yPair.Value, out y)) continue;
                    double rx = x * c - y * sn;
                    double ry = x * sn + y * c;
                    xPair.Value = Format(rx);
                    yPair.Value = Format(ry);
                }

                if (string.Equals(entity.Type, "ARC", StringComparison.OrdinalIgnoreCase))
                {
                    RotateAnglePair(entity, 50, angleDegrees);
                    RotateAnglePair(entity, 51, angleDegrees);
                }
                else if (string.Equals(entity.Type, "TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    RotateAnglePair(entity, 50, angleDegrees);
                }
            }
        }

        private static void RotateAnglePair(DxfEntity entity, int code, double deltaDegrees)
        {
            var pair = entity?.Pairs?.FirstOrDefault(p => p.Code == code);
            if (pair == null) return;
            double value;
            if (!TryParseDouble(pair.Value, out value)) return;
            value += deltaDegrees;
            while (value < 0.0) value += 360.0;
            while (value >= 360.0) value -= 360.0;
            pair.Value = Format(value);
        }

        private static void ScanExtents(IList<DxfEntity> entities, ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            foreach (var entity in entities)
            {
                if (entity == null) continue;
                string type = entity.Type ?? "";
                if (string.Equals(type, "CIRCLE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "ARC", StringComparison.OrdinalIgnoreCase))
                {
                    double cx = GetDouble(entity, 10, double.NaN);
                    double cy = GetDouble(entity, 20, double.NaN);
                    double r = Math.Abs(GetDouble(entity, 40, double.NaN));
                    if (!double.IsNaN(cx) && !double.IsNaN(cy) && !double.IsNaN(r))
                    {
                        Include(cx - r, cy - r, ref minX, ref minY, ref maxX, ref maxY);
                        Include(cx + r, cy + r, ref minX, ref minY, ref maxX, ref maxY);
                        continue;
                    }
                }

                if (string.Equals(type, "POLYLINE", StringComparison.OrdinalIgnoreCase)) continue;

                for (int i = 0; i < entity.Pairs.Count; i++)
                {
                    var p = entity.Pairs[i];
                    if (p.Code < 10 || p.Code > 18) continue;
                    double x;
                    if (!TryParseDouble(p.Value, out x)) continue;
                    int yCode = p.Code + 10;
                    double y = double.NaN;
                    for (int j = i + 1; j < entity.Pairs.Count; j++)
                    {
                        if (entity.Pairs[j].Code == yCode)
                        {
                            TryParseDouble(entity.Pairs[j].Value, out y);
                            break;
                        }
                    }
                    if (!double.IsNaN(y)) Include(x, y, ref minX, ref minY, ref maxX, ref maxY);
                }
            }
        }

        private static void Include(double x, double y, ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        private static int AppendTranslatedEntities(StringBuilder sb, SourceGeometry geometry, double dx, double dy, string layerPrefix = null)
        {
            int count = 0;
            foreach (var entity in geometry.Entities)
            {
                if (entity == null || !R12EntityTypes.Contains(entity.Type)) continue;

                string sourceLinetype = GetString(entity, 6);
                string sourceLayer = GetString(entity, 8);
                string layer;
                string linetype;
                MapStyle(sourceLinetype, sourceLayer, out layer, out linetype);
                if (!string.IsNullOrWhiteSpace(layerPrefix)) layer = layerPrefix + "_" + layer.Replace("SRC_", "");

                sb.AppendLine("0");
                sb.AppendLine(entity.Type.ToUpperInvariant());
                sb.AppendLine("8");
                sb.AppendLine(layer);
                sb.AppendLine("6");
                sb.AppendLine(linetype);
                // Explicit entity ACI color prevents CAD importers/re-savers from
                // substituting the source entity's old dark color.
                sb.AppendLine("62");
                sb.AppendLine(GetOutputColor(layer).ToString(CultureInfo.InvariantCulture));

                foreach (var pair in entity.Pairs)
                {
                    int code = pair.Code;
                    if (ShouldDropMetadata(code)) continue;
                    if (code == 8 || code == 6 || code == 67) continue;
                    if (code == 7 && string.Equals(entity.Type, "TEXT", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("7");
                        sb.AppendLine("STANDARD");
                        continue;
                    }

                    string valueText = pair.Value ?? "";
                    double value;
                    if (TryParseDouble(valueText, out value))
                    {
                        bool polylineHeader = string.Equals(entity.Type, "POLYLINE", StringComparison.OrdinalIgnoreCase);
                        if (!polylineHeader && code >= 10 && code <= 18) valueText = Format(value + dx);
                        else if (!polylineHeader && code >= 20 && code <= 28) valueText = Format(value + dy);
                    }

                    sb.AppendLine(code.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine(valueText);
                }

                if (!string.Equals(entity.Type, "VERTEX", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entity.Type, "SEQEND", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entity.Type, "TEXT", StringComparison.OrdinalIgnoreCase)) count++;
            }
            return count;
        }

        private static bool ShouldDropMetadata(int code)
        {
            if (code == 5 || code == 105 || code == 100 || code == 102 || code == 410) return true;
            if (code >= 330 && code <= 369) return true;
            // Kaynak entity'nin açık ACI renk kodu (62) katman rengimizi ezmemeli.
            // Kesim/büküm/merkez renkleri yalnız normalize edilmiş SRC_* katmanlarından gelir.
            if (code == 62 || code == 370 || code == 390 || code == 420 || code == 430 || code == 440) return true;
            if (code >= 1000) return true; // XDATA is never needed for laser geometry
            return false;
        }

        private static void MapStyle(string sourceLinetype, string sourceLayer, out string layer, out string linetype)
        {
            string value = NormalizeAscii((sourceLinetype ?? "") + " " + (sourceLayer ?? "")).ToUpperInvariant();
            if (value.Contains("KESIK") || value.Contains("DASH") || value.Contains("HIDDEN"))
            {
                layer = "SRC_BEND";
                linetype = "DASHED";
                return;
            }
            if (value.Contains("MERKEZ") || value.Contains("CENTER"))
            {
                layer = "SRC_CENTER";
                linetype = "CENTER";
                return;
            }
            layer = "SRC_GEOMETRY";
            linetype = "CONTINUOUS";
        }

        private static double EstimateInfoWidth(LaserPartRow row)
        {
            string code = string.IsNullOrWhiteSpace(row?.PartCode) ? "-" : row.PartCode.Trim();
            string thickness = string.IsNullOrWhiteSpace(row?.ThicknessText) ? "-" : row.ThicknessText.Trim();
            string material = string.IsNullOrWhiteSpace(row?.Material) ? "-" : row.Material.Trim();
            int qty = row == null ? 0 : row.Quantity;
            string[] lines =
            {
                "PARCA KODU: " + code,
                "ADET: " + qty.ToString(CultureInfo.InvariantCulture),
                "KALINLIK: " + thickness,
                "MALZEME: " + material
            };
            int maxChars = lines.Max(v => (v ?? "").Length);
            return Math.Max(80.0, maxChars * InfoTextHeight * 0.65);
        }

        private static int AppendPartInfo(StringBuilder sb, LaserPartRow row, double x, double yTop)
        {
            string code = string.IsNullOrWhiteSpace(row?.PartCode) ? "-" : row.PartCode.Trim();
            string thickness = string.IsNullOrWhiteSpace(row?.ThicknessText) ? "-" : row.ThicknessText.Trim();
            string material = string.IsNullOrWhiteSpace(row?.Material) ? "-" : row.Material.Trim();
            int qty = row == null ? 0 : row.Quantity;

            AppendText(sb, x, yTop, "PARCA KODU: " + code, InfoTextHeight);
            AppendText(sb, x, yTop + InfoLineStep, "ADET: " + qty.ToString(CultureInfo.InvariantCulture), InfoTextHeight);
            AppendText(sb, x, yTop + InfoLineStep * 2.0, "KALINLIK: " + thickness, InfoTextHeight);
            AppendText(sb, x, yTop + InfoLineStep * 3.0, "MALZEME: " + material, InfoTextHeight);
            return 4;
        }

        private static int GetOutputColor(string layer)
        {
            if (string.Equals(layer, "SRC_BEND", StringComparison.OrdinalIgnoreCase)) return 3;   // green
            if (string.Equals(layer, "SRC_CENTER", StringComparison.OrdinalIgnoreCase)) return 4; // cyan
            if (string.Equals(layer, "SRC_INFO", StringComparison.OrdinalIgnoreCase)) return 1;   // red
            return 7; // ACI 7 = white on dark CAD backgrounds / black on light backgrounds
        }

        private static void AppendHeaderAndTables(StringBuilder sb, double minX, double minY, double maxX, double maxY, int sheetCount = 0)
        {
            // AutoCAD R12 ASCII. The geometry can be perfectly valid yet open off-screen
            // when no active model-space view is stored. Write both HEADER view variables
            // and an *ACTIVE VPORT so AutoCAD opens directly on the combined layout.
            double width = Math.Max(1.0, maxX - minX);
            double height = Math.Max(1.0, maxY - minY);
            double centerX = (minX + maxX) * 0.5;
            double centerY = (minY + maxY) * 0.5;
            double viewHeight = Math.Max(height, width / 1.6) * 1.10;

            Add(sb, 0, "SECTION");
            Add(sb, 2, "HEADER");
            Add(sb, 9, "$ACADVER"); Add(sb, 1, "AC1009");
            Add(sb, 9, "$DWGCODEPAGE"); Add(sb, 3, "ANSI_1254");
            AddPointHeader(sb, "$EXTMIN", minX, minY);
            AddPointHeader(sb, "$EXTMAX", maxX, maxY);
            AddPointHeader(sb, "$LIMMIN", minX, minY);
            AddPointHeader(sb, "$LIMMAX", maxX, maxY);

            Add(sb, 9, "$VIEWCTR");
            Add(sb, 10, Format(centerX));
            Add(sb, 20, Format(centerY));
            Add(sb, 9, "$VIEWSIZE");
            Add(sb, 40, Format(viewHeight));

            Add(sb, 0, "ENDSEC");

            Add(sb, 0, "SECTION");
            Add(sb, 2, "TABLES");

            Add(sb, 0, "TABLE"); Add(sb, 2, "VPORT"); Add(sb, 70, "1");
            AppendActiveViewport(sb, centerX, centerY, viewHeight);
            Add(sb, 0, "ENDTAB");

            Add(sb, 0, "TABLE"); Add(sb, 2, "LTYPE"); Add(sb, 70, "3");
            AppendLinetype(sb, "CONTINUOUS", "Solid line", 0.0, new double[0]);
            AppendLinetype(sb, "DASHED", "Dashed __ __ __", 12.0, new[] { 8.0, -4.0 });
            AppendLinetype(sb, "CENTER", "Center ____ _ ____ _", 21.0, new[] { 12.0, -3.0, 3.0, -3.0 });
            Add(sb, 0, "ENDTAB");

            int layerCount = sheetCount > 0 ? sheetCount * 3 : 5;
            Add(sb, 0, "TABLE"); Add(sb, 2, "LAYER"); Add(sb, 70, layerCount.ToString(CultureInfo.InvariantCulture));
            if (sheetCount > 0)
            {
                for (int sheet = 1; sheet <= sheetCount; sheet++)
                {
                    string prefix = "SMARTNEST_SHEET_" + sheet.ToString(CultureInfo.InvariantCulture);
                    AppendLayer(sb, prefix + "_GEOMETRY", 7, "CONTINUOUS");
                    AppendLayer(sb, prefix + "_BEND", 3, "DASHED");
                    AppendLayer(sb, prefix + "_CENTER", 4, "CENTER");
                }
            }
            else
            {
                AppendLayer(sb, "SRC_GEOMETRY", 7, "CONTINUOUS");
                AppendLayer(sb, "SRC_BEND", 3, "DASHED");
                AppendLayer(sb, "SRC_CENTER", 4, "CENTER");
                AppendLayer(sb, "SRC_INFO", 1, "CONTINUOUS");
                AppendLayer(sb, "SRC_SEPARATOR", 7, "CONTINUOUS");
            }
            Add(sb, 0, "ENDTAB");

            Add(sb, 0, "TABLE"); Add(sb, 2, "STYLE"); Add(sb, 70, "1");
            Add(sb, 0, "STYLE"); Add(sb, 2, "STANDARD"); Add(sb, 70, "0");
            Add(sb, 40, "0.0"); Add(sb, 41, "1.0"); Add(sb, 50, "0.0"); Add(sb, 71, "0");
            Add(sb, 42, "2.5"); Add(sb, 3, "txt"); Add(sb, 4, "");
            Add(sb, 0, "ENDTAB");

            Add(sb, 0, "ENDSEC");
            Add(sb, 0, "SECTION");
            Add(sb, 2, "ENTITIES");
        }

        private static void AppendActiveViewport(StringBuilder sb, double centerX, double centerY, double viewHeight)
        {
            Add(sb, 0, "VPORT");
            Add(sb, 2, "*ACTIVE");
            Add(sb, 70, "0");
            Add(sb, 10, "0.0"); Add(sb, 20, "0.0");
            Add(sb, 11, "1.0"); Add(sb, 21, "1.0");
            Add(sb, 12, Format(centerX)); Add(sb, 22, Format(centerY));
            Add(sb, 13, "0.0"); Add(sb, 23, "0.0");
            Add(sb, 14, "10.0"); Add(sb, 24, "10.0");
            Add(sb, 15, "10.0"); Add(sb, 25, "10.0");
            Add(sb, 16, "0.0"); Add(sb, 26, "0.0"); Add(sb, 36, "1.0");
            Add(sb, 17, "0.0"); Add(sb, 27, "0.0"); Add(sb, 37, "0.0");
            Add(sb, 40, Format(viewHeight));
            Add(sb, 41, "1.6");
            Add(sb, 42, "50.0");
            Add(sb, 43, "0.0");
            Add(sb, 44, "0.0");
            Add(sb, 50, "0.0");
            Add(sb, 51, "0.0");
            Add(sb, 71, "0");
            Add(sb, 72, "100");
            Add(sb, 73, "1");
            Add(sb, 74, "3");
            Add(sb, 75, "0");
            Add(sb, 76, "0");
            Add(sb, 77, "0");
            Add(sb, 78, "0");
        }

        private static void AppendLinetype(StringBuilder sb, string name, string description, double length, double[] pattern)
        {
            Add(sb, 0, "LTYPE"); Add(sb, 2, name); Add(sb, 70, "0"); Add(sb, 3, description);
            Add(sb, 72, "65"); Add(sb, 73, pattern.Length.ToString(CultureInfo.InvariantCulture)); Add(sb, 40, Format(length));
            foreach (double segment in pattern) Add(sb, 49, Format(segment));
        }

        private static void AppendLayer(StringBuilder sb, string name, int color, string linetype)
        {
            Add(sb, 0, "LAYER"); Add(sb, 2, name); Add(sb, 70, "0");
            Add(sb, 62, color.ToString(CultureInfo.InvariantCulture)); Add(sb, 6, linetype);
        }

        private static void AppendText(StringBuilder sb, double x, double y, string text, double height)
        {
            Add(sb, 0, "TEXT"); Add(sb, 8, "SRC_INFO"); Add(sb, 6, "CONTINUOUS"); Add(sb, 62, "1");
            Add(sb, 10, Format(x)); Add(sb, 20, Format(y)); Add(sb, 30, "0.0");
            Add(sb, 40, Format(height)); Add(sb, 1, SanitizeDxfText(text)); Add(sb, 7, "STANDARD");
        }

        private static void AddPointHeader(StringBuilder sb, string name, double x, double y)
        {
            Add(sb, 9, name); Add(sb, 10, Format(x)); Add(sb, 20, Format(y)); Add(sb, 30, "0.0");
        }

        private static void Add(StringBuilder sb, int code, string value)
        {
            sb.AppendLine(code.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(value ?? "");
        }

        private static string DetectText(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes);
            try
            {
                Encoding enc = Encoding.GetEncoding(1254);
                return enc.GetString(bytes);
            }
            catch { return Encoding.Default.GetString(bytes); }
        }

        private static List<string> NormalizeLines(string text)
        {
            var raw = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            while (raw.Count > 0 && string.IsNullOrWhiteSpace(raw[raw.Count - 1])) raw.RemoveAt(raw.Count - 1);
            return raw;
        }

        private static int FindEntitiesStart(IList<string> lines)
        {
            for (int i = 0; i + 3 < lines.Count; i += 2)
            {
                if (TrimCode(lines[i]) == "0" && string.Equals(lines[i + 1].Trim(), "SECTION", StringComparison.OrdinalIgnoreCase) &&
                    TrimCode(lines[i + 2]) == "2" && string.Equals(lines[i + 3].Trim(), "ENTITIES", StringComparison.OrdinalIgnoreCase))
                    return i + 4;
            }
            return -1;
        }

        private static int FindEntitiesEnd(IList<string> lines, int start)
        {
            for (int i = start; i + 1 < lines.Count; i += 2)
            {
                if (TrimCode(lines[i]) == "0" && string.Equals(lines[i + 1].Trim(), "ENDSEC", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string GetString(DxfEntity entity, int code)
        {
            var pair = entity?.Pairs?.FirstOrDefault(p => p.Code == code);
            return pair == null ? "" : (pair.Value ?? "").Trim();
        }

        private static double GetDouble(DxfEntity entity, int code, double fallback)
        {
            double value;
            return TryParseDouble(GetString(entity, code), out value) ? value : fallback;
        }

        private static int GetInt(DxfEntity entity, int code, int fallback)
        {
            int value;
            return int.TryParse(GetString(entity, code), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static string SanitizeDxfText(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
        }

        private static string NormalizeAscii(string text)
        {
            return (text ?? "")
                .Replace('ı', 'i').Replace('İ', 'I')
                .Replace('ş', 's').Replace('Ş', 'S')
                .Replace('ğ', 'g').Replace('Ğ', 'G')
                .Replace('ü', 'u').Replace('Ü', 'U')
                .Replace('ö', 'o').Replace('Ö', 'O')
                .Replace('ç', 'c').Replace('Ç', 'C');
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
        private static string TrimCode(string value) => (value ?? "").Trim();
    }
}
