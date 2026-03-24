using SwineBot;
using SwineBot.BotMessages;
using SwineBot.Model;
using Telegram.Bot.Types;

namespace SwineTests;

public class MockBotMessageSender : IBotMessageSender
{
    public event BeforeMessageSendDelegate BeforeMessageSend;

    public async Task<Message> Send(UserContext context, ChatId chatId, int userId, BotMessage botMessage)
    {
        await botMessage.Init(context, chatId, userId);

        if (BeforeMessageSend is not null)
            BeforeMessageSend?.Invoke(context, chatId, userId, botMessage);

        return null;
    }
}

