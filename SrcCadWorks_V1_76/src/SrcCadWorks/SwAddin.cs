using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SrcCadWorks.Services;
using SrcCadWorks.UI;

namespace SrcCadWorks
{
    [ComVisible(true)]
    [Guid("7DA13345-7F3B-4FB4-B9A9-E3BA3867A64C")]
    public sealed class SwAddin : SolidWorks.Interop.swpublished.SwAddin
    {
        private const int CommandGroupId = 7120;
        private const int LaserCommandId = 7121;
        private const int WeldCommandId = 7122;
        private const int ViewerCommandId = 7123;
        private const int ManufacturingCommandId = 7124;
        private const int MachiningCommandId = 7125;
        private const int AutomaticDrawingCommandId = 7126;
        private SldWorks _swApp;
        private CommandManager _commandManager;
        private int _addinCookie;
        private TaskpaneView _taskPaneView;
        private TaskPaneControl _taskPaneControl;
        private LaserPrepForm _laserPrepForm;
        private WeldPrepForm _weldPrepForm;
        private AssemblyPrepForm _assemblyPrepForm;
        private ManufacturingPrepForm _manufacturingPrepForm;
        private MachiningPrepForm _machiningPrepForm;
        private SmartNestForm _smartNestForm;
        private AutomaticDrawingForm _automaticDrawingForm;
        private System.Windows.Forms.Timer _taskPaneGuardTimer;
        private bool _taskPaneDestroyObserved;
        private bool _suppressTaskPaneRecreate;
        private readonly List<Process> _viewerProcesses = new List<Process>();
        private readonly Dictionary<int, string> _viewerRuntimeDirs = new Dictionary<int, string>();

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                CleanupStaleHelperProcesses();
                _swApp = (SldWorks)ThisSW;
                SolidWorksOwnedWindow.Initialize(_swApp);
                _addinCookie = Cookie;
                _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);
                EnableReadOnlyNoPromptPreference();
                _commandManager = _swApp.GetCommandManager(_addinCookie);
                CreateCommands();
                _suppressTaskPaneRecreate = false;
                _taskPaneDestroyObserved = false;
                CreateTaskPane();
                StartTaskPaneGuard();
                Logger.Info("SrcCadWorks SolidWorks'e bağlandı.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ConnectToSW başarısız", ex);
                return false;
            }
        }

        private static void CleanupStaleHelperProcesses()
        {
            string[] names = { "SrcCadWorks.Viewer", "SrcCadWorks.Watchdog" };
            foreach (string name in names)
            {
                Process[] processes = null;
                try { processes = Process.GetProcessesByName(name); } catch { }
                if (processes == null) continue;

                foreach (Process process in processes)
                {
                    try
                    {
                        if (process == null || process.Id == Process.GetCurrentProcess().Id) continue;
                        if (!process.HasExited)
                        {
                            try { process.Kill(); } catch { }
                            try { process.WaitForExit(1200); } catch { }
                        }
                    }
                    catch { }
                    finally { try { process?.Dispose(); } catch { } }
                }
            }
        }

        public bool DisconnectFromSW()
        {
            _suppressTaskPaneRecreate = true;
            try
            {
                if (_taskPaneGuardTimer != null)
                {
                    _taskPaneGuardTimer.Stop();
                    _taskPaneGuardTimer.Dispose();
                    _taskPaneGuardTimer = null;
                }
            }
            catch { }
            try
            {
                if (_laserPrepForm != null && !_laserPrepForm.IsDisposed)
                {
                    _laserPrepForm.FormClosed -= LaserPrepForm_FormClosed;
                    _laserPrepForm.Close();
                    _laserPrepForm.Dispose();
                }
            }
            catch { }
            _laserPrepForm = null;
            try
            {
                if (_weldPrepForm != null && !_weldPrepForm.IsDisposed)
                {
                    _weldPrepForm.FormClosed -= WeldPrepForm_FormClosed;
                    _weldPrepForm.Close();
                    _weldPrepForm.Dispose();
                }
            }
            catch { }
            _weldPrepForm = null;
            try
            {
                if (_assemblyPrepForm != null && !_assemblyPrepForm.IsDisposed)
                {
                    _assemblyPrepForm.FormClosed -= AssemblyPrepForm_FormClosed;
                    _assemblyPrepForm.Close();
                    _assemblyPrepForm.Dispose();
                }
            }
            catch { }
            _assemblyPrepForm = null;
            try
            {
                if (_manufacturingPrepForm != null && !_manufacturingPrepForm.IsDisposed)
                {
                    _manufacturingPrepForm.FormClosed -= ManufacturingPrepForm_FormClosed;
                    _manufacturingPrepForm.Close();
                    _manufacturingPrepForm.Dispose();
                }
            }
            catch { }
            _manufacturingPrepForm = null;
            try
            {
                if (_machiningPrepForm != null && !_machiningPrepForm.IsDisposed)
                {
                    _machiningPrepForm.FormClosed -= MachiningPrepForm_FormClosed;
                    _machiningPrepForm.Close();
                    _machiningPrepForm.Dispose();
                }
            }
            catch { }
            _machiningPrepForm = null;
            try
            {
                if (_automaticDrawingForm != null && !_automaticDrawingForm.IsDisposed)
                {
                    _automaticDrawingForm.FormClosed -= AutomaticDrawingForm_FormClosed;
                    _automaticDrawingForm.Close();
                    _automaticDrawingForm.Dispose();
                }
            }
            catch { }
            _automaticDrawingForm = null;
            // SMARTNEST penceresi de add-in bağlantısı kesilirken kapatılmalı.
            // Önceki sürümde bu form DisconnectFromSW akışında atlanıyordu;
            // açık form ve olay abonelikleri add-in kaynaklarının serbest kalmasını geciktirebilirdi.
            try
            {
                if (_smartNestForm != null && !_smartNestForm.IsDisposed)
                {
                    _smartNestForm.Close();
                    _smartNestForm.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("SMARTNEST kapatılırken kaynak temizliği başarısız", ex);
            }
            finally
            {
                _smartNestForm = null;
            }
            CloseManagedViewers();
            try
            {
                if (_taskPaneView != null)
                {
                    try { _taskPaneView.TaskPaneDestroyNotify -= OnTaskPaneDestroyNotify; } catch { }
                    _taskPaneView.DeleteView();
                }
            }
            catch { }
            try
            {
                if (_taskPaneControl != null) _taskPaneControl.Dispose();
            }
            catch { }
            try
            {
                if (_commandManager != null) _commandManager.RemoveCommandGroup(CommandGroupId);
            }
            catch { }
            _taskPaneControl = null;
            _taskPaneView = null;
            _commandManager = null;
            SolidWorksOwnedWindow.Reset();
            _swApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        public void OnLaserPrepare()
        {
            try
            {
                // Modeless pencere: SrcCadWorks açıkken kullanıcı SOLIDWORKS model alanına geri dönüp
                // döndürme, zoom, seçim vb. işlemleri yapabilir. Aynı pencereden ikinci kopya açılmaz.
                if (_laserPrepForm != null && !_laserPrepForm.IsDisposed)
                {
                    if (_laserPrepForm.RestoreFromTaskPane()) return;

                    // A stale non-busy window should never block reopening the module.
                    // If production is still active, keep the existing instance and report the issue
                    // rather than creating two automation sessions at the same time.
                    if (_laserPrepForm.IsBusyForWindowRecovery)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            "Lazer Hazırla işlemi arka planda hâlâ çalışıyor ancak pencere geri getirilemedi. İşlem tamamlandığında modülü tekrar açabilirsiniz.",
                            "SrcCadWorks");
                        return;
                    }

                    try { _laserPrepForm.FormClosed -= LaserPrepForm_FormClosed; } catch { }
                    try { _laserPrepForm.Dispose(); } catch { }
                    _laserPrepForm = null;
                }

                _laserPrepForm = new LaserPrepForm(_swApp);
                _laserPrepForm.FormClosed += LaserPrepForm_FormClosed;
                SolidWorksOwnedWindow.Show(_laserPrepForm, _swApp, _laserPrepForm.KeepAboveSolidWorksRequested);
            }
            catch (Exception ex)
            {
                Logger.Error("Lazer Hazırla ekranı açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void LaserPrepForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
        {
            try { _taskPaneControl?.RefreshDocumentStatus(); } catch { }
            try
            {
                if (_laserPrepForm != null) _laserPrepForm.FormClosed -= LaserPrepForm_FormClosed;
            }
            catch { }
            _laserPrepForm = null;
        }

        public void OnWeldPrepare()
        {
            try
            {
                if (_weldPrepForm != null && !_weldPrepForm.IsDisposed)
                {
                    if (!_weldPrepForm.Visible)
                        SolidWorksOwnedWindow.Show(_weldPrepForm, _swApp, _weldPrepForm.KeepAboveSolidWorksRequested);
                    if (_weldPrepForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                        _weldPrepForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
                    _weldPrepForm.BringToFront();
                    _weldPrepForm.Activate();
                    return;
                }
                _weldPrepForm = new WeldPrepForm(_swApp);
                _weldPrepForm.FormClosed += WeldPrepForm_FormClosed;
                SolidWorksOwnedWindow.Show(_weldPrepForm, _swApp, _weldPrepForm.KeepAboveSolidWorksRequested);
            }
            catch (Exception ex)
            {
                Logger.Error("Kaynak Hazırla ekranı açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void WeldPrepForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
        {
            try { _taskPaneControl?.RefreshDocumentStatus(); } catch { }
            try { if (_weldPrepForm != null) _weldPrepForm.FormClosed -= WeldPrepForm_FormClosed; } catch { }
            _weldPrepForm = null;
        }

        public void OnAssemblyPrepare()
        {
            try
            {
                if (_assemblyPrepForm != null && !_assemblyPrepForm.IsDisposed)
                {
                    if (!_assemblyPrepForm.Visible)
                        SolidWorksOwnedWindow.Show(_assemblyPrepForm, _swApp, _assemblyPrepForm.KeepAboveSolidWorksRequested);
                    if (_assemblyPrepForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                        _assemblyPrepForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
                    _assemblyPrepForm.BringToFront();
                    _assemblyPrepForm.Activate();
                    return;
                }
                _assemblyPrepForm = new AssemblyPrepForm(_swApp);
                _assemblyPrepForm.FormClosed += AssemblyPrepForm_FormClosed;
                SolidWorksOwnedWindow.Show(_assemblyPrepForm, _swApp, _assemblyPrepForm.KeepAboveSolidWorksRequested);
            }
            catch (Exception ex)
            {
                Logger.Error("Montaj Dosyası Hazırla ekranı açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void AssemblyPrepForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
        {
            try { _taskPaneControl?.RefreshDocumentStatus(); } catch { }
            try { if (_assemblyPrepForm != null) _assemblyPrepForm.FormClosed -= AssemblyPrepForm_FormClosed; } catch { }
            _assemblyPrepForm = null;
        }

        public void OnManufacturingPrepare()
        {
            try
            {
                if (_manufacturingPrepForm != null && !_manufacturingPrepForm.IsDisposed)
                {
                    if (!_manufacturingPrepForm.Visible)
                        SolidWorksOwnedWindow.Show(_manufacturingPrepForm, _swApp, _manufacturingPrepForm.KeepAboveSolidWorksRequested);
                    if (_manufacturingPrepForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                        _manufacturingPrepForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
                    _manufacturingPrepForm.BringToFront();
                    _manufacturingPrepForm.Activate();
                    return;
                }
                _manufacturingPrepForm = new ManufacturingPrepForm(_swApp);
                _manufacturingPrepForm.FormClosed += ManufacturingPrepForm_FormClosed;
                SolidWorksOwnedWindow.Show(_manufacturingPrepForm, _swApp, _manufacturingPrepForm.KeepAboveSolidWorksRequested);
            }
            catch (Exception ex)
            {
                Logger.Error("İmalat Dosyası Hazırla ekranı açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void ManufacturingPrepForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
        {
            try { _taskPaneControl?.RefreshDocumentStatus(); } catch { }
            try { if (_manufacturingPrepForm != null) _manufacturingPrepForm.FormClosed -= ManufacturingPrepForm_FormClosed; } catch { }
            _manufacturingPrepForm = null;
        }

        public void OnSmartNest()
        {
            try
            {
                if (_smartNestForm != null && !_smartNestForm.IsDisposed) { _smartNestForm.BringToFront(); _smartNestForm.Activate(); return; }
                _smartNestForm = new SmartNestForm(_swApp);
                _smartNestForm.FormClosed += (s,e) => { _smartNestForm = null; try { _taskPaneControl?.RefreshDocumentStatus(); } catch { } };
                SolidWorksOwnedWindow.Show(_smartNestForm, _swApp, true);
            }
            catch (Exception ex) { Logger.Error("SMARTNEST açılamadı", ex); System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks SMARTNEST"); }
        }

        public void OnMachiningPrepare()
        {
            try
            {
                if (_machiningPrepForm != null && !_machiningPrepForm.IsDisposed)
                {
                    if (!_machiningPrepForm.Visible)
                        SolidWorksOwnedWindow.Show(_machiningPrepForm, _swApp, _machiningPrepForm.KeepAboveSolidWorksRequested);
                    if (_machiningPrepForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                        _machiningPrepForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
                    _machiningPrepForm.BringToFront();
                    _machiningPrepForm.Activate();
                    return;
                }
                _machiningPrepForm = new MachiningPrepForm(_swApp);
                _machiningPrepForm.FormClosed += MachiningPrepForm_FormClosed;
                SolidWorksOwnedWindow.Show(_machiningPrepForm, _swApp, _machiningPrepForm.KeepAboveSolidWorksRequested);
            }
            catch (Exception ex)
            {
                Logger.Error("Talaşlı İmalat Dosyası Hazırla ekranı açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void MachiningPrepForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
        {
            try { _taskPaneControl?.RefreshDocumentStatus(); } catch { }
            try { if (_machiningPrepForm != null) _machiningPrepForm.FormClosed -= MachiningPrepForm_FormClosed; } catch { }
            _machiningPrepForm = null;
        }

        public void OnAutomaticDrawing()
        {
            try
            {
                var doc = _swApp?.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART)
                    throw new InvalidOperationException("Otomatik Teknik Resim için aktif bir SLDPRT açın.");

                if (_automaticDrawingForm != null && !_automaticDrawingForm.IsDisposed)
                {
                    if (!_automaticDrawingForm.Visible)
                        SolidWorksOwnedWindow.Show(_automaticDrawingForm, _swApp, _automaticDrawingForm.KeepAboveSolidWorksRequested);
                    if (_automaticDrawingForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                        _automaticDrawingForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
                    _automaticDrawingForm.BringToFront();
                    _automaticDrawingForm.Activate();
                    return;
                }

                _automaticDrawingForm = new AutomaticDrawingForm(_swApp);
                _automaticDrawingForm.FormClosed += AutomaticDrawingForm_FormClosed;
                SolidWorksOwnedWindow.Show(_automaticDrawingForm, _swApp, _automaticDrawingForm.KeepAboveSolidWorksRequested);
            }
            catch (Exception ex)
            {
                Logger.Error("Otomatik Teknik Resim ekranı açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
        }

        private void AutomaticDrawingForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
        {
            try { _taskPaneControl?.RefreshDocumentStatus(); } catch { }
            try { if (_automaticDrawingForm != null) _automaticDrawingForm.FormClosed -= AutomaticDrawingForm_FormClosed; } catch { }
            _automaticDrawingForm = null;
        }

        public void OnAssemblyViewer()
        {
            EscapeCancellationMonitor escapeMonitor = null;
            try
            {
                var doc = _swApp?.ActiveDoc as ModelDoc2;
                if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    throw new InvalidOperationException("Montaj Görüntüleme için aktif bir SLDASM açın.");
                escapeMonitor = new EscapeCancellationMonitor();
                var snapshotService = new AssemblyViewerSnapshotService(_swApp);
                string snapshot = snapshotService.CreateLoadingSnapshotFile();

                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";

                // Viewer artık sürüm/bin klasöründen çalıştırılmaz. Yardımcı süreç açık kalsa bile
                // kullanıcının eski SrcCadWorks sürüm klasörünü silebilmesi için LOCALAPPDATA
                // altındaki geçici runtime kopyasından başlatılır.
                CleanupViewerProcesses();
                string viewerRuntimeDir = PrepareViewerRuntime(baseDir);
                string viewerExe = Path.Combine(viewerRuntimeDir, "SrcCadWorks.Viewer.exe");
                var viewerProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = viewerExe,
                    Arguments = "\"" + snapshot + "\" --parent-pid " + Process.GetCurrentProcess().Id,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = viewerRuntimeDir
                });
                if (viewerProcess != null)
                {
                    _viewerProcesses.Add(viewerProcess);
                    try { _viewerRuntimeDirs[viewerProcess.Id] = viewerRuntimeDir; } catch { }
                }
                else
                {
                    TryDeleteDirectory(viewerRuntimeDir);
                }

                try
                {
                    snapshotService.CreateSnapshot(() => escapeMonitor.IsCancellationRequested, snapshot);
                }
                catch (OperationCanceledException)
                {
                    snapshotService.MarkLiveSnapshotStopped(snapshot, "ESC ile tarama durduruldu.");
                    throw;
                }
                catch (Exception ex)
                {
                    snapshotService.MarkLiveSnapshotStopped(snapshot, "HATA: " + ex.Message);
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Montaj Görüntüleme hazırlığı ESC ile durduruldu.");
            }
            catch (Exception ex)
            {
                Logger.Error("Montaj Görüntüleme açılamadı", ex);
                System.Windows.Forms.MessageBox.Show(ex.Message, "SrcCadWorks");
            }
            finally
            {
                try { escapeMonitor?.Dispose(); } catch { }
            }
        }

        private void CleanupViewerProcesses()
        {
            for (int i = _viewerProcesses.Count - 1; i >= 0; i--)
            {
                Process process = _viewerProcesses[i];
                int pid = 0;
                try { pid = process == null ? 0 : process.Id; } catch { }
                try
                {
                    if (process == null || process.HasExited)
                    {
                        try { process?.Dispose(); } catch { }
                        _viewerProcesses.RemoveAt(i);
                        CleanupViewerRuntime(pid);
                    }
                }
                catch
                {
                    try { process?.Dispose(); } catch { }
                    _viewerProcesses.RemoveAt(i);
                    CleanupViewerRuntime(pid);
                }
            }
        }

        private void CloseManagedViewers()
        {
            for (int i = _viewerProcesses.Count - 1; i >= 0; i--)
            {
                Process process = _viewerProcesses[i];
                int pid = 0;
                try { pid = process == null ? 0 : process.Id; } catch { }
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        try { process.CloseMainWindow(); } catch { }
                        try
                        {
                            if (!process.WaitForExit(900))
                                process.Kill();
                        }
                        catch
                        {
                            try { if (!process.HasExited) process.Kill(); } catch { }
                        }
                    }
                }
                catch { }
                finally
                {
                    try { process?.Dispose(); } catch { }
                    CleanupViewerRuntime(pid);
                }
            }
            _viewerProcesses.Clear();
            _viewerRuntimeDirs.Clear();
        }

        private string PrepareViewerRuntime(string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                throw new InvalidOperationException("SrcCadWorks çalışma klasörü bulunamadı.");

            string sourceExe = Path.Combine(sourceDirectory, "SrcCadWorks.Viewer.exe");
            if (!File.Exists(sourceExe))
                throw new FileNotFoundException("SrcCadWorks.Viewer.exe bulunamadı. Çözümü Release + x64 ile yeniden derleyin.", sourceExe);

            string root = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SrcCadWorks", "viewer-runtime");
            Directory.CreateDirectory(root);
            CleanupOldViewerRuntimeDirectories(root);

            string runtimeDir = Path.Combine(root,
                Process.GetCurrentProcess().Id.ToString() + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtimeDir);

            string[] runtimeFiles =
            {
                "SrcCadWorks.Viewer.exe",
                "SrcCadWorks.Viewer.exe.config",
                "SolidWorks.Interop.sldworks.dll",
                "SolidWorks.Interop.swconst.dll"
            };

            foreach (string name in runtimeFiles)
            {
                string source = Path.Combine(sourceDirectory, name);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(runtimeDir, name), true);
            }

            if (!File.Exists(Path.Combine(runtimeDir, "SrcCadWorks.Viewer.exe")))
                throw new FileNotFoundException("Viewer runtime kopyası oluşturulamadı.");

            return runtimeDir;
        }

        private void CleanupViewerRuntime(int pid)
        {
            if (pid <= 0) return;
            string dir;
            if (!_viewerRuntimeDirs.TryGetValue(pid, out dir)) return;
            _viewerRuntimeDirs.Remove(pid);
            TryDeleteDirectory(dir);
        }

        private static void CleanupOldViewerRuntimeDirectories(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        DateTime last = Directory.GetLastWriteTimeUtc(dir);
                        if (DateTime.UtcNow - last > TimeSpan.FromHours(12))
                            Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }

        public int CanWeldPrepare() => CanLaserPrepare();
        public int CanManufacturingPrepare() => CanLaserPrepare();
        public int CanMachiningPrepare() => CanLaserPrepare();
        public int CanAssemblyViewer() => CanLaserPrepare();

        public int CanAutomaticDrawing()
        {
            try
            {
                var doc = _swApp?.ActiveDoc as ModelDoc2;
                return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART ? 1 : 0;
            }
            catch { return 0; }
        }

        public int CanLaserPrepare()
        {
            try
            {
                var doc = _swApp?.ActiveDoc as ModelDoc2;
                return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
            }
            catch { return 0; }
        }

        private void CreateCommands()
        {
            int errors = 0;
            var group = _commandManager.CreateCommandGroup2(CommandGroupId, "SrcCadWorks Üretim", "SrcCadWorks", "SrcCadWorks", -1, true, ref errors);
            group.AddCommandItem2("Otomatik Teknik Resim", -1,
                "Aktif SLDPRT için şablondan ön/sağ/üst/izometrik görünüşler; sac parçada Flat Pattern ve ölçülendirme oluştur",
                "Otomatik Teknik Resim", 2, nameof(OnAutomaticDrawing), nameof(CanAutomaticDrawing), AutomaticDrawingCommandId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            group.AddCommandItem2("Lazer Dosyası Hazırla", -1,
                "Montajı tara, parça koduyla DXF/PDF oluştur ve Lazer Talep Formunu doldur",
                "Lazer Dosyası Hazırla", 0, nameof(OnLaserPrepare), nameof(CanLaserPrepare), LaserCommandId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            group.AddCommandItem2("Kaynak Dosyası Hazırla", -1,
                "Ana montajı tara, KAYNAK işlem tiplerini ve reçete alt parçalarını çıkar, PDF ve Kaynak Proses Formunu oluştur",
                "Kaynak Dosyası Hazırla", 1, nameof(OnWeldPrepare), nameof(CanWeldPrepare), WeldCommandId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            group.AddCommandItem2("İmalat Dosyası Hazırla", -1,
                "İşlem Tipi TESTERE olan parçaları toplar, Weldment profil tipini okur, PDF ve İmalat Kontrol Formu oluşturur",
                "İmalat Dosyası Hazırla", 2, nameof(OnManufacturingPrepare), nameof(CanManufacturingPrepare), ManufacturingCommandId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            group.AddCommandItem2("Talaşlı İmalat Dosyası Hazırla", -1,
                "TALAŞLI İMALAT köklerini ve tüm alt seviyelerini gruplar, PDF ve seçili STEP çıktıları oluşturur",
                "Talaşlı İmalat Dosyası Hazırla", 2, nameof(OnMachiningPrepare), nameof(CanMachiningPrepare), MachiningCommandId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            group.AddCommandItem2("Montaj Görüntüle", -1,
                "Aktif montajı eDrawings ile hızlı montaj/parça görüntüleme",
                "Montaj Görüntüle", 2, nameof(OnAssemblyViewer), nameof(CanAssemblyViewer), ViewerCommandId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            group.HasMenu = true;
            group.HasToolbar = true;
            group.Activate();
        }

        private void StartTaskPaneGuard()
        {
            try
            {
                if (_taskPaneGuardTimer != null)
                {
                    _taskPaneGuardTimer.Stop();
                    _taskPaneGuardTimer.Dispose();
                }
                _taskPaneGuardTimer = new System.Windows.Forms.Timer { Interval = 2500 };
                _taskPaneGuardTimer.Tick += (s, e) => EnsureTaskPaneAlive();
                _taskPaneGuardTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Info("Task Pane koruma zamanlayıcısı başlatılamadı: " + ex.Message);
            }
        }

        private int OnTaskPaneDestroyNotify()
        {
            // SOLIDWORKS bazen başka bir Task Pane eklentisi açılıp/kapatılırken
            // uygulama seviyesindeki Task Pane görünümlerini yeniden oluşturur.
            // COM callback içinde yeni view yaratmak yerine yalnız işaret bırakıyoruz;
            // WinForms timer bir sonraki tikte SrcCadWorks sekmesini güvenle yeniler.
            if (!_suppressTaskPaneRecreate)
            {
                _taskPaneDestroyObserved = true;
                Logger.Info("SOLIDWORKS SrcCadWorks Task Pane görünümünü yok etti; yeniden oluşturma sıraya alındı.");
            }
            return 0;
        }

        private void EnsureTaskPaneAlive()
        {
            if (_swApp == null || _suppressTaskPaneRecreate) return;

            bool recreate = _taskPaneDestroyObserved ||
                            _taskPaneView == null ||
                            _taskPaneControl == null ||
                            _taskPaneControl.IsDisposed ||
                            !_taskPaneControl.IsHandleCreated;

            if (!recreate)
            {
                try
                {
                    // SOLIDWORKS 2024 SP02+ API. Sonucu önemli değil; bu çağrı
                    // COM TaskpaneView nesnesinin hâlâ geçerli olup olmadığını sınar.
                    _taskPaneView.IsActiveTab();
                }
                catch
                {
                    recreate = true;
                }
            }

            if (!recreate) return;

            Logger.Info("SOLIDWORKS Task Pane yenilendi/kayboldu; SrcCadWorks sekmesi yeniden oluşturuluyor.");
            _taskPaneDestroyObserved = false;

            var oldView = _taskPaneView;
            var oldControl = _taskPaneControl;
            _taskPaneView = null;
            _taskPaneControl = null;

            try
            {
                if (oldView != null)
                {
                    try { oldView.TaskPaneDestroyNotify -= OnTaskPaneDestroyNotify; } catch { }
                    try { oldView.DeleteView(); } catch { }
                }
            }
            catch { }

            try { oldControl?.Dispose(); } catch { }
            CreateTaskPane();
        }

        private void CreateTaskPane()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string iconDir = Path.Combine(baseDir, "assets", "taskpane");
                string[] images = new[]
                {
                    Path.Combine(iconDir, "SrcCadWorks_20.png"),
                    Path.Combine(iconDir, "SrcCadWorks_32.png"),
                    Path.Combine(iconDir, "SrcCadWorks_40.png"),
                    Path.Combine(iconDir, "SrcCadWorks_64.png"),
                    Path.Combine(iconDir, "SrcCadWorks_96.png"),
                    Path.Combine(iconDir, "SrcCadWorks_128.png")
                };
                foreach (string image in images)
                {
                    if (!File.Exists(image)) throw new FileNotFoundException("Task Pane simgesi bulunamadı.", image);
                }

                _taskPaneView = _swApp.CreateTaskpaneView3(images, "SrcCadWorks");
                if (_taskPaneView == null) throw new InvalidOperationException("SOLIDWORKS Task Pane oluşturulamadı.");

                // 2024 uyumlu yaşam döngüsü: SOLIDWORKS Task Pane'i yeniden oluşturursa
                // DestroyNotify ile yakalayıp timer üzerinden SrcCadWorks sekmesini geri kurarız.
                _taskPaneView.TaskPaneDestroyNotify += OnTaskPaneDestroyNotify;
                _taskPaneDestroyObserved = false;

                _taskPaneControl = new TaskPaneControl(_swApp, OnAssemblyViewer, OnAssemblyPrepare, OnAutomaticDrawing, OnLaserPrepare, OnWeldPrepare, OnManufacturingPrepare, OnMachiningPrepare, OnSmartNest);
                _taskPaneControl.CreateControl();
                bool shown = _taskPaneView.DisplayWindowFromHandlex64(_taskPaneControl.Handle.ToInt64());
                if (!shown) throw new InvalidOperationException("SrcCadWorks Task Pane .NET kontrolü eklenemedi.");
                Logger.Info("SrcCadWorks Task Pane sekmesi oluşturuldu.");
            }
            catch (Exception ex)
            {
                // Task Pane ikinci erişim yoludur; başarısız olması menü/toolbar komutunu bozmasın.
                Logger.Error("Task Pane oluşturulamadı", ex);
                try
                {
                    if (_taskPaneView != null)
                    {
                        try { _taskPaneView.TaskPaneDestroyNotify -= OnTaskPaneDestroyNotify; } catch { }
                        _taskPaneView.DeleteView();
                    }
                }
                catch { }
                try { _taskPaneControl?.Dispose(); } catch { }
                _taskPaneView = null;
                _taskPaneControl = null;
            }
        }


        private void EnableReadOnlyNoPromptPreference()
        {
            try
            {
                int pref = (int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave;
                _swApp.SetUserPreferenceToggle(pref, true);
                Logger.Info("SOLIDWORKS: salt-okunur referanslarda kaydetme sorusu kapatıldı (değişiklikler atılır). Bu ayar kullanıcı tercihi olarak açık bırakılır.");
            }
            catch (Exception ex)
            {
                Logger.Error("Salt-okunur kaydetme uyarısı ayarı değiştirilemedi", ex);
            }
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string guid = t.GUID.ToString("B");
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SOLIDWORKS\Addins\" + guid))
            {
                key.SetValue(null, 0, RegistryValueKind.DWord);
                key.SetValue("Title", "SrcCadWorks");
                key.SetValue("Description", "SrcCadWorks CAD ve üretim araçları");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SOLIDWORKS\AddInsStartup\" + guid))
                key.SetValue(null, 1, RegistryValueKind.DWord);
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string guid = t.GUID.ToString("B");
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SOLIDWORKS\Addins\" + guid, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SOLIDWORKS\AddInsStartup\" + guid, false); } catch { }
        }
    }
}
