using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace SrcCadWorks.CadViewer
{
    internal sealed class CadScene
    {
        public string SourcePath = "";
        public string Configuration = "";
        public readonly List<CadSceneNode> Nodes = new List<CadSceneNode>();

        public Rect3D Bounds
        {
            get
            {
                Rect3D result = Rect3D.Empty;
                foreach (var n in Nodes)
                {
                    if (n == null || n.Hidden || n.Mesh == null) continue;
                    if (result.IsEmpty) result = n.Mesh.Bounds;
                    else result.Union(n.Mesh.Bounds);
                }
                return result;
            }
        }
    }

    internal sealed class CadSceneNode
    {
        public string Name = "";
        public string SourcePath = "";
        public string Configuration = "";
        public MeshGeometry3D Mesh;
        public byte ColorR = 160;
        public byte ColorG = 160;
        public byte ColorB = 156;
        public bool Hidden;
        public GeometryModel3D Model;
    }

    internal struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }
}
