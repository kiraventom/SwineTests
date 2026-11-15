using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwineBot;
using SwineBot.Actions.Commands.Duel;
using SwineBot.Model;
using SwineBot.Updates;
using Telegram.Bot.Types;

namespace SwineTests;

/// <summary>
/// Реплей прод-беседы: те же свины, те же паттерны кормления и забоев, 90 виртуальных дней.
/// Дуэли — «малыши агрессируют на толстяков», готовность толстяка согласиться задаётся
/// параметром <see cref="Settings.DuelConsent"/>.
/// </summary>
public class ReplayService(ILogger<ReplayService> Logger, Settings Settings, IDateTimeNowProvider DtnProvider,
    IServiceScopeFactory spf, IHostApplicationLifetime lifetime) : BackgroundService
{
    private const long GroupTelegramId = -1001337;

    private readonly List<string> _failures = [];
    private readonly Dictionary<long, DateTime> _nextSession = [];
    private readonly Dictionary<long, DateTime> _nextSlaughter = [];

    private int _duelsStarted;
    private int _accepted;
    private int _declined;
    private int _feeds;
    private int _slaughters;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Run(stoppingToken);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Replay failed");
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
        var start = DateTime.UtcNow;
        ((MockDateTimeNowProvider)DtnProvider).UtcNow = start;

        await Seed();

        Console.WriteLine($"=== Replay: {ProdProfile.All.Length} свинов, {Settings.DaysCount} дней, " +
                          $"дуэли {(Settings.DuelChance <= 0 ? "выключены" : $"включены, согласие {Settings.DuelConsent:P0}")}");

        foreach (var profile in ProdProfile.All)
        {
            _nextSession[profile.TelegramId] = start.AddHours(Random.Shared.NextDouble() * profile.GapHours);
            if (profile.SlaughterEveryDays > 0)
                _nextSlaughter[profile.TelegramId] = start.AddDays(profile.SlaughterEveryDays);
        }

        for (int hour = 0; hour < Settings.DaysCount * 24; ++hour)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var now = start.AddHours(hour);
            ((MockDateTimeNowProvider)DtnProvider).UtcNow = now;

            if (hour % 24 == 0)
                Console.WriteLine($"Day {hour / 24 + 1}/{Settings.DaysCount}");

            foreach (var profile in ProdProfile.All)
            {
                // Забой по расписанию владельца
                if (profile.SlaughterEveryDays > 0 && now >= _nextSlaughter[profile.TelegramId])
                {
                    _nextSlaughter[profile.TelegramId] = now.AddDays(profile.SlaughterEveryDays);
                    await Act(profile, "/slaughter yes", stoppingToken);
                }

                // Сессия кормления: несколько кормёжек подряд
                if (now < _nextSession[profile.TelegramId])
                    continue;

                var feeds = RollFeedsPerSession(profile.FeedsPerSession);
                for (int i = 0; i < feeds; ++i)
                    await Act(profile, "/feed", stoppingToken);

                _nextSession[profile.TelegramId] = now.AddHours(profile.GapHours * (0.75 + Random.Shared.NextDouble() * 0.5));
            }

            if (Settings.DuelChance > 0)
                await RunDuels(now, stoppingToken);
        }

        await PrintSummary();
    }

    /// <summary>Среднее число кормёжек за сессию: дробная часть — вероятность лишней кормёжки.</summary>
    private static int RollFeedsPerSession(double average)
    {
        var whole = (int)Math.Floor(average);
        return Random.Shared.NextDouble() < average - whole ? whole + 1 : Math.Max(1, whole);
    }

    private async Task Seed()
    {
        // Первый /feed от каждого создаёт пользователя, группу и свина
        foreach (var profile in ProdProfile.All)
            await Act(profile, "/feed", CancellationToken.None);

        await Exec(c =>
        {
            var swines = c.Swines.AsTracking().ToList();
            foreach (var profile in ProdProfile.All)
            {
                var user = c.Users.First(u => u.TelegramId == profile.TelegramId);
                var swine = swines.First(s => s.OwnerId == user.UserId);
                swine.Weight = profile.StartWeight;
                swine.Name = profile.Name;
            }
        });

        _feeds = 0;
        _slaughters = 0;

        // Исторические забои из прода: от них зависят и множитель кормления, и дебафф в дуэлях
        await Exec(c =>
        {
            foreach (var profile in ProdProfile.All.Where(p => p.PreSlaughters > 0))
            {
                var swine = c.Swines.First(s => s.OwnerId == c.Users.First(u => u.TelegramId == profile.TelegramId).UserId);
                var perOne = profile.PreSlaughterWeight / profile.PreSlaughters;

                for (int i = 0; i < profile.PreSlaughters; ++i)
                {
                    c.Slaughters.Add(new Slaughter
                    {
                        UserId = swine.OwnerId,
                        GroupId = swine.GroupId,
                        SwineName = $"{profile.Name} {i + 1}.0",
                        SwineWeight = perOne,
                        DateTime = ((MockDateTimeNowProvider)DtnProvider).UtcNow.AddDays(-90 - i * 10),
                    });
                }
            }
        });

        await Exec(c =>
        {
            foreach (var entity in c.Feeds.AsTracking().ToList()) c.Feeds.Remove(entity);
            foreach (var entity in c.WeightLosses.AsTracking().ToList()) c.WeightLosses.Remove(entity);
            foreach (var entity in c.Achievements.AsTracking().ToList()) c.Achievements.Remove(entity);
            foreach (var entity in c.Slaughters.AsTracking().ToList()) c.Slaughters.Remove(entity);
            foreach (var entity in c.DuelResults.AsTracking().ToList()) c.DuelResults.Remove(entity);
            foreach (var entity in c.DuelRequests.AsTracking().ToList()) c.DuelRequests.Remove(entity);
        });
    }

    /// <summary>Действие свина: если его съел висящий вызов — отвечаем и повторяем.</summary>
    private async Task Act(ProdProfile profile, string command, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            using var scope = spf.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

            var result = await handler.Handle(BuildUpdate(profile, command), token);
            if (result is not (UpdateHandleResult.MessageOK or UpdateHandleResult.MessageSuccesfulMigration))
            {
                _failures.Add($"{profile.Owner} {command}: {result}");
                return;
            }

            if (command == "/feed") ++_feeds;
            if (command == "/slaughter yes") ++_slaughters;

            if (!await AnswerPendingIfAny(profile, token))
                return;
        }
    }

    private async Task<bool> AnswerPendingIfAny(ProdProfile profile, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();
        var swine = await SwineOf(context, profile.TelegramId);

        var request = await context.DuelRequests.AsNoTracking()
            .FirstOrDefaultAsync(dr => dr.DefenderId == swine.SwineId, token);
        if (request is null)
            return false;

        var accept = Random.Shared.NextDouble() < Settings.DuelConsent;
        var handler = scope.ServiceProvider.GetRequiredService<CallbackQueryHandler>();

        var query = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString(),
            From = new Telegram.Bot.Types.User { Id = profile.TelegramId, FirstName = profile.Owner },
            Data = $"{(accept ? "accept" : "decline")}_{request.RequestId}",
            Message = new Message { Id = request.MessageId ?? 1, Chat = new Chat { Id = GroupTelegramId } },
        };

        var result = await handler.Handle(query, token);

        // Протухший или уже закрытый вызов — штатный исход, а не сбой обработки
        if (result is not (UpdateHandleResult.CallbackQueryOK
            or UpdateHandleResult.CallbackQueryDuelRequestExpired
            or UpdateHandleResult.CallbackQueryDuelRequestNotFound))
        {
            _failures.Add($"{profile.Owner} callback: {result}");
            return false;
        }

        if (accept) ++_accepted; else ++_declined;
        return true;
    }

    /// <summary>
    /// Малыш агрессирует на толстяка: чем больше относительная разница в весе,
    /// тем выше шанс вызова. Один вызов в сутки на свина плюс кулдаун самого бота.
    /// </summary>
    private async Task RunDuels(DateTime now, CancellationToken token)
    {
        // Раз в сутки: иначе кулдаун бота всё равно не даст больше двух дуэлей в сутки,
        // а вероятностная модель успеет «выстрелить» десятки раз за день
        if (now.Hour != 12)
            return;

        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        var swines = await context.Swines.Where(s => s.GroupId != null).AsNoTracking().ToListAsync(token);
        if (swines.Count < 2)
            return;

        var cooldownStart = now.AddHours(-DuelBalance.CooldownHours);
        var recent = await context.DuelResults
            .Where(r => r.DateTime > cooldownStart)
            .Select(r => new { r.AttackerId, r.DefenderId })
            .ToListAsync(token);

        var onCooldown = recent.SelectMany(r => new[] { r.AttackerId, r.DefenderId })
            .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();

        var pending = (await context.DuelRequests.Select(dr => dr.DefenderId).ToListAsync(token)).ToHashSet();

        foreach (var attacker in swines.OrderBy(_ => Random.Shared.Next()))
        {
            if (onCooldown.Contains(attacker.SwineId) || pending.Contains(attacker.SwineId))
                continue;

            var fatter = swines
                .Where(s => s.Weight > attacker.Weight && !pending.Contains(s.SwineId) && !onCooldown.Contains(s.SwineId))
                .ToList();

            if (fatter.Count == 0)
                continue;

            var target = fatter[Random.Shared.Next(fatter.Count)];
            var relativeDiff = 1.0 - (double)attacker.Weight / target.Weight;
            if (Random.Shared.NextDouble() > relativeDiff)
                continue;

            var profile = ProdProfile.All.First(p => p.TelegramId == TelegramIdOf(context, attacker.OwnerId));
            var handler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();
            var result = await handler.Handle(BuildUpdate(profile, $"/duel {TelegramIdOf(context, target.OwnerId)}"), token);

            if (result is UpdateHandleResult.MessageOK)
                ++_duelsStarted;
            else
                _failures.Add($"{profile.Owner} /duel: {result}");

            pending.Add(target.SwineId);
            onCooldown.Add(attacker.SwineId);
        }
    }

    private static long TelegramIdOf(UserContext context, int userId)
        => context.Users.First(u => u.UserId == userId).TelegramId;

    private static async Task<Swine> SwineOf(UserContext context, long telegramId)
    {
        var user = await context.Users.FirstAsync(u => u.TelegramId == telegramId);
        return await context.Swines.FirstAsync(s => s.OwnerId == user.UserId);
    }

    private static Telegram.Bot.Types.Update BuildUpdate(ProdProfile profile, string command)
    {
        var entityLength = command.IndexOf(' ') is var space and > 0 ? space : command.Length;

        return new Telegram.Bot.Types.Update
        {
            Message = new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = GroupTelegramId, Type = Telegram.Bot.Types.Enums.ChatType.Supergroup, Title = "сво любители" },
                From = new Telegram.Bot.Types.User { Id = profile.TelegramId, FirstName = profile.Owner, Username = profile.Owner },
                Text = command,
                Entities = [new MessageEntity { Type = Telegram.Bot.Types.Enums.MessageEntityType.BotCommand, Offset = 0, Length = entityLength }],
            },
        };
    }

    private async Task PrintSummary()
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        Console.WriteLine();
        Console.WriteLine($"=== Replay summary ({Settings.DaysCount} дней) ===");
        Console.WriteLine($"{"owner",-14} {"start",7} {"final",7} {"delta",7} {"feeds",6} {"throws",6} {"duels",7} {"decl",5} {"slaugh",6}");

        var startWeights = new Dictionary<long, int>();
        var finalWeights = new Dictionary<long, int>();
        var fightsWon = new Dictionary<long, int>();
        var fightsLost = new Dictionary<long, int>();

        foreach (var profile in ProdProfile.All)
        {
            var swine = await SwineOf(context, profile.TelegramId);
            var feeds = await context.Feeds.CountAsync(f => f.SwineId == swine.SwineId);
            var throws = await context.WeightLosses.CountAsync(w => w.SwineId == swine.SwineId && w.IsThrowUp);
            var slaughters = await context.Slaughters.CountAsync(s => s.UserId == swine.OwnerId);

            var fights = context.DuelResults.Where(r => !r.IsDecline);
            var won = await fights.CountAsync(r => r.AttackerWon && r.AttackerId == swine.SwineId || !r.AttackerWon && r.DefenderId == swine.SwineId);
            var lost = await fights.CountAsync(r => r.AttackerWon && r.DefenderId == swine.SwineId || !r.AttackerWon && r.AttackerId == swine.SwineId);
            var declines = await context.DuelResults.CountAsync(r => r.IsDecline && (r.AttackerId == swine.SwineId || r.DefenderId == swine.SwineId));

            startWeights[profile.TelegramId] = profile.StartWeight;
            finalWeights[profile.TelegramId] = swine.Weight;
            fightsWon[profile.TelegramId] = won;
            fightsLost[profile.TelegramId] = lost;

            Console.WriteLine($"{profile.Owner,-14} {profile.StartWeight,7} {swine.Weight,7} {swine.Weight - profile.StartWeight,7} {feeds,6} {throws,6} {$"{won}/{lost}",7} {declines,5} {slaughters,6}");
        }

        var startTotal = startWeights.Values.Sum();
        var finalTotal = finalWeights.Values.Sum();
        var bigStart = ProdProfile.All.Where(p => p.StartWeight >= ProdProfile.SmallWeightBorder).Sum(p => p.StartWeight);
        var bigFinal = ProdProfile.All.Where(p => p.StartWeight >= ProdProfile.SmallWeightBorder).Sum(p => finalWeights[p.TelegramId]);
        var smallStart = startTotal - bigStart;
        var smallFinal = finalTotal - bigFinal;

        Console.WriteLine();
        Console.WriteLine($"start total {startTotal}, final total {finalTotal} (прирост {finalTotal - startTotal})");
        Console.WriteLine($"толстяки: {bigStart} -> {bigFinal} ({bigFinal - bigStart:+0;-0})");
        Console.WriteLine($"малыши:   {smallStart} -> {smallFinal} ({smallFinal - smallStart:+0;-0})");
        Console.WriteLine($"вызовов {_duelsStarted}, принято {_accepted}, отклонено {_declined}, кормёжек {_feeds}, забоев {_slaughters}");

        Console.WriteLine();
        Console.WriteLine("RESULT " + string.Join(" ", new[]
        {
            $"consent={Settings.DuelConsent:F2}",
            $"duels={_duelsStarted}",
            $"accepted={_accepted}",
            $"declined={_declined}",
            $"bigDelta={bigFinal - bigStart}",
            $"smallDelta={smallFinal - smallStart}",
            $"totalDelta={finalTotal - startTotal}",
            "weights=" + string.Join(",", ProdProfile.All.Select(p => $"{p.Owner}:{finalWeights[p.TelegramId]}")),
        }));

        Console.WriteLine();
        if (_failures.Count == 0)
        {
            Console.WriteLine("ALL UPDATES HANDLED");
        }
        else
        {
            Console.WriteLine($"FAILURES: {_failures.Count}");
            foreach (var failure in _failures.Distinct().Take(20))
                Console.WriteLine("  " + failure);
        }
    }

    private async Task Exec(Action<UserContext> action)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();
        action(context);
        await context.SaveChangesAsync();
    }
}
