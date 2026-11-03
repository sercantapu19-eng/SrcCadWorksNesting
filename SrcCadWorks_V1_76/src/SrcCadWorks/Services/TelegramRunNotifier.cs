using System;
using System.IO;
using System.Text;
using System.Threading;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class TelegramRunNotifier : IDisposable
    {
        private readonly TelegramSettings _settings;
        private readonly OperationProgressTracker _tracker;
        private readonly bool _manageStatusMonitor;
        private TelegramStatusMonitor _statusMonitor;

        public TelegramRunNotifier(TelegramSettings settings, string operationName, int totalItems, bool manageStatusMonitor = true)
        {
            _settings = settings == null ? new TelegramSettings() : settings.Clone();
            _tracker = new OperationProgressTracker(operationName, totalItems);
            _manageStatusMonitor = manageStatusMonitor;
        }

        public bool IsActive => _settings.IsConfigured;

        public OperationProgressSnapshot Snapshot() => _tracker.Snapshot();

        public void Start()
        {
            if (!IsActive) return;
            if (_manageStatusMonitor && _settings.EnableStatusCommand)
            {
                _statusMonitor = new TelegramStatusMonitor(_settings, () => _tracker.Snapshot());
                _statusMonitor.Start();
            }
            if (_settings.SendStartMessage)
                QueueSend(TelegramReportFormatter.FormatStarted(_tracker.Snapshot(), _settings.EnableStatusCommand), _settings.EnableStatusCommand);
        }

        public void Update(PackageProgress progress)
        {
            _tracker.Update(progress);
        }

        public void Complete(ProductionPackageResult result, ProductionPackageOptions options)
        {
            if (!IsActive || result == null) return;
            var report = ProductionTelegramReportFactory.Create(_tracker.Snapshot().OperationName, result, options);
            _tracker.Complete((report.StatusIcon + " " + report.StatusText).Trim(), result.ProductionDuration);
            if (_manageStatusMonitor) StopMonitor();

            string text = TelegramReportFormatter.FormatCompleted(report);
            TelegramLastReportStore.Save(text);
            if (_settings.SendCompletionMessage)
                QueueSend(text, _settings.EnableStatusCommand);
        }

        public void Cancelled()
        {
            if (!IsActive) return;
            if (_manageStatusMonitor) StopMonitor();
            QueueSend(TelegramReportFormatter.FormatCancelled(_tracker.Snapshot()), _settings.EnableStatusCommand);
        }

        public void Failed(Exception error)
        {
            if (!IsActive) return;
            if (_manageStatusMonitor) StopMonitor();
            QueueSend(TelegramReportFormatter.FormatFailed(_tracker.Snapshot(), error == null ? "Bilinmeyen hata" : error.Message), _settings.EnableStatusCommand);
        }

        private void QueueSend(string text, bool statusButton)
        {
            if (!IsActive || string.IsNullOrWhiteSpace(text)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { new TelegramBotClient(_settings).SendMessage(text, statusButton); }
                catch (Exception ex) { Logger.Error("Telegram bildirimi gönderilemedi", ex); }
            });
        }

        private void StopMonitor()
        {
            try { _statusMonitor?.Stop(); } catch { }
            _statusMonitor = null;
        }

        public void Dispose()
        {
            if (_manageStatusMonitor) StopMonitor();
        }
    }

    internal static class TelegramLastReportStore
    {
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SrcCadWorks");
                return Path.Combine(dir, "telegram_last_report.txt");
            }
        }

        public static void Save(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Logger.Info("Telegram son raporu kaydedilemedi: " + ex.Message);
            }
        }

        public static string Load()
        {
            try
            {
                string path = FilePath;
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            catch (Exception ex)
            {
                Logger.Info("Telegram son raporu okunamadı: " + ex.Message);
                return "";
            }
        }
    }
}
