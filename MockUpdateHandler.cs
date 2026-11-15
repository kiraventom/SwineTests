using SwineBot.Updates;

namespace SwineTests;

/// <summary>
/// Not a real handler: kept as a compiling stub for cases when a test needs
/// to register <see cref="IUpdateHandler"/> without running the bot pipeline.
/// </summary>
public class MockUpdateHandler : IUpdateHandler
{
    public Task<UpdateHandleResult> Handle(Telegram.Bot.Types.Update update, CancellationToken token)
        => throw new NotImplementedException();
}
