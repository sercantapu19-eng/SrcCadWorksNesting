namespace SrcCadWorks.Models
{
    public sealed class TelegramSettings
    {
        public bool Enabled { get; set; }
        public string BotToken { get; set; }
        public string ChatId { get; set; }
        public bool SendStartMessage { get; set; } = true;
        public bool SendCompletionMessage { get; set; } = true;
        public bool EnableStatusCommand { get; set; } = true;

        public TelegramSettings Clone()
        {
            return new TelegramSettings
            {
                Enabled = Enabled,
                BotToken = BotToken,
                ChatId = ChatId,
                SendStartMessage = SendStartMessage,
                SendCompletionMessage = SendCompletionMessage,
                EnableStatusCommand = EnableStatusCommand
            };
        }

        public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
    }
}
