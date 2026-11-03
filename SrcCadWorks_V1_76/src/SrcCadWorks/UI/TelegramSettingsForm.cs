using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SrcCadWorks.Models;
using SrcCadWorks.Services;

namespace SrcCadWorks.UI
{
    internal sealed class TelegramSettingsForm : Form
    {
        private readonly CheckBox _enabled;
        private readonly TextBox _token;
        private readonly TextBox _chatId;
        private readonly CheckBox _sendStart;
        private readonly CheckBox _sendCompletion;
        private readonly CheckBox _statusCommand;
        private readonly CheckBox _showToken;
        private readonly Button _findChat;
        private readonly Button _test;
        private readonly Button _save;

        public TelegramSettingsForm()
        {
            Text = "SrcCadWorks — Telegram Ayarları";
            SrcCadWorks.Services.BrandIcon.Apply(this);
            StartPosition = FormStartPosition.CenterParent;
            Width = 660;
            Height = 485;
            MinimumSize = new Size(620, 460);
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(244, 246, 249);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var header = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Telegram Bildirim ve Durum Botu",
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
                ForeColor = Color.FromArgb(31, 36, 42),
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(header, 0, 0);

            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                BackColor = Color.White,
                Padding = new Padding(14)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 7F));
            root.Controls.Add(fields, 0, 1);

