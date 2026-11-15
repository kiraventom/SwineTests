using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwineBot;
using SwineBot.Achievements;
using SwineBot.Model;
using SwineBot.Updates;

namespace SwineTests;

public class TryProgress
{
    public List<TestUser> Users { get; } = [];
    public int Day { get; set; }
    public int Hour { get; set; }
}

public class TestService(ILogger<TestService> Logger, Settings Settings, MockBotMessageSender Sender, IDateTimeNowProvider DtnProvider, IServiceScopeFactory spf, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly List<string> _failures = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Run(stoppingToken);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Test run failed");
            _failures.Add(e.Message);
        }
        finally
        {
            Environment.ExitCode = _failures.Count == 0 ? 0 : 1;
            lifetime.StopApplication();
        }
    }

    private async Task Run(CancellationToken stoppingToken)
    {
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

                if (h == 0)
                    Console.WriteLine($"Day {tryProgress.Day + 1}/{Settings.DaysCount}");

                var dt = start.AddDays(d).AddHours(h);
                ((MockDateTimeNowProvider)DtnProvider).UtcNow = dt;

                foreach (var user in tryProgress.Users)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    using var scope = spf.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<UserContext>();
                    var achievController = scope.ServiceProvider.GetRequiredService<AchievementController>();

                    Telegram.Bot.Types.Update update;

                    if (!Settings.NoSlaughter
                        && user.Strategy != TestUserStrategy.RegularNoOverfeed
                        && d != 0 && d < 730 && d % 60 == 0 && h == 0)
                        update = user.GenerateSlaughter();
                    else
                        update = user.TryGenerateFeed(context, achievController, dt);

                    if (update is null)
                        continue;

                    var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

                    var messagesBefore = Sender.MessagesHistory.Count;
                    var updateHandleResult = await updateHandler.Handle(update, CancellationToken.None);

                    if (!IsSuccess(updateHandleResult))
                    {
                        _failures.Add($"{user.Strategy}: {updateHandleResult}");
                        Console.Error.WriteLine($"{user.Strategy}: failed to handle update: {updateHandleResult}");
                        continue;
                    }

                    if (!Settings.Verbose || Sender.MessagesHistory.Count == messagesBefore)
                        continue;

                    foreach (var message in Sender.MessagesHistory.Skip(messagesBefore))
                        Console.WriteLine($"  {message.Text}");
                }
            }
        }

        await PrintSummary();
        await PrintTop(stoppingToken);
        var graphBytes = PrintHistory(stoppingToken);
        await CheckInvariants(graphBytes);

        Console.WriteLine();
        if (_failures.Count == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED");
        }
        else
        {
            Console.WriteLine($"FAILURES: {_failures.Count}");
            foreach (var failure in _failures.Take(20))
                Console.WriteLine("  " + failure);
        }
    }

    /// <summary>
    /// Cheap sanity checks of the simulation itself: they do not know the exact numbers
    /// (the bot is random), but they catch broken pipelines and inverted rules.
    /// </summary>
    private async Task CheckInvariants(byte[] graphBytes)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        Console.WriteLine();
        Console.WriteLine("=== Invariants ===");

        var swines = await context.Swines.ToListAsync();
        Check("свины не улетели в отрицательный вес", swines.All(s => s.Weight >= 1),
            string.Join(", ", swines.Select(s => $"{s.Name}={s.Weight}")));

        var noOverfeedInfoId = await GetInfoId(context, TestUserStrategy.RegularNoOverfeed);
        var overfeedAchievements = await context.Achievements
            .CountAsync(a => a.SwineInfoId == noOverfeedInfoId && a.Type == AchievementType.Overfeed);
        Check("стратегия без перекорма не получила ачивку Overfeed", overfeedAchievements == 0, $"Overfeed x{overfeedAchievements}");

        var regularThrowups = await GetThrowups(context, TestUserStrategy.RegularNoOverfeed);
        var greedyThrowups = await GetThrowups(context, TestUserStrategy.Overfeed4);
        Check("жадная стратегия блюёт чаще спокойной", greedyThrowups > regularThrowups,
            $"Overfeed4={greedyThrowups}, Regular={regularThrowups}");

        var regularWeight = await GetWeight(context, TestUserStrategy.RegularNoOverfeed);
        var greedyWeight = await GetWeight(context, TestUserStrategy.Overfeed4);
        Check("спокойное кормление набирает вес лучше жадного", regularWeight > greedyWeight,
            $"Regular={regularWeight}, Overfeed4={greedyWeight}");

        var slaughterStrategies = Enum.GetValues<TestUserStrategy>().Where(s => s != TestUserStrategy.RegularNoOverfeed);
        var slaughterCounts = new List<int>();
        foreach (var strategy in slaughterStrategies)
            slaughterCounts.Add(await GetSlaughters(context, strategy));

        var expectedSlaughters = Settings.NoSlaughter ? 0 : Settings.DaysCount / 60;
        Check("убийства идут каждые 60 дней", slaughterCounts.All(c => c == expectedSlaughters), string.Join(", ", slaughterCounts));

        Check("/history построил график", graphBytes is { Length: > 1024 }, $"{graphBytes?.Length ?? 0} bytes");
    }

    private void Check(string name, bool ok, string details = null)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(details is null ? "" : $"  <- {details}")}");
        if (!ok)
            _failures.Add(name);
    }

    private static async Task<int> GetInfoId(UserContext context, TestUserStrategy strategy)
    {
        var telegramId = TestUser.GetTelegramId(strategy);
        var user = await context.Users.FirstAsync(u => u.TelegramId == telegramId);
        var swine = await context.Swines.FirstAsync(s => s.OwnerId == user.UserId);
        return (await context.Infos.FirstAsync(i => i.SwineId == swine.SwineId)).InfoId;
    }

    private static async Task<int> GetThrowups(UserContext context, TestUserStrategy strategy)
    {
        var telegramId = TestUser.GetTelegramId(strategy);
        var user = await context.Users.FirstAsync(u => u.TelegramId == telegramId);
        var swine = await context.Swines.FirstAsync(s => s.OwnerId == user.UserId);
        return await context.WeightLosses.CountAsync(w => w.SwineId == swine.SwineId && w.IsThrowUp);
    }

    private static async Task<int> GetWeight(UserContext context, TestUserStrategy strategy)
    {
        var telegramId = TestUser.GetTelegramId(strategy);
        var user = await context.Users.FirstAsync(u => u.TelegramId == telegramId);
        return (await context.Swines.FirstAsync(s => s.OwnerId == user.UserId)).Weight;
    }

    private static async Task<int> GetSlaughters(UserContext context, TestUserStrategy strategy)
    {
        var telegramId = TestUser.GetTelegramId(strategy);
        var user = await context.Users.FirstAsync(u => u.TelegramId == telegramId);
        var swine = await context.Swines.FirstAsync(s => s.OwnerId == user.UserId);
        return await context.Slaughters.CountAsync(s => s.UserId == swine.OwnerId && s.GroupId == swine.GroupId);
    }

    private static bool IsSuccess(UpdateHandleResult result)
        => result is UpdateHandleResult.MessageOK
            or UpdateHandleResult.InlineQueryOK
            or UpdateHandleResult.CallbackQueryOK
            or UpdateHandleResult.MessageSuccesfulMigration;

    private async Task PrintSummary()
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"{"strategy",-20} {"swine",-20} {"weight",6} {"feeds",6} {"throws",6} {"slaught",7} {"achiev",6} {"duels",7} {"decl",5}");

        foreach (var strategy in Enum.GetValues<TestUserStrategy>())
        {
            var telegramId = TestUser.GetTelegramId(strategy);
            var user = await context.Users.FirstAsync(u => u.TelegramId == telegramId);
            var swine = await context.Swines.FirstAsync(s => s.OwnerId == user.UserId);
            var info = await context.Infos.FirstAsync(i => i.SwineId == swine.SwineId);

            var feeds = await context.Feeds.CountAsync(f => f.SwineId == swine.SwineId);
            var throwups = await context.WeightLosses.CountAsync(w => w.SwineId == swine.SwineId && w.IsThrowUp);
            var slaughters = await context.Slaughters.CountAsync(s => s.UserId == swine.OwnerId && s.GroupId == swine.GroupId);
            var achievements = await context.Achievements.CountAsync(a => a.SwineInfoId == info.InfoId);

            var fights = context.DuelResults.Where(r => !r.IsDecline);
            var wins = await fights.CountAsync(r => r.AttackerWon && r.AttackerId == swine.SwineId || !r.AttackerWon && r.DefenderId == swine.SwineId);
            var losses = await fights.CountAsync(r => r.AttackerWon && r.DefenderId == swine.SwineId || !r.AttackerWon && r.AttackerId == swine.SwineId);
            var declines = await context.DuelResults.CountAsync(r => r.IsDecline && (r.AttackerId == swine.SwineId || r.DefenderId == swine.SwineId));

            Console.WriteLine($"{strategy,-20} {swine.Name,-20} {swine.Weight,6} {feeds,6} {throwups,6} {slaughters,7} {achievements,6} {$"{wins}/{losses}",7} {declines,5}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Achievements by type ===");
        foreach (var strategy in Enum.GetValues<TestUserStrategy>())
        {
            var telegramId = TestUser.GetTelegramId(strategy);
            var infoId = await context.Infos
                .Where(i => i.SwineId == context.Swines.First(s => s.OwnerId == context.Users.First(u => u.TelegramId == telegramId).UserId).SwineId)
                .Select(i => i.InfoId)
                .FirstAsync();

            var groups = await context.Achievements
                .Where(a => a.SwineInfoId == infoId)
                .GroupBy(a => a.Type)
                .Select(g => new { Type = g.Key, Count = g.Count(), MaxValue = g.Max(a => a.Value) })
                .ToListAsync();

            var line = groups.Count == 0
                ? "none"
                : string.Join(", ", groups.OrderBy(g => g.Type).Select(g => $"{g.Type} x{g.Count} (max {g.MaxValue})"));

            Console.WriteLine($"{strategy,-20} {line}");
        }
    }

    private async Task PrintTop(CancellationToken stoppingToken)
    {
        var firstUser = new TestUser(TestUserStrategy.RegularNoOverfeed);

        using var scope = spf.CreateScope();
        var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

        var messagesBefore = Sender.MessagesHistory.Count;
        await updateHandler.Handle(firstUser.GenerateTop(), stoppingToken);

        Console.WriteLine();
        Console.WriteLine("=== /top ===");
        foreach (var message in Sender.MessagesHistory.Skip(messagesBefore))
            Console.WriteLine(message.Text);
    }

    private byte[] PrintHistory(CancellationToken stoppingToken)
    {
        var firstUser = new TestUser(TestUserStrategy.RegularNoOverfeed);

        using var scope = spf.CreateScope();
        var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

        var messagesBefore = Sender.MessagesHistory.Count;
        updateHandler.Handle(firstUser.GenerateHistory(), stoppingToken).GetAwaiter().GetResult();

        var message = Sender.MessagesHistory.Skip(messagesBefore).LastOrDefault();
        var bytes = message?.PhotoBytes;
        if (bytes is null)
        {
            Console.WriteLine();
            Console.WriteLine("=== /history: no photo produced ===");
            return null;
        }

        var tmpPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), ".png"));
        using (var s = File.OpenWrite(tmpPath))
            s.Write(bytes, 0, bytes.Length);

        Console.WriteLine();
        Console.WriteLine("=== /history graph: " + tmpPath);
        return bytes;
    }
}
