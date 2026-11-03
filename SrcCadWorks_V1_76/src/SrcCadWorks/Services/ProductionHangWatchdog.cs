using System;
using System.Threading;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class ProductionHangWatchdog : IDisposable
    {
        private readonly ProductionRecoveryManager _recovery;
        private readonly TelegramSettings _telegram;
        private readonly Thread _thread;
        private volatile bool _stop;

        public ProductionHangWatchdog(ProductionRecoveryManager recovery, TelegramSettings telegram)
        {
            _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
            _telegram = telegram == null ? new TelegramSettings() : telegram.Clone();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SrcCadWorks Production Hang Watchdog"
            };
        }

        public void Start() => _thread.Start();

        private void Run()
        {
            while (!_stop)
            {
                for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(500);
                if (_stop) break;
                try
                {
                    if (!_recovery.TryMarkHung(DateTime.UtcNow)) continue;
                    var s = _recovery.Snapshot();
                    if (s == null) continue;
                    Logger.Info("Üretim watchdog ilerleme durması algıladı: " + (s.Stage ?? "") + " | " + (s.PartCode ?? ""));
                    if (_telegram.IsConfigured)
                    {
                        string text = "⚠️ SrcCadWorks – SOLIDWORKS yanıt vermiyor olabilir\n\n" +
                                      "🔧 İşlem: " + Safe(s.OperationName, "Üretim Paketi") + "\n" +
                                      "⚙️ Aşama: " + StageText(s.Stage) + "\n" +
                                      "📍 Parça: " + Math.Max(0, s.Index) + "/" + Math.Max(0, s.Total) +
                                      (string.IsNullOrWhiteSpace(s.PartCode) ? "" : " – " + s.PartCode) + "\n" +
                                      "⏱️ " + Math.Max(30, s.HangThresholdSeconds) + " saniyedir ilerleme yok.\n\n" +
                                      "💾 İlerleme diske kaydedildi. SOLIDWORKS yeniden başlatılırsa SrcCadWorks yarım kalan işi algılayabilir.";
                        new TelegramBotClient(_telegram).SendMessage(text, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info("Üretim watchdog uyarısı gönderilemedi: " + ex.Message);
                }
            }
        }

        private static string Safe(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string StageText(string stage)
        {
            switch ((stage ?? "").Trim().ToUpperInvariant())
            {
                case "PDF": return "Teknik resim PDF oluşturma";
                case "DXF": return "DXF oluşturma";
                case "TOPLU_DXF_KAYNAK": return "Toplu DXF/DWG kaynak hazırlama";
                case "TOPLU_DXF": return "Toplu DXF oluşturma";
                case "TOPLU_DWG": return "Toplu DWG oluşturma";
                case "EXCEL": return "Lazer Excel oluşturma";
                default: return string.IsNullOrWhiteSpace(stage) ? "Üretim" : stage;
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread.IsAlive) _thread.Join(1500); } catch { }
        }
    }
}
