using System;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace SrcCadWorks.Services
{
    internal static class ComUtil
    {
        public static void Release(object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); } catch { }
        }

        public static void FinalRelease(object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
        }

        public static bool SafeVisible(ModelDoc2 doc)
        {
            if (doc == null) return false;
            try { return doc.Visible; } catch { return false; }
        }

        // SrcCadWorks'in kendisinin açtığı veya işlem öncesinde görünür olmayan belgeyi işlem biter bitmez
        // CloseDoc ile kapatır. CloseDoc dirty belgeleri diske kaydetmeden kapatır ve referans olarak
        // montaj belleğinde tutulan belgelerin UI kaynaklarını da serbest bırakabilir.
        public static bool CloseTransientDocument(SldWorks app, ModelDoc2 doc, string sourcePath, bool openedByUs, bool wasVisibleBefore)
        {
            if (app == null || doc == null) return true;
            if (!openedByUs && wasVisibleBefore) return true; // Kullanıcının zaten açık sekmesine dokunma.
            return CloseOpenedBySrc(app, doc, sourcePath);
        }

        // SrcCadWorks'in geçici belgesini işlem biter bitmez kapatır ve kapanmayı doğrular.
        // Modal PDM/CAM uyarısı ilk CloseDoc turunu geciktirirse kısa aralıklarla yeniden dener.
        public static bool CloseOpenedBySrc(SldWorks app, ModelDoc2 doc, string sourcePath)
        {
            if (app == null || doc == null) return true;
            string title = "";
            try { title = doc.GetTitle() ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(title)) return true;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                try { app.CloseDoc(title); } catch { }
                PumpMessages(20 + attempt * 15);

                ModelDoc2 stillOpen = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                        stillOpen = app.GetOpenDocumentByName(sourcePath) as ModelDoc2;
                }
                catch { }

                if (stillOpen == null) return true;

                // Referans belge montaj belleğinde kalabilir. Esas kritik olan görünür UI sekmesinin kapanmasıdır.
                bool visible = SafeVisible(stillOpen);
                if (!visible)
                {
                    if (!object.ReferenceEquals(stillOpen, doc)) Release(stillOpen);
                    return true;
                }

                if (!object.ReferenceEquals(stillOpen, doc)) Release(stillOpen);
            }

            Logger.Info("SrcCadWorks belgesi görünür olarak kapanamadı: " + (sourcePath ?? title));
            return false;
        }

        public static void PumpMessages(int milliseconds)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                int slice = Math.Min(10, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        public static void ManagedCleanup(bool aggressive)
        {
            try
            {
                if (aggressive)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                else
                {
                    GC.Collect(0, GCCollectionMode.Optimized);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Global ESC monitor used while SOLIDWORKS COM calls are running. Form-level KeyDown
    /// events are not reliable when a form is disabled or SOLIDWORKS temporarily owns focus,
    /// so GetAsyncKeyState is polled at safe boundaries between component/document operations.
    /// If ESC was already held when the operation started, the monitor arms only after release.
    /// </summary>
    internal sealed class EscapeCancellationMonitor : IDisposable
    {
        private const int VkEscape = 0x1B;
        private volatile bool _armed;
        private volatile bool _requested;
        private volatile bool _disposed;
        private Timer _pollTimer;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public EscapeCancellationMonitor()
        {
            // Avoid treating a key held during the start button click as a cancellation.
            // The timer captures quick ESC taps even while the UI thread is blocked inside
            // a SOLIDWORKS/Excel COM call; the operation stops at its next safe boundary.
            _armed = !IsEscapeDown();
            _pollTimer = new Timer(PollEscapeState, null, 10, 10);
        }

        public bool IsCancellationRequested
        {
            get
            {
                PollEscapeState(null);
                return _requested;
            }
        }

        public void ThrowIfRequested(string message = "İşlem ESC ile iptal edildi.")
        {
            if (IsCancellationRequested) throw new OperationCanceledException(message);
        }

        private void PollEscapeState(object state)
        {
            if (_disposed || _requested) return;
            bool down = IsEscapeDown();
            if (!_armed)
            {
                if (!down) _armed = true;
                return;
            }
            if (down) _requested = true;
        }

        private static bool IsEscapeDown()
        {
            try { return (GetAsyncKeyState(VkEscape) & 0x8000) != 0; }
            catch { return false; }
        }

        public void Dispose()
        {
            _disposed = true;
            Timer timer = Interlocked.Exchange(ref _pollTimer, null);
            try { timer?.Dispose(); } catch { }
        }
    }

}
