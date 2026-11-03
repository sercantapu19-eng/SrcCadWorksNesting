using System.Drawing;
using System.Drawing.Drawing2D;

namespace SrcCadWorks.Services
{
    // Original SRC geometry, drawn at display resolution; no external icon dependency.
    internal static class CadDocumentIcons
    {
        public static Image Part(int size = 24) => Draw(size, 0);
        public static Image Assembly(int size = 24) => Draw(size, 1);
        public static Image Drawing(int size = 24) => Draw(size, 2);
        public static Image AllAssemblies(int size = 24)
        {
            var bitmap = new Bitmap(size,size);
            using (var g = Graphics.FromImage(bitmap))
            using (var assembly = Assembly(48))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.ScaleTransform(size/32f,size/32f);
                g.DrawImage(assembly,new RectangleF(1,1,21,21));
                g.DrawImage(assembly,new RectangleF(7,8,21,21));
                using (var badge = new SolidBrush(Color.FromArgb(190,24,39))) g.FillEllipse(badge,19,19,13,13);
                using (var outline = new Pen(Color.White,1)) g.DrawEllipse(outline,19,19,13,13);
                // Geometric sigma stays sharp at small sizes and avoids font dependence.
                using (var sigma = new Pen(Color.White,1.6f))
                {
                    sigma.LineJoin = LineJoin.Round;
                    g.DrawLines(sigma,new[] {new PointF(28,22),new PointF(23,22),new PointF(26,25.5f),new PointF(23,29),new PointF(28,29)});
                }
            }
            return bitmap;
        }
        private static Image Draw(int size, int kind)
        {
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 32f, size / 32f);
                using (var shadow = new SolidBrush(Color.FromArgb(28, 30, 36, 44))) g.FillEllipse(shadow, 4, 26, 24, 4);
                if (kind == 2)
                {
                    using (var paper = new SolidBrush(Color.White)) g.FillRectangle(paper, 4, 3, 24, 25);
                    using (var line = new Pen(Color.FromArgb(70, 78, 90), .9f)) { g.DrawRectangle(line, 4, 3, 24, 25); g.DrawRectangle(line, 6, 5, 20, 21); g.DrawLine(line, 18, 21, 26, 21); g.DrawLine(line, 18, 24, 26, 24); g.DrawLine(line, 18, 21, 18, 26); }
                    Face(g, new[] { new PointF(8,8),new PointF(17,8),new PointF(17,13),new PointF(8,13) }, Color.FromArgb(248,99,103),Color.FromArgb(194,26,39));
                    Face(g, new[] { new PointF(8,17),new PointF(12,17),new PointF(12,15),new PointF(15,15),new PointF(15,23),new PointF(8,23) }, Color.FromArgb(239,83,90),Color.FromArgb(177,25,36));
                    Face(g, new[] { new PointF(20,8),new PointF(24,8),new PointF(24,13),new PointF(20,13) },Color.FromArgb(245,246,250),Color.FromArgb(174,185,199));
                }
                else
                {
                    Face(g,new[] {new PointF(3,12),new PointF(12,8),new PointF(28,16),new PointF(19,21)},Color.FromArgb(255,168,166),Color.FromArgb(235,70,80));
                    Face(g,new[] {new PointF(3,12),new PointF(19,21),new PointF(19,29),new PointF(3,20)},Color.FromArgb(235,64,76),Color.FromArgb(190,24,39));
                    Face(g,new[] {new PointF(19,21),new PointF(28,16),new PointF(28,24),new PointF(19,29)},Color.FromArgb(200,29,46),Color.FromArgb(133,15,30));
                    if(kind==0)
                    {
                        Face(g,new[] {new PointF(9,6),new PointF(18,2),new PointF(25,6),new PointF(16,10)},Color.FromArgb(255,193,188),Color.FromArgb(245,112,117));
                        Face(g,new[] {new PointF(9,6),new PointF(16,10),new PointF(16,19),new PointF(9,15)},Color.FromArgb(241,83,93),Color.FromArgb(195,30,43));
                        Face(g,new[] {new PointF(16,10),new PointF(25,6),new PointF(25,15),new PointF(16,19)},Color.FromArgb(218,43,60),Color.FromArgb(158,21,37));
                    }
                    else
                    {
                        Face(g,new[] {new PointF(12,6),new PointF(21,2),new PointF(29,6),new PointF(20,10)},Color.White,Color.FromArgb(219,231,241));
                        Face(g,new[] {new PointF(12,6),new PointF(20,10),new PointF(20,19),new PointF(12,15)},Color.FromArgb(226,238,247),Color.FromArgb(136,173,198));
                        Face(g,new[] {new PointF(20,10),new PointF(29,6),new PointF(29,15),new PointF(20,19)},Color.FromArgb(173,203,223),Color.FromArgb(94,136,166));
                    }
                }
            }
            return bitmap;
        }
        private static void Face(Graphics g, PointF[] points, Color light, Color dark)
        {
            using (var brush = new LinearGradientBrush(new RectangleF(0,0,32,32), light,dark,90f)) g.FillPolygon(brush,points);
            using (var outline = new Pen(Color.FromArgb(95,40,47,59),.8f)) g.DrawPolygon(outline,points);
        }
    }
}
