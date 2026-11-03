using System;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    /// <summary>
    /// İmalat/Talaşlı dosya hazırlama ekranları için Lazer Hazırla ile aynı
    /// Telegram Durum / Son Rapor / Duraklat / Devam Et altyapısını sağlar.
    /// </summary>
    internal sealed class PrepTelegramControl : IDisposable
    {
        private readonly TelegramSettings _settings;
        private readonly Func<bool> _busyProvider;
        private readonly Func<bool> _pausedProvider;
        private readonly Action _pauseAction;
        private readonly Action _resumeAction;
        private TelegramStatusMonitor _monitor;
        private OperationProgressTracker _tracker;
        private DateTime _runStartedUtc;

        public PrepTelegramControl(Func<bool> busyProvider, Func<bool> pausedProvider, Action pauseAction, Action resumeAction)
        {
            _settings = TelegramSettingsStore.Load();
            _busyProvider = busyProvider;
            _pausedProvider = pausedProvider;
            _pauseAction = pauseAction;
            _resumeAction = resumeAction;
        }

        public TelegramSettings Settings => _settings;

        public void StartMonitor()
        {
            StopMonitor();
            if (!_settings.IsConfigured || !_settings.EnableStatusCommand) return;
            _monitor = new TelegramStatusMonitor(
                _settings,
                Snapshot,
                _busyProvider,
                _pausedProvider,
                _pauseAction,
                _resumeAction,
                TelegramLastReportStore.Load);
            _monitor.Start();
        }

        public void BeginRun(string operationName, int totalItems)
        {
            _runStartedUtc = DateTime.UtcNow;
            _tracker = new OperationProgressTracker(operationName, Math.Max(1, totalItems));
        }

        public void Update(int index, int total, string stage, string partCode)
        {
            var tracker = _tracker;
            if (tracker == null) return;
            TimeSpan elapsed = _runStartedUtc == default(DateTime) ? TimeSpan.Zero : DateTime.UtcNow - _runStartedUtc;
            tracker.Update(new PackageProgress
            {
                Stage = string.IsNullOrWhiteSpace(stage) ? "İŞLENİYOR" : stage,
                Index = Math.Max(0, index),
                Total = Math.Max(1, total),
                PartCode = partCode ?? "",
                OverallIndex = Math.Max(0, index),
                OverallTotal = Math.Max(1, total),
                Elapsed = elapsed
            });
        }

        public void Complete(string finalStatus, TimeSpan elapsed)
        {
            try { _tracker?.Complete(finalStatus, elapsed); } catch { }
        }

        public OperationProgressSnapshot Snapshot()
        {
            try { return _tracker == null ? null : _tracker.Snapshot(); }
            catch { return null; }
        }

        private void StopMonitor()
        {
            try { _monitor?.Stop(); } catch { }
            _monitor = null;
        }

        public void Dispose()
        {
            StopMonitor();
        }
    }
}
