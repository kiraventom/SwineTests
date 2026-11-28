using SwineBot;
using SwineBot.Senders;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace SwineTests;

/// <summary>
/// Collects the toasts the bot would have shown on inline button presses.
/// </summary>
public class MockCallbackQueryAnswerer : ICallbackQueryAnswerer
{
    public List<string> Answers { get; } = [];

    public Task Answer(string callbackQueryId, string text = null)
    {
        Answers.Add(text);
        return Task.CompletedTask;
    }
}

public record EditedMessage(long ChatId, int MessageId, string Text, int Buttons);

/// <summary>
/// Collects message edits instead of calling Telegram. Needed by duel callbacks,
/// which rewrite the request message with its outcome.
/// </summary>
public class MockMessageEditor : IMessageEditor
{
    public List<EditedMessage> Edited { get; } = [];

    public Task EditReplyMarkup(long chatId, int messageId, InlineKeyboardMarkup markup)
    {
        Edited.Add(new EditedMessage(chatId, messageId, null, markup?.InlineKeyboard?.Sum(r => r.Count()) ?? 0));
        return Task.CompletedTask;
    }

    public Task RemoveReplyMarkup(long chatId, int messageId)
    {
        Edited.Add(new EditedMessage(chatId, messageId, null, 0));
        return Task.CompletedTask;
    }

    public Task EditText(long chatId, int messageId, string text)
    {
        Edited.Add(new EditedMessage(chatId, messageId, text, -1));
        return Task.CompletedTask;
    }

    public Task EditMessage(long chatId, int messageId, SwineBot.BotMessages.IBotMessage message)
    {
        var buttons = message.Keyboard?.InlineKeyboard?.Sum(r => r.Count()) ?? 0;
        Edited.Add(new EditedMessage(chatId, messageId, message.Text, buttons));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Подменяет отправку меню команд в Telegram: сценарий проверяет, что именно ушло бы,
/// и что недоступность api.telegram.org не роняет старт бота.
/// </summary>
public class MockCommandMenuSender : ICommandMenuSender
{
    public List<IReadOnlyList<BotCommand>> Sent { get; } = [];

    /// <summary>Если задано, отправка падает этой ошибкой — так проверяется обработка сбоя</summary>
    public Exception Fail { get; set; }

    public Task SetCommands(IReadOnlyList<BotCommand> commands, CancellationToken token)
    {
        if (Fail is not null)
            throw Fail;

        Sent.Add(commands);
        return Task.CompletedTask;
    }
}
