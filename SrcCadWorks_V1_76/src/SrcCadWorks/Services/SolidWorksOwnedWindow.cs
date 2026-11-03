using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// SrcCadWorks "Pencereyi üstte tut" implementation.
    ///
    /// A SrcCadWorks form is a genuine owned top-level window of the SOLIDWORKS
    /// main frame. This is the Windows-native relationship required here:
    /// - the owned form remains above its SOLIDWORKS owner while SW documents change;
    /// - no global TopMost flag is used;
    /// - another application can naturally cover both SOLIDWORKS and SrcCadWorks.
    ///
    /// The SOLIDWORKS main-frame HWND is cached before SrcCadWorks modeless forms are
    /// shown, using IFrame.GetHWndx64(). This avoids Process.MainWindowHandle ever
    /// accidentally resolving to a SrcCadWorks form.
    /// </summary>
    internal static class SolidWorksOwnedWindow
    {
        private const int GWLP_HWNDPARENT = -8;

        private static readonly object Sync = new object();
        private static IntPtr _solidWorksMainHwnd = IntPtr.Zero;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private static IntPtr SetNativeOwner(IntPtr hwnd, IntPtr owner)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hwnd, GWLP_HWNDPARENT, owner);

            return new IntPtr(SetWindowLong32(hwnd, GWLP_HWNDPARENT, owner.ToInt32()));
        }

        public static void Initialize(SldWorks swApp)
        {
            lock (Sync)
            {
                IntPtr hwnd = ResolveSolidWorksMainWindow(swApp);
                if (hwnd != IntPtr.Zero)
                    _solidWorksMainHwnd = hwnd;
            }
        }

        public static void Reset()
        {
            lock (Sync) _solidWorksMainHwnd = IntPtr.Zero;
        }

        public static IntPtr GetSolidWorksMainWindow(SldWorks swApp)
        {
            lock (Sync)
            {
                if (_solidWorksMainHwnd != IntPtr.Zero && IsWindow(_solidWorksMainHwnd))
                    return _solidWorksMainHwnd;

                _solidWorksMainHwnd = ResolveSolidWorksMainWindow(swApp);
                return _solidWorksMainHwnd;
            }
        }

        public static void Show(Form form, SldWorks swApp, bool keepAboveSolidWorks)
        {
            if (form == null || form.IsDisposed) return;

            // Important: managed TopMost is ALWAYS off.
            form.TopMost = false;

            IntPtr swHwnd = keepAboveSolidWorks ? GetSolidWorksMainWindow(swApp) : IntPtr.Zero;
            if (swHwnd != IntPtr.Zero)
                form.Show(new NativeOwner(swHwnd));
            else
                form.Show();

            Apply(form, swApp, keepAboveSolidWorks, false);
        }

        public static void Apply(Form form, bool keepAboveSolidWorks)
        {
            Apply(form, null, keepAboveSolidWorks, false);
        }

        public static void Apply(Form form, SldWorks swApp, bool keepAboveSolidWorks, bool activate = false)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;

            try { form.TopMost = false; } catch { }

            IntPtr owner = keepAboveSolidWorks ? GetSolidWorksMainWindow(swApp) : IntPtr.Zero;
            if (owner == form.Handle) owner = IntPtr.Zero;

            try { SetNativeOwner(form.Handle, owner); } catch { }

            if (activate)
            {
                try
                {
                    form.BringToFront();
                    form.Activate();
                }
                catch { }
            }
        }

        public static Tracker Track(Form form, SldWorks swApp, bool enabled)
        {
            if (form == null || form.IsDisposed) return null;
            return new Tracker(form, swApp, enabled);
        }

        internal sealed class Tracker : IDisposable
        {
            private readonly Form _form;
            private readonly SldWorks _swApp;
            private bool _enabled;
            private int _suspendCount;
            private bool _disposed;

            internal Tracker(Form form, SldWorks swApp, bool enabled)
            {
                _form = form;
                _swApp = swApp;
                _enabled = enabled;

                _form.HandleCreated += Form_HandleCreated;
                _form.Shown += Form_Shown;
                ApplyOwner(false);
            }

            private void Form_HandleCreated(object sender, EventArgs e)
            {
                ApplyOwner(false);
            }

            private void Form_Shown(object sender, EventArgs e)
            {
                ApplyOwner(false);
            }

            public void SetEnabled(bool enabled)
            {
                if (_disposed) return;
                _enabled = enabled;
                ApplyOwner(false);
            }

            private void ApplyOwner(bool activate)
            {
                if (_disposed || _form == null || _form.IsDisposed || !_form.IsHandleCreated)
                    return;

                bool attach = _enabled && _suspendCount == 0;
                Apply(_form, _swApp, attach, activate);
            }

            public IDisposable Suspend()
            {
                if (_disposed) return null;
                _suspendCount++;
                ApplyOwner(false);
                return new Suspension(this);
            }

            private void Resume()
            {
                if (_suspendCount > 0) _suspendCount--;
                ApplyOwner(false);
            }

            private sealed class Suspension : IDisposable
            {
                private Tracker _tracker;
                public Suspension(Tracker tracker) { _tracker = tracker; }

                public void Dispose()
                {
                    var tracker = _tracker;
                    _tracker = null;
                    tracker?.Resume();
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try { _form.HandleCreated -= Form_HandleCreated; } catch { }
                try { _form.Shown -= Form_Shown; } catch { }

                try
                {
                    if (_form != null && !_form.IsDisposed && _form.IsHandleCreated)
                    {
                        _form.TopMost = false;
                        SetNativeOwner(_form.Handle, IntPtr.Zero);
                    }
                }
                catch { }
            }
        }

        private sealed class NativeOwner : IWin32Window
        {
            public NativeOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        private static IntPtr ResolveSolidWorksMainWindow(SldWorks swApp)
        {
            if (swApp == null) return IntPtr.Zero;

            try
            {
                object frameObject = swApp.Frame();
                var frame = frameObject as IFrame;
                if (frame != null)
                {
                    long hwnd64 = frame.GetHWndx64();
                    if (hwnd64 != 0)
                        return new IntPtr(hwnd64);
                }
            }
            catch (Exception ex)
            {
                Logger.Info("SOLIDWORKS IFrame.GetHWndx64 okunamadı: " + ex.Message);
            }

            // Deliberately no Process.MainWindowHandle fallback:
            // once a SrcCadWorks form is active that API can identify the wrong top-level window.
            return IntPtr.Zero;
        }
    }
}