            _enabled = new CheckBox { Text = "Telegram bildirimlerini etkinleştir", AutoSize = true, Dock = DockStyle.Fill };
            fields.Controls.Add(new Label { Text = "Bildirim", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            fields.Controls.Add(_enabled, 1, 0);

            _token = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(3, 6, 3, 6) };
            fields.Controls.Add(new Label { Text = "Bot Token", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            fields.Controls.Add(_token, 1, 1);

            var chatPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            chatPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            chatPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            _chatId = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 6, 6, 6) };
            _findChat = new Button { Text = "Chat ID Bul", Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
            _findChat.Click += FindChatClicked;
            chatPanel.Controls.Add(_chatId, 0, 0);
            chatPanel.Controls.Add(_findChat, 1, 0);
            fields.Controls.Add(new Label { Text = "Chat ID", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            fields.Controls.Add(chatPanel, 1, 2);

            _showToken = new CheckBox { Text = "Token'ı göster", AutoSize = true, Dock = DockStyle.Fill };
            _showToken.CheckedChanged += (s, e) => _token.UseSystemPasswordChar = !_showToken.Checked;
            fields.Controls.Add(new Label { Text = "Görünüm", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
            fields.Controls.Add(_showToken, 1, 3);

            _sendStart = new CheckBox { Text = "İşlem başladığında mesaj gönder", AutoSize = true, Dock = DockStyle.Fill };
            fields.Controls.Add(new Label { Text = "Başlangıç", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
            fields.Controls.Add(_sendStart, 1, 4);

            _sendCompletion = new CheckBox { Text = "İşlem tamamlandığında sonuç raporu gönder", AutoSize = true, Dock = DockStyle.Fill };
            fields.Controls.Add(new Label { Text = "Sonuç", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
            fields.Controls.Add(_sendCompletion, 1, 5);

            _statusCommand = new CheckBox { Text = "Telegram durum/rapor ve uzaktan kontrol düğmeleri", AutoSize = true, Dock = DockStyle.Fill };
            fields.Controls.Add(new Label { Text = "Anlık Durum", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
            fields.Controls.Add(_statusCommand, 1, 6);

            var info = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Chat ID Bul: Önce Telegram'da botunuza /start veya herhangi bir mesaj gönderin. Grup için botu gruba ekleyip grupta bir mesaj gönderin.\n" +
                       "Bot Token ve Chat ID bu Windows kullanıcı hesabında saklanır; token Windows DPAPI ile şifrelenir. İşlem sırasında Durum, Son Rapor, Duraklat ve Devam Et düğmelerini kullanabilirsiniz.",
                ForeColor = Color.FromArgb(94, 103, 113),
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(info, 0, 2);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 5, 0, 0)
            };
            var cancel = new Button { Text = "İptal", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
            _save = new Button { Text = "Kaydet", Width = 100, Height = 32 };
            _test = new Button { Text = "Bağlantıyı Test Et", Width = 145, Height = 32 };
            _save.Click += SaveClicked;
            _test.Click += TestClicked;
            actions.Controls.Add(cancel);
            actions.Controls.Add(_save);
            actions.Controls.Add(_test);
            root.Controls.Add(actions, 0, 3);
            CancelButton = cancel;

            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = TelegramSettingsStore.Load();
            _enabled.Checked = settings.Enabled;
            _token.Text = settings.BotToken ?? "";
            _chatId.Text = settings.ChatId ?? "";
            _sendStart.Checked = settings.SendStartMessage;
            _sendCompletion.Checked = settings.SendCompletionMessage;
            _statusCommand.Checked = settings.EnableStatusCommand;
        }

        private TelegramSettings ReadSettings()
        {
            return new TelegramSettings
            {
                Enabled = _enabled.Checked,
                BotToken = _token.Text.Trim(),
                ChatId = _chatId.Text.Trim(),
                SendStartMessage = _sendStart.Checked,
                SendCompletionMessage = _sendCompletion.Checked,
                EnableStatusCommand = _statusCommand.Checked
            };
        }

        private bool ValidateBotToken(TelegramSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.BotToken)) return true;
            MessageBox.Show(this, "Bot Token boş olamaz.", "Telegram Ayarları", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _token.Focus();
            return false;
        }

        private bool ValidateSettings(TelegramSettings settings, bool requireConfigured)
        {
            if (!requireConfigured && !settings.Enabled) return true;
            if (!ValidateBotToken(settings)) return false;
            if (string.IsNullOrWhiteSpace(settings.ChatId))
            {
                MessageBox.Show(this, "Chat ID boş olamaz. 'Chat ID Bul' düğmesini kullanabilirsiniz.", "Telegram Ayarları", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _chatId.Focus();
                return false;
            }
            return true;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            try
            {
                var settings = ReadSettings();
                if (!ValidateSettings(settings, settings.Enabled)) return;
                TelegramSettingsStore.Save(settings);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram ayarları kaydedilemedi", ex);
                MessageBox.Show(this, "Telegram ayarları kaydedilemedi:\n" + ex.Message, "Telegram Ayarları", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void FindChatClicked(object sender, EventArgs e)
        {
            var settings = ReadSettings();
            if (!ValidateBotToken(settings)) return;

            try
            {
                SetBusy(true);
                Application.DoEvents();

                IList<TelegramChatCandidate> chats = new TelegramBotClient(settings).DiscoverChats();
                if (chats == null || chats.Count == 0)
                {
                    MessageBox.Show(this,
                        "Telegram'da bu bot için henüz sohbet bulunamadı.\n\n" +
                        "1) Telegram'da botunuzu açın.\n" +
                        "2) /start veya herhangi bir mesaj gönderin.\n" +
                        "3) Grup kullanacaksanız botu gruba ekleyip grupta bir mesaj gönderin.\n" +
                        "4) Sonra tekrar 'Chat ID Bul' düğmesine basın.",
                        "Chat ID Bul", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                TelegramChatCandidate selected = chats.Count == 1 ? chats[0] : SelectChat(chats);
                if (selected == null) return;

                _chatId.Text = selected.Id.ToString();
                MessageBox.Show(this,
                    "Chat ID bulundu ve alana yazıldı:\n\n" + selected + "\n\nKaydet veya Bağlantıyı Test Et ile devam edebilirsiniz.",
                    "Chat ID Bul", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram Chat ID bulunamadı", ex);
                MessageBox.Show(this,
                    "Chat ID bulunamadı:\n" + ex.Message + "\n\nBot Token'ı kontrol edin ve Telegram'da bota en az bir mesaj gönderildiğinden emin olun.",
                    "Chat ID Bul", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private TelegramChatCandidate SelectChat(IList<TelegramChatCandidate> chats)
        {
            using (var dialog = new Form())
            {
                dialog.Text = "SrcCadWorks — Telegram Sohbetini Seç";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Width = 650;
                dialog.Height = 330;
                dialog.MinimumSize = new Size(560, 280);
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.Font = Font;
                dialog.BackColor = Color.FromArgb(244, 246, 249);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14),
                    ColumnCount = 1,
                    RowCount = 3
                };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                dialog.Controls.Add(root);

                root.Controls.Add(new Label
                {
                    Text = "Birden fazla Telegram sohbeti bulundu. Mesajların gönderileceği hesabı veya grubu seçin:",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, 0);

                var list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    IntegralHeight = false
                };
                foreach (var chat in chats) list.Items.Add(chat);
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                root.Controls.Add(list, 0, 1);

                var actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Padding = new Padding(0, 5, 0, 0)
                };
                var cancel = new Button { Text = "İptal", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
                var select = new Button { Text = "Seç", Width = 100, Height = 32, DialogResult = DialogResult.OK };
                actions.Controls.Add(cancel);
                actions.Controls.Add(select);
                root.Controls.Add(actions, 0, 2);
                dialog.AcceptButton = select;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                return list.SelectedItem as TelegramChatCandidate;
            }
        }

        private void TestClicked(object sender, EventArgs e)
        {
            var settings = ReadSettings();
            settings.Enabled = true;
            if (!ValidateSettings(settings, true)) return;
            try
            {
                SetBusy(true);
                Application.DoEvents();
                new TelegramBotClient(settings).SendMessage(
                    "✅ SrcCadWorks – Telegram bağlantı testi başarılı.\n\n" +
                    "📊 /durum özelliği üretim işlemi sırasında kullanılabilir.");
                MessageBox.Show(this, "Test mesajı Telegram'a gönderildi.", "Telegram Ayarları", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("Telegram bağlantı testi başarısız", ex);
                MessageBox.Show(this, "Telegram bağlantı testi başarısız:\n" + ex.Message, "Telegram Ayarları", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            _findChat.Enabled = !busy;
            _test.Enabled = !busy;
            _save.Enabled = !busy;
        }
    }
}
