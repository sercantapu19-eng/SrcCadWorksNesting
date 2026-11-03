using System;
using System.Diagnostics;
using System.IO;

namespace SrcCadWorks.Services
{
    internal sealed class ExternalWatchdogLauncher : IDisposable
    {
        private Process _process;
        private string _runtimeDirectory;

        public bool Start(int parentPid)
        {
            try
            {
                Stop();
                string sourceExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SrcCadWorks.Watchdog.exe");
                if (!File.Exists(sourceExe))
                {
                    Logger.Info("Harici watchdog bulunamadı: " + sourceExe);
                    return false;
                }

                string runtimeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SrcCadWorks", "watchdog-runtime");
                Directory.CreateDirectory(runtimeRoot);
                _runtimeDirectory = Path.Combine(runtimeRoot, parentPid + "_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_runtimeDirectory);
                string exePath = Path.Combine(_runtimeDirectory, "SrcCadWorks.Watchdog.exe");
                File.Copy(sourceExe, exePath, true);
                string sourceConfig = sourceExe + ".config";
                if (File.Exists(sourceConfig)) File.Copy(sourceConfig, exePath + ".config", true);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--parent-pid " + parentPid,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = _runtimeDirectory
                };
                _process = Process.Start(psi);
                Logger.Info("Harici üretim watchdog başlatıldı. PID=" + (_process == null ? 0 : _process.Id));
                return _process != null;
            }
            catch (Exception ex)
            {
                Logger.Info("Harici watchdog başlatılamadı: " + ex.Message);
                _process = null;
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                if (_process != null)
                {
                    if (!_process.HasExited) _process.Kill();
                    _process.Dispose();
                }
            }
            catch { }
            finally
            {
                _process = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(_runtimeDirectory) && Directory.Exists(_runtimeDirectory))
                        Directory.Delete(_runtimeDirectory, true);
                }
                catch { }
                _runtimeDirectory = null;
            }
        }

        public void Dispose() => Stop();
    }
}
