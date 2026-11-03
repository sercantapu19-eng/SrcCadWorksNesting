using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace SrcCadWorks.Watchdog
{
    internal static class Program
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SrcCadWorks.Telegram.Settings.v1");
        private static string RecoveryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "recovery", "production_recovery.xml");
        private static string HangMarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "recovery", "production_recovery.hung");
        private static string TelegramSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SrcCadWorks", "telegram_settings.xml");
        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SrcCadWorks", "logs", "watchdog.log");

        [STAThread]
        private static int Main(string[] args)
        {
            int parentPid = ReadIntArg(args, "--parent-pid");
            if (parentPid <= 0) return 2;

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            Log("Harici watchdog başladı. Parent PID=" + parentPid);

            string lastAlertKey = "";
            DateTime recoveryMissingSinceUtc = DateTime.MinValue;

            while (true)
            {
                Thread.Sleep(2000);

                bool parentAlive = IsProcessAlive(parentPid);
                RecoverySnapshot state = TryReadRecovery();
                if (state == null)
                {
                    if (recoveryMissingSinceUtc == DateTime.MinValue) recoveryMissingSinceUtc = DateTime.UtcNow;
                    if (!parentAlive || DateTime.UtcNow - recoveryMissingSinceUtc > TimeSpan.FromSeconds(20))
                    {
                        Log("Kurtarma kaydı yok; watchdog kapanıyor.");
                        return 0;
                    }
                    continue;
                }
                recoveryMissingSinceUtc = DateTime.MinValue;

                // Eski watchdog yeni başlatılmış bir SOLIDWORKS koşusunun recovery kaydını izlememeli.
                if (state.HostProcessId > 0 && state.HostProcessId != parentPid)
                {
                    Log("Recovery kaydı başka host sürecine ait; eski watchdog kapanıyor. Recovery PID=" + state.HostProcessId);
                    return 0;
                }

                // Parent süreç gerçekten kapandıysa 120 saniyelik hang eşiğini bekleme.
                // Sert SOLIDWORKS/add-in kapanmalarında kullanıcıya anında Telegram bildirimi gönder.
                if (!parentAlive)
                {
                    int crashThreshold = Math.Max(30, state.HangThresholdSeconds <= 0 ? 120 : state.HangThresholdSeconds);
                    try { WriteHangMarker(state, crashThreshold); } catch (Exception ex) { Log("Crash marker yazılamadı: " + ex.Message); }
                    try
                    {
                        TelegramConfig telegram = LoadTelegram();
                        if (telegram != null && telegram.IsConfigured)
                        {
                            string text = "❌ SrcCadWorks – SOLIDWORKS/üretim süreci beklenmedik şekilde kapandı\n\n" +
                                          "🔧 İşlem: " + Safe(state.OperationName, "Üretim Paketi") + "\n" +
                                          "⚙️ Aşama: " + StageText(state.Stage) + "\n" +
                                          "📍 Parça: " + Math.Max(0, state.Index) + "/" + Math.Max(0, state.Total) +
                                          (string.IsNullOrWhiteSpace(state.PartCode) ? "" : " – " + state.PartCode) + "\n\n" +
                                          "💾 Kurtarma kaydı ve tamamlanan çıktı önbelleği korunuyor. SOLIDWORKS yeniden açıldığında SrcCadWorks kaldığı işten devam edebilir.";
                            SendTelegram(telegram, text);
                            Log("Telegram beklenmedik kapanma uyarısı gönderildi.");
                        }
                        else Log("Parent kapandı; Telegram yapılandırılmamış.");
                    }
                    catch (Exception ex) { Log("Beklenmedik kapanma Telegram uyarısı gönderilemedi: " + ex.Message); }
                    return 0;
                }

                int threshold = Math.Max(30, state.HangThresholdSeconds <= 0 ? 120 : state.HangThresholdSeconds);
                if (state.LastProgressUtc == DateTime.MinValue) continue;
                TimeSpan stale = DateTime.UtcNow - state.LastProgressUtc;
                if (stale.TotalSeconds < threshold) continue;

                string alertKey = state.LastProgressUtc.Ticks + "|" + (state.Stage ?? "") + "|" + (state.PartCode ?? "") + "|" + state.Index;
                if (string.Equals(alertKey, lastAlertKey, StringComparison.Ordinal)) continue;
                lastAlertKey = alertKey;

                try { WriteHangMarker(state, threshold); }
                catch (Exception ex) { Log("Hang marker yazılamadı: " + ex.Message); }

                try
                {
                    TelegramConfig telegram = LoadTelegram();
                    if (telegram != null && telegram.IsConfigured)
                    {
                        string text = "⚠️ SrcCadWorks – SOLIDWORKS yanıt vermiyor olabilir\n\n" +
                                      "🔧 İşlem: " + Safe(state.OperationName, "Üretim Paketi") + "\n" +
                                      "⚙️ Aşama: " + StageText(state.Stage) + "\n" +
                                      "📍 Parça: " + Math.Max(0, state.Index) + "/" + Math.Max(0, state.Total) +
                                      (string.IsNullOrWhiteSpace(state.PartCode) ? "" : " – " + state.PartCode) + "\n" +
                                      "⏱️ " + threshold + " saniyedir ilerleme yok.\n\n" +
                                      "💾 İlerleme diske kaydedildi. Bu uyarı SOLIDWORKS sürecinden bağımsız watchdog tarafından gönderildi. " +
                                      "SOLIDWORKS yeniden başlatılırsa SrcCadWorks yarım kalan işi algılayabilir.";
                        SendTelegram(telegram, text);
                        Log("Telegram kilitlenme uyarısı gönderildi: " + (state.PartCode ?? ""));
                    }
                    else Log("Telegram yapılandırılmamış; yalnız hang marker yazıldı.");
                }
                catch (Exception ex)
                {
                    Log("Telegram uyarısı gönderilemedi: " + ex.Message);
                }

                if (!parentAlive)
                {
                    Log("Parent süreç kapalı; son uyarı sonrası watchdog kapanıyor.");
                    return 0;
                }
            }
        }

        private static RecoverySnapshot TryReadRecovery()
        {
            try
            {
                if (!File.Exists(RecoveryPath)) return null;
                var doc = new XmlDocument();
                using (var stream = new FileStream(RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    doc.Load(stream);
                var root = doc.DocumentElement;
                if (root == null) return null;
                return new RecoverySnapshot
                {
                    RunId = ReadText(root, "RunId"),
                    HostProcessId = ReadInt(root, "HostProcessId"),
                    OperationName = ReadText(root, "OperationName"),
                    LastProgressUtc = ReadDate(root, "LastProgressUtc"),
                    Stage = ReadText(root, "Stage"),
                    PartCode = ReadText(root, "PartCode"),
                    Index = ReadInt(root, "Index"),
                    Total = ReadInt(root, "Total"),
                    HangThresholdSeconds = ReadInt(root, "HangThresholdSeconds")
                };
            }
            catch (IOException) { return null; }
            catch (XmlException) { return null; }
            catch (Exception ex)
            {
                Log("Kurtarma kaydı okunamadı: " + ex.Message);
                return null;
            }
        }

        private static TelegramConfig LoadTelegram()
        {
            if (!File.Exists(TelegramSettingsPath)) return null;
            var doc = new XmlDocument();
            doc.Load(TelegramSettingsPath);
            var root = doc.DocumentElement;
            if (root == null) return null;

            bool enabled;
            bool.TryParse(ReadText(root, "Enabled"), out enabled);
            string protectedToken = ReadText(root, "BotTokenProtected");
            string token = "";
            if (!string.IsNullOrWhiteSpace(protectedToken))
            {
                byte[] encrypted = Convert.FromBase64String(protectedToken);
                byte[] raw = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                token = Encoding.UTF8.GetString(raw);
            }
            return new TelegramConfig
            {
                Enabled = enabled,
                BotToken = token,
                ChatId = ReadText(root, "ChatId")
            };
        }

        private static void SendTelegram(TelegramConfig cfg, string text)
        {
            string url = "https://api.telegram.org/bot" + cfg.BotToken.Trim() + "/sendMessage";
            var fields = new Dictionary<string, string>
            {
                { "chat_id", cfg.ChatId.Trim() },
                { "text", text ?? "" },
                { "disable_web_page_preview", "true" }
            };
            byte[] data = Encoding.UTF8.GetBytes(BuildForm(fields));
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            request.ContentLength = data.Length;
            request.Timeout = 7000;
            request.ReadWriteTimeout = 7000;
            using (var stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                if (body.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("Telegram Bot API başarısız yanıt verdi.");
            }
        }

        private static void WriteHangMarker(RecoverySnapshot state, int threshold)
        {
            string dir = Path.GetDirectoryName(HangMarkerPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            string temp = HangMarkerPath + ".tmp";
            File.WriteAllLines(temp, new[]
            {
                DateTime.UtcNow.ToString("o"),
                state.LastProgressUtc.ToString("o"),
                state.Stage ?? "",
                state.PartCode ?? "",
                state.Index.ToString(),
                state.Total.ToString(),
                threshold.ToString()
            }, Encoding.UTF8);
            if (File.Exists(HangMarkerPath)) File.Delete(HangMarkerPath);
            File.Move(temp, HangMarkerPath);
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        private static int ReadIntArg(string[] args, string name)
        {
            if (args == null) return 0;
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(args[i + 1], out value)) return value;
                }
            return 0;
        }

        private static string BuildForm(IDictionary<string, string> fields)
        {
            var sb = new StringBuilder();
            foreach (var pair in fields)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(pair.Key ?? ""));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(pair.Value ?? ""));
            }
            return sb.ToString();
        }

        private static string ReadText(XmlElement root, string name)
        {
            var node = root.SelectSingleNode(name);
            return node == null ? "" : (node.InnerText ?? "");
        }

        private static int ReadInt(XmlElement root, string name)
        {
            int value;
            return int.TryParse(ReadText(root, name), out value) ? value : 0;
        }

        private static DateTime ReadDate(XmlElement root, string name)
        {
            DateTime value;
            return DateTime.TryParse(ReadText(root, name), null, System.Globalization.DateTimeStyles.RoundtripKind, out value)
                ? value.ToUniversalTime() : DateTime.MinValue;
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

        private static void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private sealed class RecoverySnapshot
        {
            public string RunId { get; set; }
            public int HostProcessId { get; set; }
            public string OperationName { get; set; }
            public DateTime LastProgressUtc { get; set; }
            public string Stage { get; set; }
            public string PartCode { get; set; }
            public int Index { get; set; }
            public int Total { get; set; }
            public int HangThresholdSeconds { get; set; }
        }

        private sealed class TelegramConfig
        {
            public bool Enabled { get; set; }
            public string BotToken { get; set; }
            public string ChatId { get; set; }
            public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
        }
    }
}
