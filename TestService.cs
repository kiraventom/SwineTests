using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwineBot;
using SwineBot.Achievements;
using SwineBot.Actions.Commands.Duel;
using SwineBot.Model;
using SwineBot.Updates;
using Telegram.Bot.Types;

namespace SwineTests;

public class TryProgress
{
    public List<TestUser> Users { get; } = [];
    public int Day { get; set; }
    public int Hour { get; set; }
}

public class TestService(ILogger<TestService> Logger, Settings Settings, MockBotMessageSender Sender,
    MockCallbackQueryAnswerer Answerer, MockMessageEditor Editor, IDateTimeNowProvider DtnProvider,
    MockCommandMenuSender CommandMenuSender, IServiceScopeFactory spf, IHostApplicationLifetime lifetime) : BackgroundService
{
    private const long GROUP_TG_ID = TestUser.GroupTelegramId;

    private readonly List<string> _failures = [];

    private int _duelAttempts;
    private int _duelAnswers;

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
        var start = DateTime.UtcNow;

        ((MockDateTimeNowProvider)DtnProvider).UtcNow = start;

        foreach (var strategy in Enum.GetValues<TestUserStrategy>())
            tryProgress.Users.Add(new TestUser(strategy));

        // Меню команд базе не нужно: шаг идёт первым и ничего не портит дуэльным сценариям
        var commandMenuScenarios = new CommandMenuScenarios(spf, CommandMenuSender);
        _failures.AddRange(await commandMenuScenarios.Run(stoppingToken));

        var scenarios = new DuelScenarios(spf, Sender, Answerer, Editor, DtnProvider, tryProgress.Users);
        _failures.AddRange(await scenarios.Run(stoppingToken));

        // Сценарии оставили после себя дуэли, кормёжки и ачивки: длинная симуляция
        // должна начинаться с чистого листа, иначе кулдауны и веса будут чужими
        await ResetSimulationState();

        ((MockDateTimeNowProvider)DtnProvider).UtcNow = start;

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

                    if (update is not null)
                    {
                        // Если действие съел неотвеченный вызов — отвечаем и повторяем,
                        // как сделал бы живой игрок
                        var wasBlockedByDuel = await Handle(user, update, stoppingToken);
                        if (wasBlockedByDuel)
                            await Handle(user, update, stoppingToken);
                    }

                    if (Settings.DuelChance > 0 && Random.Shared.NextDouble() < Settings.DuelChance)
                        await TryStartDuel(user, stoppingToken);
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

    /// <returns>true, если вместо действия бот прислал напоминание о висящем вызове</returns>
    private async Task<bool> Handle(TestUser user, Telegram.Bot.Types.Update update, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

        var messagesBefore = Sender.MessagesHistory.Count;
        var updateHandleResult = await updateHandler.Handle(update, token);

        if (!IsSuccess(updateHandleResult))
        {
            _failures.Add($"{user.Strategy}: {updateHandleResult}");
            Console.Error.WriteLine($"{user.Strategy}: failed to handle update: {updateHandleResult}");
            return false;
        }

        var newMessages = Sender.MessagesHistory.Skip(messagesBefore).ToList();

        if (Settings.Verbose)
        {
            foreach (var message in newMessages)
                Console.WriteLine($"  {message.Text}");
        }

        // Защитник упёрся в неотвеченный вызов — отвечаем на него его же руками
        if (!newMessages.Any(m => m is SwineBot.BotMessages.Duels.DuelReminderMessage))
            return false;

        await AnswerPendingDuel(user, token);
        return true;
    }

    /// <summary>
    /// Long simulation of duels: a random valid opponent from the same group, then the
    /// defender answers whenever their next action hits the reminder.
    /// </summary>
    private async Task TryStartDuel(TestUser user, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();
        var now = ((MockDateTimeNowProvider)DtnProvider).UtcNow;

        var challenger = await GetSwine(context, user);
        if (!await CanDuel(context, challenger.SwineId, now))
            return;

        var targetTelegramId = await FindOpponent(context, challenger, now);
        if (targetTelegramId is null)
            return;

        var updateHandler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();
        var result = await updateHandler.Handle(user.GenerateCommand($"/duel {targetTelegramId}"), token);
        ++_duelAttempts;

        if (!IsSuccess(result))
            _failures.Add($"{user.Strategy} /duel: {result}");
    }

    private static async Task<bool> CanDuel(UserContext context, int swineId, DateTime now)
    {
        var cooldownStart = now.AddHours(-DuelBalance.DefendCooldownHours);

        var onCooldown = await context.DuelResults.AnyAsync(r =>
            (r.AttackerId == swineId || r.DefenderId == swineId) && r.DateTime > cooldownStart);
        if (onCooldown)
            return false;

        var isBusy = await context.DuelRequests.AnyAsync(dr => dr.AttackerId == swineId || dr.DefenderId == swineId);
        return !isBusy;
    }

    private static async Task<long?> FindOpponent(UserContext context, Swine challenger, DateTime now)
    {
        var cooldownStart = now.AddHours(-DuelBalance.DefendCooldownHours);
        var requestTtlStart = now - DuelRequestExtensions.TTL;

        var candidates = await context.Swines
            .Where(s => s.GroupId == challenger.GroupId)
            .Where(s => s.SwineId != challenger.SwineId)
            .Where(s => s.Weight > 1)
            .ToListAsync();

        if (candidates.Count == 0)
            return null;

        var busySwines = (await context.DuelRequests
                .Where(dr => dr.DateTime > requestTtlStart)
                .Select(dr => dr.DefenderId)
                .ToListAsync())
            .ToHashSet();

        var recentDuels = await context.DuelResults
            .Where(r => r.DateTime > cooldownStart)
            .Select(r => new { r.AttackerId, r.DefenderId })
            .ToListAsync();

        var onCooldownSwines = recentDuels
            .SelectMany(r => new[] { r.AttackerId, r.DefenderId })
            .Where(id => id.HasValue)
            .Select(id => id.Value)
            .ToHashSet();

        var free = candidates
            .Where(s => !busySwines.Contains(s.SwineId) && !onCooldownSwines.Contains(s.SwineId))
            .ToList();

        if (free.Count == 0)
            return null;

        var opponentSwine = free[Random.Shared.Next(free.Count)];
        return await context.Users
            .Where(u => u.UserId == opponentSwine.OwnerId)
            .Select(u => (long?)u.TelegramId)
            .FirstOrDefaultAsync();
    }

    private async Task AnswerPendingDuel(TestUser user, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        var swine = await GetSwine(context, user);
        var request = await context.DuelRequests.AsNoTracking().FirstOrDefaultAsync(dr => dr.DefenderId == swine.SwineId);
        if (request is null)
            return;

        var accept = Random.Shared.NextDouble() < 0.7;
        var handler = scope.ServiceProvider.GetRequiredService<CallbackQueryHandler>();

        var query = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString(),
            From = new Telegram.Bot.Types.User { Id = user.TelegramId, FirstName = user.Strategy.ToString() },
            Data = $"{(accept ? "accept" : "decline")}_{request.RequestId}",
            Message = new Message { Id = request.MessageId ?? 1, Chat = new Chat { Id = GROUP_TG_ID } },
        };

        var result = await handler.Handle(query, token);
        ++_duelAnswers;

        if (result is not UpdateHandleResult.CallbackQueryOK)
            _failures.Add($"{user.Strategy} callback: {result}");
    }

    private static async Task<Swine> GetSwine(UserContext context, TestUser user)
    {
        var dbUser = await context.Users.FirstAsync(u => u.TelegramId == user.TelegramId);
        return await context.Swines.FirstAsync(s => s.OwnerId == dbUser.UserId);
    }

    private static bool IsSuccess(UpdateHandleResult result)
        => result is UpdateHandleResult.MessageOK
            or UpdateHandleResult.InlineQueryOK
            or UpdateHandleResult.CallbackQueryOK
            or UpdateHandleResult.MessageSuccesfulMigration;

    private async Task ResetSimulationState()
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        await context.Database.ExecuteSqlRawAsync("DELETE FROM DuelRequests");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM DuelResults");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM Feeds");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM WeightLosses");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM Achievements");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM Slaughters");
        await context.Database.ExecuteSqlRawAsync("UPDATE Swines SET Weight = 1");

        Console.WriteLine();
        Console.WriteLine("=== Long simulation ===");
    }

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
        Console.WriteLine($"Duels started: {_duelAttempts}, answers: {_duelAnswers}");

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

        // Вес сравнивать честно можно только без дуэлей: дуэли перетасовывают вес между свинами
        if (Settings.DuelChance <= 0)
        {
            var regularWeight = await GetWeight(context, TestUserStrategy.RegularNoOverfeed);
            var greedyWeight = await GetWeight(context, TestUserStrategy.Overfeed4);
            Check("спокойное кормление набирает вес лучше жадного", regularWeight > greedyWeight,
                $"Regular={regularWeight}, Overfeed4={greedyWeight}");
        }
        else
        {
            Console.WriteLine("SKIP спокойное кормление набирает вес лучше жадного: включены дуэли");
        }

        var slaughterStrategies = Enum.GetValues<TestUserStrategy>().Where(s => s != TestUserStrategy.RegularNoOverfeed);
        var slaughterCounts = new List<int>();
        foreach (var strategy in slaughterStrategies)
            slaughterCounts.Add(await GetSlaughters(context, strategy));

        var expectedSlaughters = Settings.NoSlaughter ? 0 : Settings.DaysCount / 60;
        Check("убийства идут каждые 60 дней", slaughterCounts.All(c => c == expectedSlaughters), string.Join(", ", slaughterCounts));

        // Дуэли: ставка всегда симметрична, вес не появляется из воздуха
        var duelResults = await context.DuelResults.ToListAsync();
        Check("у каждой дуэли ставка одинаковая с двух сторон",
            duelResults.All(r => r.WinnerWeightAfter - r.WinnerWeightBefore == r.LoserWeightBefore - r.LoserWeightAfter),
            $"{duelResults.Count} записей");
        Check("ставка не отрицательная", duelResults.All(r => r.WinnerWeightAfter >= r.WinnerWeightBefore));
        Check("дуэли шли, если они включены", Settings.DuelChance <= 0 || duelResults.Count > 0, $"{duelResults.Count} записей");

        var expiredRequests = await context.DuelRequests
            .CountAsync(dr => dr.DateTime < ((MockDateTimeNowProvider)DtnProvider).UtcNow - DuelRequestExtensions.TTL);
        Check("не осталось просроченных вызовов", expiredRequests == 0, $"{expiredRequests}");

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
}
