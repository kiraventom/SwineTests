using System.Collections.Concurrent;
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

public class TestService(ILogger<TestService> Logger, Settings Settings, IServiceScopeFactory spf) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = spf.CreateScope())
        {
            var messageSender = scope.ServiceProvider.GetRequiredService<IBotMessageSender>();
            var achievController = scope.ServiceProvider.GetRequiredService<IAchievementController>();
            messageSender.BeforeMessageSend += achievController.OnBeforeMessageSend;
        }

        var tries = new TryProgress[Settings.TriesCount];
        for (int i = 0; i < tries.Length; ++i)
            tries[i] = new TryProgress();

        Console.WriteLine();
        var top = Console.CursorTop;

        string log = string.Empty;

        for (int t = 0; t < Settings.TriesCount; ++t)
        {
            var tryProgress = tries[t];

            foreach (var strategy in Enum.GetValues<TestUserStrategy>())
            {
                for (int u = 0; u < Settings.SwinesCount; ++u)
                {
                    tryProgress.Users.Add(new TestUser(strategy));
                }
            }

            var start = DateTime.UtcNow;
            for (int d = 0; d < Settings.DaysCount; ++d)
            {
                tryProgress.Day = d;
                for (int h = 0; h < 24; ++h)
                {
                    tryProgress.Hour = h;
                    stoppingToken.ThrowIfCancellationRequested();

                    Console.SetCursorPosition(0, top + t);
                    Console.Write(new string(' ', log.Length));
                    log = $"Try {t + 1}/{Settings.TriesCount}, day {tryProgress.Day + 1}/{Settings.DaysCount}, hour {tryProgress.Hour + 1}/{24}";
                    Console.SetCursorPosition(0, top + t);
                    Console.Write(log);

                    var dt = start.AddDays(d).AddHours(h);
                    foreach (var user in tryProgress.Users)
                    {
                        var update = user.TryGenerateUpdate(dt);

                        if (update != null)
                        {
                            using var scope = spf.CreateScope();
                            var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();
                            var context = scope.ServiceProvider.GetRequiredService<UserContext>();

                            updateHandler.Handle(update, CancellationToken.None).Wait();

                            int amount;
                            var feed = context.Feeds.OrderByDescending(f => f.DateTime).FirstOrDefault();
                            if (feed is null)
                            {
                                var weightLoss = context.WeightLosses.OrderByDescending(wl => wl.DateTime).First();
                                amount = weightLoss.Amount;
                            }
                            else
                            {
                                amount = feed.Amount;
                            }

                            user.FeedLog.Add(new FeedRecord(dt, amount));
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Runs: {Settings.TriesCount}");
        Console.WriteLine($"Days: {Settings.DaysCount}");
        Console.WriteLine($"Swines: {Settings.SwinesCount * Enum.GetValues<TestUserStrategy>().Count()}");

        var results = Enum.GetValues<TestUserStrategy>()
            .Select(s => new
            {
                Strategy = s,
                Averages = tries
                    .Select(t => t.Users.Where(u => u.Strategy == s).ToList())
                    .Where(users => users.Any())
                    .Select(users => users.Average(u => u.Weight))
            });

        string format = "{0, -25}" + string.Join(string.Empty, tries.Select((_, i) => $"{{Run #{i + 1}, -10}}")) + $"{{{tries.Length + 1},-10}}";

        var headerContent = new List<string>() { "Strategy" };
        headerContent.AddRange(Enumerable.Range(1, tries.Length).Select(i => i.ToString()));
        headerContent.Add("Total");
        var header = string.Format(format, headerContent.ToArray());
        Console.WriteLine(header);

        foreach (var result in results)
        {
            var resultContent = new List<string>();
            resultContent.Add(result.Strategy.ToString());
            resultContent.AddRange(result.Averages.Select(a => a.ToString()));
            resultContent.Add(result.Averages.Average().ToString());

            var line = string.Format(format, resultContent.ToArray());
            Console.WriteLine(line);
        }
    }
}

