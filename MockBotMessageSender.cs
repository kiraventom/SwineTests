using SwineBot;
using SwineBot.BotMessages;
using Telegram.Bot.Types;

namespace SwineTests;

public class MockBotMessageSender : IBotMessageSender
{
    private readonly List<IBotMessage> _messages = [];
    public IReadOnlyList<IBotMessage> MessagesHistory => _messages;

    public Task<Message> Send(SwineBot.Update update, IBotMessage botMessage)
    {
        _messages.Add(botMessage);
        return Task.FromResult<Message>(null);
    }
}

