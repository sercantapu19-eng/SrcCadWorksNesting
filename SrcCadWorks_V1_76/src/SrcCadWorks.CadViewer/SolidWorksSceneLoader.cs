using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Media3D;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.CadViewer
{
    internal sealed class SolidWorksSceneLoader
    {
        private const int CacheVersion = 3;
        private readonly SldWorks _swApp;

        public SolidWorksSceneLoader(SldWorks swApp = null)
        {
            _swApp = swApp ?? TryGetActiveSolidWorks();
        }

        public CadScene Load(string path, string configuration, Action<string> status = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("3D dosyası bulunamadı.", path);

            configuration = configuration ?? "";
            string cache = CachePath(path, configuration);
            CadScene cached;
            if (TryReadCache(cache, path, configuration, out cached))
            {
                status?.Invoke("SrcCadViewer cache • " + Path.GetFileName(path));
                return cached;
            }

            if (_swApp == null)
                throw new InvalidOperationException("SOLIDWORKS bağlantısı bulunamadı. SrcCadViewer şu aşamada SrcCadWorks içinde çalışan SOLIDWORKS tessellation verisini kullanır.");

            status?.Invoke("SrcCadViewer 3D hazırlanıyor • " + Path.GetFileName(path));
            CadScene scene = LoadFromSolidWorks(path, configuration, status);
            TryWriteCache(cache, scene, path, configuration);
            return scene;
        }

        private CadScene LoadFromSolidWorks(string path, string configuration, Action<string> status)
        {
            var scene = new CadScene { SourcePath = path, Configuration = configuration };
            string ext = Path.GetExtension(path) ?? "";
            int docType = DocumentTypeFromExtension(ext);
            ModelDoc2 model = null;
            bool openedByUs = false;
            bool visibilitySuppressed = false;
            string originalConfig = "";
            bool switched = false;

            try
            {
                try { model = _swApp.GetOpenDocumentByName(path) as ModelDoc2; } catch { }
                if (model == null)
                {
                    try { ((dynamic)_swApp).DocumentVisible(false, docType); visibilitySuppressed = true; } catch { }
                    int errors = 0, warnings = 0;
                    model = _swApp.OpenDoc6(path, docType,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        configuration ?? "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = model != null;
                    if (model == null)
                        throw new InvalidOperationException("3D belge SOLIDWORKS ile açılamadı. Hata=" + errors + " | " + path);
                }

                try { originalConfig = model.ConfigurationManager.ActiveConfiguration.Name ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(configuration) &&
                    !string.Equals(originalConfig, configuration, StringComparison.OrdinalIgnoreCase))
                {
                    try { switched = model.ShowConfiguration2(configuration); } catch { }
                }

                int actualType = 0;
                try { actualType = model.GetType(); } catch { actualType = docType; }
                if (actualType == (int)swDocumentTypes_e.swDocASSEMBLY)
                    LoadAssembly(scene, model as AssemblyDoc, status);
                else
                    LoadPart(scene, model as PartDoc, Path.GetFileNameWithoutExtension(path), path, configuration, null, ReadModelAppearance(model));

                if (scene.Nodes.Count == 0)
                    throw new InvalidOperationException("SrcCadViewer için 3D tessellation alınamadı: " + Path.GetFileName(path));
                return scene;
            }
            finally
            {
                if (!openedByUs && switched && model != null && !string.IsNullOrWhiteSpace(originalConfig))
                {
                    try { model.ShowConfiguration2(originalConfig); } catch { }
                }
                if (openedByUs && model != null)
                {
                    try { _swApp.CloseDoc(model.GetTitle()); } catch { }
                }
                if (visibilitySuppressed)
                {
                    try { ((dynamic)_swApp).DocumentVisible(true, docType); } catch { }
                }
            }
        }

        private void LoadAssembly(CadScene scene, AssemblyDoc assembly, Action<string> status)
        {
            if (assembly == null) return;
            object[] raw = null;
            try { raw = assembly.GetComponents(false) as object[]; } catch { }
            if (raw == null) raw = new object[0];

            var parts = raw.OfType<Component2>()
                .Where(c => c != null && IsPartComponent(c))
                .ToList();
            if (parts.Count == 0) return;

            int index = 0;
            // Keep the complete tessellation for every unique part/configuration.
            // Previous versions sampled every Nth triangle to stay inside a budget;
            // that produced literal holes in the rendered surfaces. We now keep
            // full triangle streams and rely on per-file mesh cache for speed.
            var localTriangleCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var comp in parts)
            {
                index++;
                if ((index % 25) == 0)
                    status?.Invoke("SrcCadViewer montaj • " + index + "/" + parts.Count + " bileşen");

                string compPath = "";
                string compCfg = "";
                string name = "";
                try { compPath = comp.GetPathName() ?? ""; } catch { }
                try { compCfg = comp.ReferencedConfiguration ?? ""; } catch { }
                try { name = comp.Name2 ?? Path.GetFileNameWithoutExtension(compPath); }
                catch { name = Path.GetFileNameWithoutExtension(compPath); }

                double[] transform = TransformData(comp);
                string meshKey = compPath + "|" + compCfg;
                float[] triangles;
                if (!localTriangleCache.TryGetValue(meshKey, out triangles))
                {
                    // Imported vendor parts (Interroll, STEP, IGES, etc.) frequently consist
                    // of many sheet/surface bodies instead of one ordinary solid body.
                    // Component.GetTessTriangles can be incomplete for those cases, so
                    // traverse all surface/solid faces and combine their shaded tessellation.
                    if (HasSurfaceBodies(comp))
                        triangles = TryGetComponentFaceTriangles(comp);

                    if (triangles == null || triangles.Length < 9)
                        triangles = TryGetComponentTriangles(comp);

                    if (triangles == null || triangles.Length < 9)
                    {
                        ModelDoc2 partModel = null;
                        try { partModel = comp.GetModelDoc2() as ModelDoc2; } catch { }

                        if (HasSurfaceBodies(partModel as PartDoc))
                            triangles = TryGetPartFaceTriangles(partModel as PartDoc);

                        if (triangles == null || triangles.Length < 9)
                            triangles = TryGetPartTriangles(partModel as PartDoc);
                    }

                    if (triangles != null && triangles.Length >= 9)
                        localTriangleCache[meshKey] = triangles;
                }
                if (triangles == null || triangles.Length < 9) continue;

                byte[] appearance = ReadComponentAppearance(comp);
                CadSceneNode node = BuildNode(name, compPath, compCfg, triangles, transform, appearance);
                if (node != null) scene.Nodes.Add(node);
            }
        }

        private void LoadPart(CadScene scene, PartDoc part, string name, string path, string configuration, double[] transform, byte[] appearance)
        {
            float[] triangles = null;
            if (HasSurfaceBodies(part))
                triangles = TryGetPartFaceTriangles(part);

            if (triangles == null || triangles.Length < 9)
                triangles = TryGetPartTriangles(part);

            if (triangles == null || triangles.Length < 9) return;
            CadSceneNode node = BuildNode(name, path, configuration, triangles, transform, appearance);
            if (node != null) scene.Nodes.Add(node);
        }

        private static bool IsPartComponent(Component2 comp)
        {
            try
            {
                if (comp.IsSuppressed()) return false;
            }
            catch { }
            string path = "";
            try { path = comp.GetPathName() ?? ""; } catch { }
            return path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".STEP", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".STP", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".IGES", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".IGS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasSurfaceBodies(Component2 comp)
        {
            if (comp == null) return false;
            try
            {
                object raw = comp.GetBodies2((int)swBodyType_e.swSheetBody);
                Array a = raw as Array;
                return a != null && a.Length > 0;
            }
            catch { return false; }
        }

        private static bool HasSurfaceBodies(PartDoc part)
        {
            if (part == null) return false;
            try
            {
                object raw = part.GetBodies2((int)swBodyType_e.swSheetBody, true);
                Array a = raw as Array;
                return a != null && a.Length > 0;
            }
            catch { return false; }
        }

        private static float[] TryGetComponentFaceTriangles(Component2 comp)
        {
            if (comp == null) return null;
            var all = new List<float>(16384);

            AppendComponentBodies(comp, (int)swBodyType_e.swSolidBody, all);
            AppendComponentBodies(comp, (int)swBodyType_e.swSheetBody, all);

            return all.Count >= 9 ? all.ToArray() : null;
        }

        private static void AppendComponentBodies(Component2 comp, int bodyType, List<float> output)
        {
            if (comp == null || output == null) return;
            try
            {
                Array bodies = comp.GetBodies2(bodyType) as Array;
                if (bodies == null) return;

                foreach (object rawBody in bodies)
                    AppendBodyFaces(rawBody as Body2, output);
            }
            catch { }
        }

        private static float[] TryGetPartFaceTriangles(PartDoc part)
        {
            if (part == null) return null;
            var all = new List<float>(16384);

            AppendPartBodies(part, (int)swBodyType_e.swSolidBody, all);
            AppendPartBodies(part, (int)swBodyType_e.swSheetBody, all);

            return all.Count >= 9 ? all.ToArray() : null;
        }

        private static void AppendPartBodies(PartDoc part, int bodyType, List<float> output)
        {
            if (part == null || output == null) return;
            try
            {
                Array bodies = part.GetBodies2(bodyType, true) as Array;
                if (bodies == null) return;

                foreach (object rawBody in bodies)
                    AppendBodyFaces(rawBody as Body2, output);
            }
            catch { }
        }

        private static void AppendBodyFaces(Body2 body, List<float> output)
        {
            if (body == null || output == null) return;

            try
            {
                Face2 face = body.GetFirstFace() as Face2;
                int safety = 0;
                while (face != null && safety++ < 250000)
                {
                    try
                    {
                        float[] tri = ToFloatArray(face.GetTessTriangles(true));
                        if (tri != null && tri.Length >= 9)
                            output.AddRange(tri);
                    }
                    catch { }

                    try { face = face.GetNextFace() as Face2; }
                    catch { face = null; }
                }
            }
            catch { }
        }

        private static float[] TryGetComponentTriangles(Component2 comp)
        {
            try { return ToFloatArray(comp.GetTessTriangles(true)); } catch { return null; }
        }

        private static float[] TryGetPartTriangles(PartDoc part)
        {
            if (part == null) return null;
            try { return ToFloatArray(part.GetTessTriangles(true)); } catch { return null; }
        }

        private static float[] ToFloatArray(object value)
        {
            if (value == null) return null;
            var direct = value as float[];
            if (direct != null) return direct;
            var doubles = value as double[];
            if (doubles != null)
            {
                var f = new float[doubles.Length];
                for (int i = 0; i < doubles.Length; i++) f[i] = (float)doubles[i];
                return f;
            }
            var arr = value as Array;
            if (arr == null) return null;
            var result = new float[arr.Length];
            for (int i = 0; i < arr.Length; i++) result[i] = Convert.ToSingle(arr.GetValue(i));
            return result;
        }

        private static CadSceneNode BuildNode(string name, string path, string configuration,
            float[] values, double[] transform, byte[] appearance)
        {
            int triangleCount = values.Length / 9;
            if (triangleCount <= 0) return null;

            var mesh = new MeshGeometry3D();
            int vertexIndex = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int o = t * 9;
                Point3D a = ApplyTransform(values[o], values[o + 1], values[o + 2], transform);
                Point3D b = ApplyTransform(values[o + 3], values[o + 4], values[o + 5], transform);
                Point3D c = ApplyTransform(values[o + 6], values[o + 7], values[o + 8], transform);
                Vector3D n = Vector3D.CrossProduct(b - a, c - a);
                if (n.LengthSquared > 1e-20) n.Normalize();
                else n = new Vector3D(0, 0, 1);

                mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
                mesh.Normals.Add(n); mesh.Normals.Add(n); mesh.Normals.Add(n);
                mesh.TriangleIndices.Add(vertexIndex++);
                mesh.TriangleIndices.Add(vertexIndex++);
                mesh.TriangleIndices.Add(vertexIndex++);
            }

            if (mesh.Positions.Count == 0) return null;
            return new CadSceneNode
            {
                Name = name ?? "",
                SourcePath = path ?? "",
                Configuration = configuration ?? "",
                Mesh = mesh,
                ColorR = appearance != null && appearance.Length > 2 ? appearance[0] : (byte)160,
                ColorG = appearance != null && appearance.Length > 2 ? appearance[1] : (byte)160,
                ColorB = appearance != null && appearance.Length > 2 ? appearance[2] : (byte)156
            };
        }

        private static byte[] ReadComponentAppearance(Component2 comp)
        {
            if (comp == null) return null;
            try
            {
                dynamic c = comp;
                object raw = null;
                try { raw = c.MaterialPropertyValues; } catch { }
                byte[] result = AppearanceBytes(raw);
                if (result != null) return result;

                ModelDoc2 model = null;
                try { model = comp.GetModelDoc2() as ModelDoc2; } catch { }
                return ReadModelAppearance(model);
            }
            catch { return null; }
        }

        private static byte[] ReadModelAppearance(ModelDoc2 model)
        {
            if (model == null) return null;
            try
            {
                dynamic m = model;
                object raw = null;
                try { raw = m.MaterialPropertyValues; } catch { }
                return AppearanceBytes(raw);
            }
            catch { return null; }
        }

        private static byte[] AppearanceBytes(object raw)
        {
            if (raw == null) return null;
            Array a = raw as Array;
            if (a == null || a.Length < 3) return null;
            try
            {
                double r = Convert.ToDouble(a.GetValue(0));
                double g = Convert.ToDouble(a.GetValue(1));
                double b = Convert.ToDouble(a.GetValue(2));
                if (double.IsNaN(r) || double.IsNaN(g) || double.IsNaN(b)) return null;
                // Component appearance may report -1 when it inherits the part/model color.
                if (r < 0 || g < 0 || b < 0) return null;
                r = Math.Max(0, Math.Min(1, r));
                g = Math.Max(0, Math.Min(1, g));
                b = Math.Max(0, Math.Min(1, b));
                return new[]
                {
                    (byte)Math.Round(r * 255.0),
                    (byte)Math.Round(g * 255.0),
                    (byte)Math.Round(b * 255.0)
                };
            }
            catch { return null; }
        }

        private static Point3D ApplyTransform(double x, double y, double z, double[] m)
        {
            if (m == null || m.Length < 13) return new Point3D(x, y, z);
            double s = Math.Abs(m[12]) < 1e-12 ? 1.0 : m[12];
            return new Point3D(
                (x * m[0] + y * m[3] + z * m[6]) * s + m[9],
                (x * m[1] + y * m[4] + z * m[7]) * s + m[10],
                (x * m[2] + y * m[5] + z * m[8]) * s + m[11]);
        }

        private static double[] TransformData(Component2 comp)
        {
            try
            {
                MathTransform tr = comp.Transform2;
                if (tr == null) return null;
                var arr = tr.ArrayData as double[];
                if (arr != null) return arr;
                var raw = tr.ArrayData as Array;
                if (raw == null) return null;
                var d = new double[raw.Length];
                for (int i = 0; i < raw.Length; i++) d[i] = Convert.ToDouble(raw.GetValue(i));
                return d;
            }
            catch { return null; }
        }

        private static int DocumentTypeFromExtension(string ext)
        {
            if (ext.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocASSEMBLY;
            if (ext.Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocDRAWING;
            return (int)swDocumentTypes_e.swDocPART;
        }

        private static SldWorks TryGetActiveSolidWorks()
        {
            try { return (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
            catch { return null; }
        }

        private static string CachePath(string path, string configuration)
        {
            var fi = new FileInfo(path);
            string identity = Path.GetFullPath(path).ToUpperInvariant() + "|" + (configuration ?? "") + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            string key;
            using (var sha = SHA256.Create())
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SrcCadWorks", "cadviewer", "mesh-cache");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, key + ".srcmesh");
        }

        private static bool TryReadCache(string cache, string sourcePath, string configuration, out CadScene scene)
        {
            scene = null;
            try
            {
                if (!File.Exists(cache)) return false;
                using (var br = new BinaryReader(File.Open(cache, FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    if (br.ReadInt32() != CacheVersion) return false;
                    var s = new CadScene { SourcePath = sourcePath, Configuration = configuration ?? "" };
                    int count = br.ReadInt32();
                    for (int n = 0; n < count; n++)
                    {
                        var node = new CadSceneNode
                        {
                            Name = br.ReadString(),
                            SourcePath = br.ReadString(),
                            Configuration = br.ReadString(),
                            ColorR = br.ReadByte(),
                            ColorG = br.ReadByte(),
                            ColorB = br.ReadByte()
                        };
                        int points = br.ReadInt32();
                        var mesh = new MeshGeometry3D();
                        for (int i = 0; i < points; i++)
                        {
                            mesh.Positions.Add(new Point3D(br.ReadDouble(), br.ReadDouble(), br.ReadDouble()));
                            mesh.Normals.Add(new Vector3D(br.ReadDouble(), br.ReadDouble(), br.ReadDouble()));
                            mesh.TriangleIndices.Add(i);
                        }
                        node.Mesh = mesh;
                        s.Nodes.Add(node);
                    }
                    scene = s;
                    return s.Nodes.Count > 0;
                }
            }
            catch { scene = null; return false; }
        }

        private static void TryWriteCache(string cache, CadScene scene, string sourcePath, string configuration)
        {
            try
            {
                string temp = cache + ".tmp";
                using (var bw = new BinaryWriter(File.Create(temp)))
                {
                    bw.Write(CacheVersion);
                    bw.Write(scene.Nodes.Count);
                    foreach (var node in scene.Nodes)
                    {
                        bw.Write(node.Name ?? ""); bw.Write(node.SourcePath ?? ""); bw.Write(node.Configuration ?? "");
                        bw.Write(node.ColorR); bw.Write(node.ColorG); bw.Write(node.ColorB);
                        int count = node.Mesh == null ? 0 : node.Mesh.Positions.Count;
                        bw.Write(count);
                        for (int i = 0; i < count; i++)
                        {
                            Point3D p = node.Mesh.Positions[i];
                            Vector3D n = node.Mesh.Normals.Count > i ? node.Mesh.Normals[i] : new Vector3D(0, 0, 1);
                            bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z);
                            bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
                        }
                    }
                }
                if (File.Exists(cache)) File.Delete(cache);
                File.Move(temp, cache);
            }
            catch { }
        }
    }
}
