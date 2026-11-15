using SwineBot.BotMessages;
using SwineBot.Senders;
using Telegram.Bot.Types;
using Update = SwineBot.Updates.Update;

namespace SwineTests;

/// <summary>
/// Collects everything the bot would have sent to Telegram. Message ids are fake but
/// non-null: commands like /duel store the id of the sent message in the database.
/// </summary>
public class MockBotMessageSender : IBotMessageSender
{
    private readonly List<IBotMessage> _messages = [];
    private int _nextMessageId = 1000;

    public IReadOnlyList<IBotMessage> MessagesHistory => _messages;

    public Task<Message> Send(Update update, IBotMessage botMessage)
    {
        _messages.Add(botMessage);

        var chatId = botMessage is ICustomRecepientMessage { CustomRecepient: { } recepient }
            ? recepient.ChatId
            : update.Recepient.ChatId;

        return Task.FromResult(new Message { Id = _nextMessageId++, Chat = new Chat { Id = chatId } });
    }
}
