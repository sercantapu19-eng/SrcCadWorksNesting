using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// SOLIDWORKS API calls such as OpenDoc6/ActivateDoc3/LoadFile4 can bring the
    /// SOLIDWORKS frame to the foreground even when the user is working in Chrome,
    /// Explorer, Outlook, etc. Capture the external foreground immediately before
    /// such a call and restore it only if SOLIDWORKS stole foreground.
    ///
    /// If the user has already switched to some other external application, no action
    /// is taken; we never override a newer external foreground choice.
    /// </summary>
    internal sealed class ForegroundWindowGuard : IDisposable
    {
        private const int SW_RESTORE = 9;
        private readonly IntPtr _externalWindow;
        private readonly uint _solidWorksPid;
        private bool _disposed;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private ForegroundWindowGuard(IntPtr externalWindow, uint solidWorksPid)
        {
            _externalWindow = externalWindow;
            _solidWorksPid = solidWorksPid;
        }

        public static ForegroundWindowGuard CaptureExternal()
        {
            uint swPid = (uint)Process.GetCurrentProcess().Id;
            IntPtr foreground = IntPtr.Zero;
            try { foreground = GetForegroundWindow(); } catch { }

            if (foreground == IntPtr.Zero)
                return new ForegroundWindowGuard(IntPtr.Zero, swPid);

            try
            {
                uint pid;
                GetWindowThreadProcessId(foreground, out pid);
                if (pid == swPid)
                    foreground = IntPtr.Zero;
            }
            catch { foreground = IntPtr.Zero; }

            return new ForegroundWindowGuard(foreground, swPid);
        }

        public void RestoreIfSolidWorksStoleFocus()
        {
            if (_externalWindow == IntPtr.Zero || !IsWindow(_externalWindow)) return;

            IntPtr current = IntPtr.Zero;
            try { current = GetForegroundWindow(); } catch { }
            if (current == IntPtr.Zero || current == _externalWindow) return;

            // Only restore when SOLIDWORKS/SrcCadWorks currently owns foreground.
            // If the user moved to another external application meanwhile, leave it alone.
            try
            {
                uint currentPid;
                GetWindowThreadProcessId(current, out currentPid);
                if (currentPid != _solidWorksPid) return;
            }
            catch { return; }

            try { if (IsIconic(_externalWindow)) ShowWindowAsync(_externalWindow, SW_RESTORE); } catch { }
            try { SetForegroundWindow(_externalWindow); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RestoreIfSolidWorksStoleFocus();
        }
    }
}
