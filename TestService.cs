using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwineBot;
using SwineBot.Achievements;
using SwineBot.Model;

namespace SwineTests;

public class TryProgress
{
    public List<TestUser> Users { get; } = [];
    public int Day { get; set; }
    public int Hour { get; set; }
}

public class TestService(ILogger<TestService> Logger, Settings Settings, IBotMessageSender sender, IDateTimeNowProvider DtnProvider, IServiceScopeFactory spf) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mockSender = (MockBotMessageSender)sender;
        Console.WriteLine();

        var tryProgress = new TryProgress();

        foreach (var strategy in Enum.GetValues<TestUserStrategy>())
        {
            tryProgress.Users.Add(new TestUser(strategy));
        }

        var start = DateTime.UtcNow;
        for (int d = 0; d < Settings.DaysCount; ++d)
        {
            tryProgress.Day = d;

            for (int h = 0; h < 24; ++h)
            {
                stoppingToken.ThrowIfCancellationRequested();
                tryProgress.Hour = h;

                Console.WriteLine($"Day {tryProgress.Day + 1}/{Settings.DaysCount}, hour {tryProgress.Hour + 1}/{24}");

                var dt = start.AddDays(d).AddHours(h);
                ((MockDateTimeNowProvider)DtnProvider).UtcNow = dt;

                foreach (var user in tryProgress.Users)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    using var scope = spf.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<UserContext>();
                    var achievController = scope.ServiceProvider.GetRequiredService<AchievementController>();

                    Telegram.Bot.Types.Update update;

                    if (user.Strategy != TestUserStrategy.RegularNoOverfeed
                        && d != 0 && d < 730 && d % 60 == 0 && h == 0) 
                        update = user.GenerateSlaughter();
                    else
                        update = user.TryGenerateFeed(context, achievController, dt);

                    if (update is null)
                        continue;

                    var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

                    var updateHandleResult = await updateHandler.Handle(update, CancellationToken.None);
                    if (updateHandleResult == UpdateHandleResult.OK)
                    {
                        Console.WriteLine(mockSender.MessagesHistory.Last().Text);
                        continue;
                    }

                    Console.Error.WriteLine($"Failed to handle update: {updateHandleResult.ToString()}");
                    Console.Error.WriteLine("Press any key to continue...");
                }
            }
        }

        // Results
        var firstUser = tryProgress.Users.First();
        var topUpdate = firstUser.GenerateTop();
        var historyUpdate = firstUser.GenerateHistory();

        {
            using var scope = spf.CreateScope();
            var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();
            await updateHandler.Handle(topUpdate, CancellationToken.None);
            Console.WriteLine(mockSender.MessagesHistory.Last().Text);

            await updateHandler.Handle(historyUpdate, CancellationToken.None);
            var bytes = mockSender.MessagesHistory.Last().PhotoBytes;

            var tmpPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), ".png"));
            using (var s = File.OpenWrite(tmpPath))
                s.Write(bytes, 0, bytes.Length);

            Console.WriteLine("Graph: " + tmpPath);
        }

    }
}

