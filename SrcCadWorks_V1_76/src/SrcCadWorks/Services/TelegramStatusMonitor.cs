using System;
using System.Threading;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class TelegramStatusMonitor : IDisposable
    {
        private readonly TelegramSettings _settings;
        private readonly TelegramBotClient _client;
        private readonly Func<OperationProgressSnapshot> _snapshotProvider;
        private readonly Func<bool> _isBusyProvider;
        private readonly Func<bool> _isPausedProvider;
        private readonly Action _pauseAction;
        private readonly Action _resumeAction;
        private readonly Func<string> _lastReportProvider;
        private Thread _thread;
        private volatile bool _stopRequested;
        private long _offset;
        private readonly long _startedUnix;

        public TelegramStatusMonitor(
            TelegramSettings settings,
            Func<OperationProgressSnapshot> snapshotProvider,
            Func<bool> isBusyProvider = null,
            Func<bool> isPausedProvider = null,
            Action pauseAction = null,
            Action resumeAction = null,
            Func<string> lastReportProvider = null)
        {
            _settings = settings == null ? new TelegramSettings() : settings.Clone();
            _client = new TelegramBotClient(_settings);
            _snapshotProvider = snapshotProvider;
            _isBusyProvider = isBusyProvider;
            _isPausedProvider = isPausedProvider;
            _pauseAction = pauseAction;
            _resumeAction = resumeAction;
            _lastReportProvider = lastReportProvider;
            _startedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2;
        }

        public void Start()
        {
            if (!_settings.IsConfigured || !_settings.EnableStatusCommand || _thread != null) return;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SrcCadWorks.TelegramStatus"
            };
            _thread.Start();
        }

        private void Run()
        {
            int failureCount = 0;
            while (!_stopRequested)
            {
                int delayTicks = 30; // normal durumda yaklaşık 3 saniye
                try
                {
                    var updates = _client.GetUpdates(_offset);
                    foreach (var update in updates)
                    {
                        if (update == null) continue;
                        _offset = Math.Max(_offset, update.update_id + 1);
                        HandleUpdate(update);
                    }
                    failureCount = 0;
                }
                catch (Exception ex)
                {
                    failureCount++;
                    if (failureCount == 1 || failureCount % 6 == 0)
                        Logger.Error("Telegram komut/buton dinleyicisi hata verdi", ex);
                    delayTicks = 100; // bağlantı yoksa log/spam üretmeden yaklaşık 10 saniye bekle
                }

                for (int i = 0; i < delayTicks && !_stopRequested; i++) Thread.Sleep(100);
            }
        }

        private void HandleUpdate(TelegramUpdateDto update)
        {
            if (update.message != null)
            {
                if (update.message.date < _startedUnix || !IsAuthorized(update.message.chat)) return;
                string command = ParseCommand(update.message.text);
                switch (command)
                {
                    case "durum": SendStatus(); break;
                    case "rapor": SendLastReport(); break;
                    case "duraklat": Pause(); break;
                    case "devam": Resume(); break;
                }
                return;
            }

            var callback = update.callback_query;
            if (callback == null || callback.message == null || callback.message.date < _startedUnix || !IsAuthorized(callback.message.chat)) return;

            switch ((callback.data ?? "").Trim())
            {
                case "srccadworks_status":
                    TryAnswerCallback(callback.id, "Güncel durum gönderildi.");
                    SendStatus();
                    break;
                case "srccadworks_report":
                    TryAnswerCallback(callback.id, "Son rapor gönderildi.");
                    SendLastReport();
                    break;
                case "srccadworks_pause":
                    TryAnswerCallback(callback.id, "Duraklatma isteği alındı.");
                    Pause();
                    break;
                case "srccadworks_resume":
                    TryAnswerCallback(callback.id, "Devam isteği alındı.");
                    Resume();
                    break;
            }
        }

        private void TryAnswerCallback(string callbackId, string text)
        {
            try { _client.AnswerCallback(callbackId, text); } catch { }
        }

        private void SendStatus()
        {
            try
            {
                var snapshot = _snapshotProvider == null ? null : _snapshotProvider();
                string text = TelegramReportFormatter.FormatStatus(snapshot);
                if (IsBusy() && IsPaused())
                    text += "\n\n⏸ Durum: Duraklatıldı";
                _client.SendMessage(text, true);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram anlık durum mesajı gönderilemedi", ex);
            }
        }

        private void SendLastReport()
        {
            try
            {
                string report = _lastReportProvider == null ? "" : (_lastReportProvider() ?? "");
                if (string.IsNullOrWhiteSpace(report))
                    report = "📋 SrcCadWorks – Henüz tamamlanmış bir işlem raporu bulunmuyor.";
                _client.SendMessage(report, true);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram son rapor mesajı gönderilemedi", ex);
            }
        }

        private void Pause()
        {
            try
            {
                if (!IsBusy())
                {
                    _client.SendMessage("ℹ️ SrcCadWorks – Şu anda duraklatılabilecek aktif bir üretim işlemi yok.", true);
                    return;
                }
                if (IsPaused())
                {
                    _client.SendMessage("⏸ SrcCadWorks – İşlem zaten duraklatılmış durumda.", true);
                    return;
                }
                _pauseAction?.Invoke();
                _client.SendMessage("⏸ SrcCadWorks – Duraklatma isteği alındı. Geçerli güvenli adım tamamlanınca işlem bekleyecek.", true);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram duraklat komutu uygulanamadı", ex);
            }
        }

        private void Resume()
        {
            try
            {
                if (!IsBusy())
                {
                    _client.SendMessage("ℹ️ SrcCadWorks – Şu anda devam ettirilecek aktif bir üretim işlemi yok.", true);
                    return;
                }
                if (!IsPaused())
                {
                    _client.SendMessage("▶️ SrcCadWorks – İşlem zaten devam ediyor.", true);
                    return;
                }
                _resumeAction?.Invoke();
                _client.SendMessage("▶️ SrcCadWorks – Devam isteği alındı. Üretim kaldığı yerden sürdürülüyor.", true);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram devam komutu uygulanamadı", ex);
            }
        }

        private bool IsBusy()
        {
            try { return _isBusyProvider != null && _isBusyProvider(); }
            catch { return false; }
        }

        private bool IsPaused()
        {
            try { return _isPausedProvider != null && _isPausedProvider(); }
            catch { return false; }
        }

        private bool IsAuthorized(TelegramChatDto chat)
        {
            if (chat == null) return false;
            long configured;
            return long.TryParse((_settings.ChatId ?? "").Trim(), out configured) && configured == chat.id;
        }

        private static string ParseCommand(string text)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) return "";

            if (value.StartsWith("/", StringComparison.Ordinal))
            {
                int space = value.IndexOf(' ');
                if (space >= 0) value = value.Substring(0, space);
                int at = value.IndexOf('@');
                if (at >= 0) value = value.Substring(0, at);
                value = value.TrimStart('/');
            }

            if (string.Equals(value, "durum", StringComparison.OrdinalIgnoreCase)) return "durum";
            if (string.Equals(value, "rapor", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "sonrapor", StringComparison.OrdinalIgnoreCase)) return "rapor";
            if (string.Equals(value, "duraklat", StringComparison.OrdinalIgnoreCase)) return "duraklat";
            if (string.Equals(value, "devam", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "devamet", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "devam et", StringComparison.OrdinalIgnoreCase)) return "devam";
            return "";
        }

        public void Stop()
        {
            _stopRequested = true;
            try
            {
                if (_thread != null && _thread.IsAlive) _thread.Join(700);
            }
            catch { }
        }

        public void Dispose() => Stop();
    }
}
