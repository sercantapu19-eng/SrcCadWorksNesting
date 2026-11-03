using System;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SrcCadWorks.CadViewer
{
    internal sealed class DrawingBitmapLoader
    {
        private readonly SldWorks _swApp;
        public DrawingBitmapLoader(SldWorks swApp = null)
        {
            _swApp = swApp ?? TryGetActiveSolidWorks();
        }

        public Image Load(string path, int width, int height)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (_swApp == null) return null;

            string temp = Path.Combine(Path.GetTempPath(), "SrcCadViewer_drawing_" + Guid.NewGuid().ToString("N") + ".bmp");

            // Prefer a fresh, high-resolution SOLIDWORKS render. Stored preview bitmaps are
            // often low resolution and may carry the old green drawing background.
            ModelDoc2 model = null;
            bool openedByUs = false;
            bool visibilitySuppressed = false;
            ModelDoc2 original = null;
            try
            {
                original = _swApp.ActiveDoc as ModelDoc2;
                try { model = _swApp.GetOpenDocumentByName(path) as ModelDoc2; } catch { }

                if (model == null)
                {
                    try
                    {
                        ((dynamic)_swApp).DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING);
                        visibilitySuppressed = true;
                    }
                    catch { }

                    int errors = 0, warnings = 0;
                    model = _swApp.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        "", ref errors, ref warnings) as ModelDoc2;
                    openedByUs = model != null;
                }

                if (model != null)
                {
                    try
                    {
                        int activateError = 0;
                        _swApp.ActivateDoc3(model.GetTitle(), true, 0, ref activateError);
                        model.ViewZoomtofit2();
                        model.GraphicsRedraw2();
                    }
                    catch { }

                    int w = Math.Max(1800, Math.Min(5200, Math.Max(1, width) * 3));
                    int h = Math.Max(1200, Math.Min(3600, Math.Max(1, height) * 3));
                    bool ok = false;
                    try { ok = model.SaveBMP(temp, w, h); } catch { }

                    if (ok && File.Exists(temp))
                    {
                        using (var img = Image.FromFile(temp))
                            return NormalizeTechnicalDrawing(img);
                    }
                }
            }
            finally
            {
                if (openedByUs && model != null)
                {
                    try { _swApp.CloseDoc(model.GetTitle()); } catch { }
                }
                if (visibilitySuppressed)
                {
                    try { ((dynamic)_swApp).DocumentVisible(true, (int)swDocumentTypes_e.swDocDRAWING); } catch { }
                }
                if (original != null)
                {
                    try
                    {
                        int e = 0;
                        _swApp.ActivateDoc3(original.GetTitle(), false, 0, ref e);
                    }
                    catch { }
                }
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            // Fallback only: stored preview bitmap. It is normalized to the same clean white style.
            try
            {
                if (_swApp.GetPreviewBitmapFile(path, "", temp) && File.Exists(temp))
                {
                    using (var preview = Image.FromFile(temp))
                        return NormalizeTechnicalDrawing(preview);
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            return null;
        }

        private static Bitmap NormalizeTechnicalDrawing(Image source)
        {
            var bmp = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            // Estimate the sheet/background color from the four corners.
            Color[] corners =
            {
                bmp.GetPixel(2, 2),
                bmp.GetPixel(Math.Max(0, bmp.Width - 3), 2),
                bmp.GetPixel(2, Math.Max(0, bmp.Height - 3)),
                bmp.GetPixel(Math.Max(0, bmp.Width - 3), Math.Max(0, bmp.Height - 3))
            };
            int bgR = (corners[0].R + corners[1].R + corners[2].R + corners[3].R) / 4;
            int bgG = (corners[0].G + corners[1].G + corners[2].G + corners[3].G) / 4;
            int bgB = (corners[0].B + corners[1].B + corners[2].B + corners[3].B) / 4;

            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                int bytes = Math.Abs(stride) * bmp.Height;
                byte[] buffer = new byte[bytes];
                Marshal.Copy(data.Scan0, buffer, 0, bytes);

                for (int y = 0; y < bmp.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = row + x * 3;
                        int b = buffer[i];
                        int g = buffer[i + 1];
                        int r = buffer[i + 2];

                        int dr = r - bgR, dg = g - bgG, db = b - bgB;
                        int colorDistance2 = dr * dr + dg * dg + db * db;
                        double lum = 0.299 * r + 0.587 * g + 0.114 * b;

                        int gray;
                        if (colorDistance2 < 3600 && lum > 105)
                        {
                            gray = 255;
                        }
                        else
                        {
                            // Neutral CAD drawing: suppress color cast and increase line contrast.
                            gray = (int)Math.Max(0, Math.Min(235, (lum - 22) * 0.82));
                            if (lum > 205) gray = 255;
                        }

                        buffer[i] = buffer[i + 1] = buffer[i + 2] = (byte)gray;
                    }
                }

                Marshal.Copy(buffer, 0, data.Scan0, bytes);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        private static SldWorks TryGetActiveSolidWorks()
        {
            try { return (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
            catch { return null; }
        }
    }
}
