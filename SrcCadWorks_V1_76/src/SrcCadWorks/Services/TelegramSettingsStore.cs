using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal static class TelegramSettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SrcCadWorks.Telegram.Settings.v1");

        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SrcCadWorks", "telegram_settings.xml");

        public static TelegramSettings Load()
        {
            var settings = new TelegramSettings();
            try
            {
                if (!File.Exists(SettingsPath)) return settings;
                var doc = new XmlDocument();
                doc.Load(SettingsPath);
                var root = doc.DocumentElement;
                if (root == null) return settings;

                settings.Enabled = ReadBool(root, "Enabled", false);
                settings.ChatId = ReadText(root, "ChatId");
                settings.SendStartMessage = ReadBool(root, "SendStartMessage", true);
                settings.SendCompletionMessage = ReadBool(root, "SendCompletionMessage", true);
                settings.EnableStatusCommand = ReadBool(root, "EnableStatusCommand", true);

                string protectedToken = ReadText(root, "BotTokenProtected");
                if (!string.IsNullOrWhiteSpace(protectedToken))
                    settings.BotToken = Unprotect(protectedToken);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram ayarları okunamadı", ex);
            }
            return settings;
        }

        public static void Save(TelegramSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            string dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var doc = new XmlDocument();
            var root = doc.CreateElement("TelegramSettings");
            doc.AppendChild(root);
            Add(doc, root, "Enabled", settings.Enabled.ToString());
            Add(doc, root, "ChatId", settings.ChatId ?? "");
            Add(doc, root, "SendStartMessage", settings.SendStartMessage.ToString());
            Add(doc, root, "SendCompletionMessage", settings.SendCompletionMessage.ToString());
            Add(doc, root, "EnableStatusCommand", settings.EnableStatusCommand.ToString());
            Add(doc, root, "BotTokenProtected", Protect(settings.BotToken ?? ""));
            doc.Save(SettingsPath);
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] raw = Encoding.UTF8.GetBytes(value);
            byte[] encrypted = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            byte[] encrypted = Convert.FromBase64String(value);
            byte[] raw = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }

        private static void Add(XmlDocument doc, XmlElement root, string name, string value)
        {
            var node = doc.CreateElement(name);
            node.InnerText = value ?? "";
            root.AppendChild(node);
        }

        private static string ReadText(XmlElement root, string name)
        {
            var node = root.SelectSingleNode(name);
            return node == null ? "" : (node.InnerText ?? "");
        }

        private static bool ReadBool(XmlElement root, string name, bool fallback)
        {
            bool value;
            return bool.TryParse(ReadText(root, name), out value) ? value : fallback;
        }
    }
}
