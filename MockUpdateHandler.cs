using System.Text;
using Microsoft.Extensions.Logging;
using SwineBot;
using SwineBot.BotMessages;
using SwineBot.Model;
using Telegram.Bot.Types;

namespace SwineTests;

public class MockUpdateHandler(ILogger<MockUpdateHandler> logger, UserContext context, IBotMessageSender Sender, IMessageFactory factory) : IUpdateHandler
{
    public async Task Handle(Update update, CancellationToken token)
    {
        var chat = update.Message.Chat;
        var sender = update.Message.From;

        var user = context.GetOrAddUser(chat.Id, chat.Title, sender.Id, sender.FirstName, sender.Username);

        switch (update.Message.Text)
        {
            case "/feed":
                var feedMessage = factory.Create<FeedMessage>();
                await Sender.Send(context, chat.Id, user.UserId, feedMessage);
                break;

        }
    }
}

