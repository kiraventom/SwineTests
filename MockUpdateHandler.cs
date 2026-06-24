using Microsoft.Extensions.Logging;
using SwineBot;
using SwineBot.BotMessages;
using SwineBot.Model;

namespace SwineTests;

public class MockUpdateHandler(ILogger<MockUpdateHandler> logger, UserContext context, IBotMessageSender Sender, IMessageFactory factory) : IUpdateHandler
{
    public Task<UpdateHandleResult> Handle(Telegram.Bot.Types.Update update, CancellationToken token)
    {
        throw new NotImplementedException();
    }
}

