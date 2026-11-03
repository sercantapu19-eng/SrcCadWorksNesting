using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SrcCadWorks.Viewer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            DpiBootstrap.EnablePerMonitorV2();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args == null || args.Length == 0 || !File.Exists(args[0]))
            {
                MessageBox.Show("Montaj görüntüleme verisi bulunamadı.", "SrcCadWorks");
                return;
            }

            int parentPid = ReadIntArg(args, "--parent-pid");
            StartIndependentHostGuard(parentPid);
            try { Application.Run(new ViewerMainForm(args[0], parentPid)); }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "SrcCadWorks Montaj Görüntüleme"); }
        }

        private static void StartIndependentHostGuard(int parentPid)
        {
            if (parentPid <= 0) return;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(750);
                    bool alive = false;
                    try
                    {
                        using (var parent = Process.GetProcessById(parentPid))
                            alive = !parent.HasExited;
                    }
                    catch { alive = false; }

                    if (alive) continue;

                    // UI/eDrawings COM takılı olsa bile eski sürüm klasörünü kilitlemeyelim.
                    try { Process.GetCurrentProcess().Kill(); }
                    catch { try { Environment.Exit(0); } catch { } }
                    return;
                }
            })
            {
                IsBackground = true,
                Name = "SrcCadWorks.Viewer.HostGuard"
            };
            thread.Start();
        }


        private static int ReadIntArg(string[] args, string name)
        {
            if (args == null || string.IsNullOrWhiteSpace(name)) return 0;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                int value;
                if (int.TryParse(args[i + 1], out value)) return value;
            }
            return 0;
        }

        private static class DpiBootstrap
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4.
            // Viewer ayrı bir EXE olduğu için bu çağrı WinForms başlamadan önce yapılır.
            private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

            internal static void EnablePerMonitorV2()
            {
                try
                {
                    SetProcessDpiAwarenessContext(PerMonitorAwareV2);
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
            }
        }

    }
}
