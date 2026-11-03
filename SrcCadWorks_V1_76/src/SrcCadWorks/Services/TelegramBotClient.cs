using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class TelegramBotClient
    {
        private readonly TelegramSettings _settings;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public TelegramBotClient(TelegramSettings settings)
        {
            _settings = settings == null ? new TelegramSettings() : settings.Clone();
        }

        public void SendMessage(string text, bool includeStatusButton = false)
        {
            if (!_settings.IsConfigured) return;
            foreach (string chunk in SplitMessage(text, 3500))
            {
                var fields = new Dictionary<string, string>
                {
                    { "chat_id", _settings.ChatId.Trim() },
                    { "text", chunk },
                    { "disable_web_page_preview", "true" }
                };
                if (includeStatusButton)
                    fields["reply_markup"] = BuildControlKeyboard();
                Post<TelegramApiResponse<object>>("sendMessage", fields);
                includeStatusButton = false;
            }
        }


        private static string BuildControlKeyboard()
        {
            return "{\"inline_keyboard\":[" +
                   "[{\"text\":\"📊 Durum\",\"callback_data\":\"srccadworks_status\"}," +
                   "{\"text\":\"📋 Son Rapor\",\"callback_data\":\"srccadworks_report\"}]," +
                   "[{\"text\":\"⏸ Duraklat\",\"callback_data\":\"srccadworks_pause\"}," +
                   "{\"text\":\"▶️ Devam Et\",\"callback_data\":\"srccadworks_resume\"}]]}";
        }

        public IList<TelegramUpdateDto> GetUpdates(long offset)
        {
            if (!_settings.IsConfigured) return new List<TelegramUpdateDto>();
            return GetUpdatesCore(offset);
        }

        /// <summary>
        /// Chat ID alanı henüz boşken de yalnız Bot Token ile Telegram'daki son
        /// mesajlardan kullanılabilir sohbetleri bulur. Kullanıcı önce bota /start
        /// veya herhangi bir mesaj göndermelidir; grup için bot gruba eklenip grupta
        /// en az bir mesaj oluşmalıdır.
        /// </summary>
        public IList<TelegramChatCandidate> DiscoverChats()
        {
            if (string.IsNullOrWhiteSpace(_settings.BotToken))
                throw new InvalidOperationException("Bot Token boş olamaz.");

            var updates = GetUpdatesCore(0);
            var map = new Dictionary<long, TelegramChatCandidate>();

            foreach (var update in updates)
            {
                if (update == null) continue;

                TelegramChatDto chat = null;
                if (update.message != null) chat = update.message.chat;
                else if (update.callback_query != null && update.callback_query.message != null)
                    chat = update.callback_query.message.chat;

                if (chat == null || chat.id == 0) continue;

                TelegramChatCandidate existing;
                if (!map.TryGetValue(chat.id, out existing))
                {
                    existing = new TelegramChatCandidate
                    {
                        Id = chat.id,
                        Type = chat.type,
                        Title = chat.title,
                        Username = chat.username,
                        FirstName = chat.first_name,
                        LastName = chat.last_name,
                        LastUpdateId = update.update_id
                    };
                    map[chat.id] = existing;
                }
                else if (update.update_id >= existing.LastUpdateId)
                {
                    existing.Type = chat.type;
                    existing.Title = chat.title;
                    existing.Username = chat.username;
                    existing.FirstName = chat.first_name;
                    existing.LastName = chat.last_name;
                    existing.LastUpdateId = update.update_id;
                }
            }

            return map.Values.OrderByDescending(x => x.LastUpdateId).ToList();
        }

        public void AnswerCallback(string callbackId, string text)
        {
            if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(callbackId)) return;
            var fields = new Dictionary<string, string>
            {
                { "callback_query_id", callbackId },
                { "text", text ?? "" },
                { "show_alert", "false" }
            };
            Post<TelegramApiResponse<object>>("answerCallbackQuery", fields);
        }

        private IList<TelegramUpdateDto> GetUpdatesCore(long offset)
        {
            var fields = new Dictionary<string, string>
            {
                { "offset", offset.ToString() },
                { "timeout", "0" },
                { "limit", "100" },
                { "allowed_updates", "[\"message\",\"callback_query\"]" }
            };
            var response = Post<TelegramApiResponse<TelegramUpdateDto[]>>("getUpdates", fields);
            return response.result ?? new TelegramUpdateDto[0];
        }

        private T Post<T>(string method, IDictionary<string, string> fields) where T : TelegramApiResponseBase
        {
            string token = (_settings.BotToken ?? "").Trim();
            if (token.Length == 0) throw new InvalidOperationException("Telegram Bot Token boş olamaz.");

            string url = "https://api.telegram.org/bot" + token + "/" + method;
            byte[] data = Encoding.UTF8.GetBytes(BuildForm(fields));

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            request.ContentLength = data.Length;
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.Proxy = WebRequest.DefaultWebProxy;

            using (var stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    var parsed = _json.Deserialize<T>(body);
                    if (parsed == null || !parsed.ok)
                        throw new InvalidOperationException("Telegram Bot API yanıtı başarısız: " + (parsed == null ? "boş yanıt" : parsed.description));
                    return parsed;
                }
            }
            catch (WebException ex)
            {
                string detail = "";
                try
                {
                    using (var response = ex.Response)
                    using (var reader = response == null ? null : new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
                        detail = reader == null ? "" : reader.ReadToEnd();
                }
                catch { }
                throw new InvalidOperationException("Telegram Bot API bağlantısı başarısız." + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail), ex);
            }
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

        private static IEnumerable<string> SplitMessage(string text, int maxLength)
        {
            string value = text ?? "";
            if (value.Length <= maxLength) { yield return value; yield break; }
            var lines = value.Replace("\r\n", "\n").Split('\n');
            var part = new StringBuilder();
            foreach (string line in lines)
            {
                if (part.Length > 0 && part.Length + line.Length + 1 > maxLength)
                {
                    yield return part.ToString();
                    part.Clear();
                }
                if (line.Length > maxLength)
                {
                    int start = 0;
                    while (start < line.Length)
                    {
                        int take = Math.Min(maxLength, line.Length - start);
                        if (part.Length > 0) { yield return part.ToString(); part.Clear(); }
                        yield return line.Substring(start, take);
                        start += take;
                    }
                    continue;
                }
                if (part.Length > 0) part.AppendLine();
                part.Append(line);
            }
            if (part.Length > 0) yield return part.ToString();
        }
    }

    internal sealed class TelegramChatCandidate
    {
        public long Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public long LastUpdateId { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
                string person = ((FirstName ?? "") + " " + (LastName ?? "")).Trim();
                if (!string.IsNullOrWhiteSpace(person)) return person;
                if (!string.IsNullOrWhiteSpace(Username)) return "@" + Username.Trim().TrimStart('@');
                return "Telegram sohbeti";
            }
        }

        public override string ToString()
        {
            string typeText;
            switch ((Type ?? "").Trim().ToLowerInvariant())
            {
                case "private": typeText = "Kişisel"; break;
                case "group": typeText = "Grup"; break;
                case "supergroup": typeText = "Süper Grup"; break;
                case "channel": typeText = "Kanal"; break;
                default: typeText = "Sohbet"; break;
            }

            string user = string.IsNullOrWhiteSpace(Username) ? "" : " • @" + Username.Trim().TrimStart('@');
            return typeText + " • " + DisplayName + user + " • Chat ID: " + Id;
        }
    }

    internal abstract class TelegramApiResponseBase
    {
        public bool ok { get; set; }
        public string description { get; set; }
    }

    internal sealed class TelegramApiResponse<T> : TelegramApiResponseBase
    {
        public T result { get; set; }
    }

    internal sealed class TelegramUpdateDto
    {
        public long update_id { get; set; }
        public TelegramMessageDto message { get; set; }
        public TelegramCallbackQueryDto callback_query { get; set; }
    }

    internal sealed class TelegramMessageDto
    {
        public long message_id { get; set; }
        public long date { get; set; }
        public string text { get; set; }
        public TelegramChatDto chat { get; set; }
    }

    internal sealed class TelegramChatDto
    {
        public long id { get; set; }
        public string type { get; set; }
        public string title { get; set; }
        public string username { get; set; }
        public string first_name { get; set; }
        public string last_name { get; set; }
    }

    internal sealed class TelegramCallbackQueryDto
    {
        public string id { get; set; }
        public string data { get; set; }
        public TelegramMessageDto message { get; set; }
    }
}
