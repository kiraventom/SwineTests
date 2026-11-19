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
    private ProdProfile[] Profiles => ProdProfile.WithKiller(Settings.IncludeKiller);

    /// <summary>Из скольких ближайших по весу жертв свин выбирает цель (модель nearest).</summary>
    private const int NearestPoolSize = 3;

    /// <summary>Кто вообще участвует в дуэлях (пустая строка — все).</summary>
    private bool IsDuelist(ProdProfile profile)
        => string.IsNullOrWhiteSpace(Settings.Duelists)
           || Settings.Duelists.Split(',', StringSplitOptions.TrimEntries).Contains(profile.Owner);
    private const long GroupTelegramId = -1001337;

    private readonly List<string> _failures = [];
    private readonly Dictionary<long, DateTime> _nextSession = [];
    private readonly Dictionary<long, DateTime> _nextSlaughter = [];

    private int _duelsStarted;
    private int _accepted;
    private int _declined;
    private int _expired;
    private int _notFound;
    private int _requestsCreated;
    private int _rejectedByLimits;
    private int _declinedRatio;
    private int _declinedChance;
    private int _feeds;
    private int _slaughters;

    private readonly List<string> _series = [];
    private readonly Dictionary<string, int> _duelsPerDay = [];

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

        Console.WriteLine($"=== Replay: {Profiles.Length} свинов, {Settings.DaysCount} дней, " +
                          $"дуэли {(Settings.DuelChance <= 0 ? "выключены" : $"включены, согласие {Settings.DuelConsent:P0}")}, " +
                          $"модель вызовов {Settings.CallModel}");

        foreach (var profile in Profiles)
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
            {
                Console.WriteLine($"Day {hour / 24 + 1}/{Settings.DaysCount}");
                await SnapshotDay(hour / 24, now);
            }

            foreach (var profile in Profiles)
            {
                // Забой по расписанию владельца (--no-slaughter отключает, чтобы не мешать графикам дуэлей)
                if (!Settings.NoSlaughter
                    && (Settings.StopSlaughterDay < 0 || hour / 24 < Settings.StopSlaughterDay)
                    && profile.SlaughterEveryDays > 0 && now >= _nextSlaughter[profile.TelegramId])
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

            // Дуэли «включаются» с назначенного дня: на графике это видно как излом
            if (Settings.DuelChance > 0 && hour / 24 >= Settings.DuelsFromDay)
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
        foreach (var profile in Profiles)
            await Act(profile, "/feed", CancellationToken.None);

        await Exec(c =>
        {
            var swines = c.Swines.AsTracking().ToList();
            foreach (var profile in Profiles)
            {
                var user = c.Users.First(u => u.TelegramId == profile.TelegramId);
                var swine = swines.First(s => s.OwnerId == user.UserId);
                swine.Weight = profile.StartWeight;
                swine.Name = profile.Name;
            }
        });

        _feeds = 0;
        _slaughters = 0;

        await Exec(c =>
        {
            foreach (var entity in c.Feeds.AsTracking().ToList()) c.Feeds.Remove(entity);
            foreach (var entity in c.WeightLosses.AsTracking().ToList()) c.WeightLosses.Remove(entity);
            foreach (var entity in c.Achievements.AsTracking().ToList()) c.Achievements.Remove(entity);
            foreach (var entity in c.Slaughters.AsTracking().ToList()) c.Slaughters.Remove(entity);
            foreach (var entity in c.DuelResults.AsTracking().ToList()) c.DuelResults.Remove(entity);
            foreach (var entity in c.DuelRequests.AsTracking().ToList()) c.DuelRequests.Remove(entity);
        });

        // Возраст свина: кладём одну «историческую» кормёжку на дату старт − возраст.
        // От неё считается, сколько забоёв свин пережил при жизни (боевой бонус witness).
        await Exec(c =>
        {
            var start = ((MockDateTimeNowProvider)DtnProvider).UtcNow;
            foreach (var profile in Profiles.Where(p => p.AgeDays > 0))
            {
                var swine = c.Swines.First(s => s.OwnerId == c.Users.First(u => u.TelegramId == profile.TelegramId).UserId);
                c.Feeds.Add(new Feed
                {
                    SwineId = swine.SwineId,
                    DateTime = start.AddDays(-profile.AgeDays),
                    Amount = 0,
                    Luck = null,
                });
            }
        });

        // Исторические забои из прода: от них зависят и множитель кормления, и дебафф в дуэлях
        await Exec(c =>
        {
            foreach (var profile in Profiles.Where(p => p.PreSlaughters > 0))
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
                        DateTime = ((MockDateTimeNowProvider)DtnProvider).UtcNow.AddDays(-20 - i * 25),
                    });
                }
            }
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

        var attackerSwine = await context.Swines.AsNoTracking().FirstAsync(s => s.SwineId == request.AttackerId, token);
        var statsProvider = scope.ServiceProvider.GetRequiredService<DuelStatsProvider>();
        var nowUtc = ((MockDateTimeNowProvider)DtnProvider).UtcNow;

        var attackerStats = await statsProvider.GetAsync(attackerSwine, nowUtc);
        var defenderStats = await statsProvider.GetAsync(swine, nowUtc);

        var winChance = DuelResolution.GetDefenderWinChance(
            attackerSwine.Weight, swine.Weight, attackerStats, defenderStats) / 100.0;

        // Решение как у живого игрока (v3):
        // 1) риск ограничен ставкой (5% от лёгкого), поэтому «мелюзга» больше не страшна —
        //    ratio-гейт включается только явно, флагом --min-weight-ratio;
        // 2) в минусе славы — соглашаемся, иначе будем трусом и нас будут бить без очереди;
        // 3) иначе — согласие только при вменяемом шансе (порог храбрости).
        var weightRatio = (double)attackerSwine.Weight / Math.Max(1, swine.Weight);

        bool accept;
        string reason;

        if (Settings.DuelMinWeightRatio > 0 && weightRatio < Settings.DuelMinWeightRatio)
        {
            accept = false;
            ++_declinedRatio;
            reason = $"атакующий весит {weightRatio:P0} от нас — риск не оправдан";
        }
        else if (swine.Glory < 0)
        {
            accept = true;
            reason = $"слава в минусе ({swine.Glory}), надо отмываться";
        }
        else if (winChance >= Settings.DuelConsent)
        {
            accept = true;
            reason = $"шанс {winChance:P0} >= порога {Settings.DuelConsent:P0}";
        }
        else
        {
            accept = false;
            ++_declinedChance;
            reason = $"шанс {winChance:P0} < порога {Settings.DuelConsent:P0}";
        }

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

        if (result == UpdateHandleResult.CallbackQueryDuelRequestExpired)
            ++_expired;
        else if (result == UpdateHandleResult.CallbackQueryDuelRequestNotFound)
            ++_notFound;

        if (accept)
        {
            ++_accepted;
            if (_accepted <= 10)
                Console.WriteLine($"  {profile.Owner}: принял вызов ({reason})");
        }
        else
        {
            ++_declined;
            if (_declined <= 10)
                Console.WriteLine($"  {profile.Owner}: отказался ({reason})");
        }

        return true;
    }

    /// <summary>
    /// Малыш агрессирует на толстяка — один вызов в сутки на свина плюс кулдаун самого бота.
    /// Выбор цели задаётся <see cref="Settings.CallModel"/>:
    /// <c>any</c> — случайный более тяжёлый, шанс вызова = относительная разница весов
    /// (старая модель: почти всегда «малыш против толстяка»);
    /// <c>nearest</c> — из ближайших по весу, кого реально можно побить (по умолчанию).
    /// </summary>
    private async Task RunDuels(DateTime now, CancellationToken token)
    {
        // Раз в сутки: иначе кулдаун бота всё равно не даст больше двух дуэлей в сутки,
        // а вероятностная модель успеет «выстрелить» десятки раз за день
        if (now.Hour != 12)
            return;

        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        // Лимит «один вызов на беседу в сутки» — такой же, как в боте, иначе симуляция
        // нафантазирует в разы больше дуэлей, чем возможно в реальности
        var callSince = now.AddHours(-DuelBalance.CallCooldownHours);
        var anyCallToday = await context.DuelResults
            .AnyAsync(r => r.GroupId == GroupTelegramId && r.DateTime > callSince, token)
            || await context.DuelRequests.AnyAsync(token);

        if (anyCallToday)
            return;

        // В дуэлях участвуют только выбранные свины (по умолчанию — все)
        var duelistNames = string.IsNullOrWhiteSpace(Settings.Duelists)
            ? null
            : Settings.Duelists.Split(',', StringSplitOptions.TrimEntries).ToHashSet();

        var duelistTelegramIds = duelistNames is null
            ? null
            : Profiles.Where(p => duelistNames.Contains(p.Owner)).Select(p => p.TelegramId).ToHashSet();

        var allSwines = await context.Swines.Where(s => s.GroupId != null).AsNoTracking().ToListAsync(token);
        var swines = duelistTelegramIds is null
            ? allSwines
            : allSwines.Where(s => duelistTelegramIds.Contains(TelegramIdOf(context, s.OwnerId))).ToList();

        if (swines.Count < 2)
            return;

        var longAgo = now.AddHours(-DuelBalance.DefendCooldownHours);
        var recent = await context.DuelResults
            .Where(r => r.DateTime > longAgo)
            .Select(r => new { r.AttackerId, r.DefenderId, r.DateTime })
            .ToListAsync(token);

        var onCooldown = recent.SelectMany(r => new[] { r.AttackerId, r.DefenderId })
            .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();

        var pending = (await context.DuelRequests.Select(dr => dr.DefenderId).ToListAsync(token)).ToHashSet();

        // Точные повторы пары запрещены 72 часа
        var pairSince = now.AddHours(-DuelBalance.PairCooldownHours);
        var recentPairs = recent.Where(r => r.DateTime > pairSince)
            .Select(r => (r.AttackerId, r.DefenderId))
            .ToHashSet();

        foreach (var attacker in swines.OrderBy(_ => Random.Shared.Next()))
        {
            if (onCooldown.Contains(attacker.SwineId) || pending.Contains(attacker.SwineId))
                continue;

            var fatter = swines
                .Where(s => s.Weight > attacker.Weight && !pending.Contains(s.SwineId) && !onCooldown.Contains(s.SwineId))
                .Where(s => !recentPairs.Contains((attacker.SwineId, s.SwineId)))
                .ToList();

            if (fatter.Count == 0)
                continue;

            Swine target;
            double relativeDiff;
            double callChance;

            if (Settings.CallModel == "any")
            {
                target = fatter[Random.Shared.Next(fatter.Count)];
                relativeDiff = 1.0 - (double)attacker.Weight / target.Weight;
                callChance = relativeDiff;
            }
            else
            {
                // Берём ближайших по весу: с равным по силе бойцом шансы не запредельные,
                // а с толстяком — почти нулевые, поэтому в реальности ищут ровню
                var nearest = fatter.OrderBy(s => s.Weight).Take(NearestPoolSize).ToList();
                target = nearest[Random.Shared.Next(nearest.Count)];
                relativeDiff = 1.0 - (double)attacker.Weight / target.Weight;
                callChance = 0.5 + 0.5 * (1 - relativeDiff);
            }

            if (Random.Shared.NextDouble() > callChance)
                continue;

            var profile = Profiles.First(p => p.TelegramId == TelegramIdOf(context, attacker.OwnerId));
            var handler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();

            // Ответ «слишком рано» — тоже MessageOK, но вызова в базе после него нет:
            // считаем именно созданные запросы, иначе воронка врёт вдвое
            var requestsBefore = await context.DuelRequests.CountAsync(token);
            var result = await handler.Handle(BuildUpdate(profile, $"/duel {TelegramIdOf(context, target.OwnerId)}"), token);

            if (result is UpdateHandleResult.MessageOK)
            {
                ++_duelsStarted;
                if (await context.DuelRequests.CountAsync(token) > requestsBefore)
                    ++_requestsCreated;
                else
                    ++_rejectedByLimits;
            }
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

    /// <summary>Дневной срез: вес каждого свина плюс сумма — из него строятся графики.</summary>
    private async Task SnapshotDay(int day, DateTime now)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        var parts = new List<string>();
        var total = 0;

        foreach (var profile in Profiles)
        {
            var swine = await SwineOf(context, profile.TelegramId);
            parts.Add($"{profile.Owner}:{swine.Weight}");
            total += swine.Weight;
        }

        _series.Add($"{{\"day\":{day},\"total\":{total},\"weights\":{{{string.Join(",", parts.Select(p => $"\"{p.Split(':')[0]}\":{p.Split(':')[1]}"))}}}}}");
    }

    private async Task PrintSummary()
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();

        Console.WriteLine();
        Console.WriteLine($"=== Replay summary ({Settings.DaysCount} дней) ===");
        Console.WriteLine($"{"owner",-14} {"start",7} {"final",7} {"delta",7} {"feeds",6} {"throws",6} {"duels",7} {"decl",5} {"glory",6} {"mod",5} {"slaugh",6}");

        var startWeights = new Dictionary<long, int>();
        var finalWeights = new Dictionary<long, int>();
        var fightsWon = new Dictionary<long, int>();
        var fightsLost = new Dictionary<long, int>();

        foreach (var profile in Profiles)
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

            Console.WriteLine($"{profile.Owner,-14} {profile.StartWeight,7} {swine.Weight,7} {swine.Weight - profile.StartWeight,7} {feeds,6} {throws,6} {$"{won}/{lost}",7} {declines,5} {swine.Glory,6} {await context.GetGrowthModifierAsync(swine),5:F2} {slaughters,6}");
        }

        var startTotal = startWeights.Values.Sum();
        var finalTotal = finalWeights.Values.Sum();
        var bigStart = Profiles.Where(p => p.StartWeight >= ProdProfile.SmallWeightBorder).Sum(p => p.StartWeight);
        var bigFinal = Profiles.Where(p => p.StartWeight >= ProdProfile.SmallWeightBorder).Sum(p => finalWeights[p.TelegramId]);
        var smallStart = startTotal - bigStart;
        var smallFinal = finalTotal - bigFinal;

        Console.WriteLine();
        Console.WriteLine($"start total {startTotal}, final total {finalTotal} (прирост {finalTotal - startTotal})");
        Console.WriteLine($"толстяки: {bigStart} -> {bigFinal} ({bigFinal - bigStart:+0;-0})");
        Console.WriteLine($"малыши:   {smallStart} -> {smallFinal} ({smallFinal - smallStart:+0;-0})");
        Console.WriteLine($"вызовов {_duelsStarted}, принято {_accepted}, отклонено {_declined}, "
            + $"протухло {_expired}, не найдено {_notFound}, кормёжек {_feeds}, забоев {_slaughters}");
        Console.WriteLine($"попыток вызова {_duelsStarted}, из них запросов создано {_requestsCreated}, "
            + $"отбито лимитами {_rejectedByLimits}");
        Console.WriteLine($"отказов: мелюзга {_declinedRatio}, шанс ниже порога {_declinedChance}");

        // Апсеты: победил тот, кто был не легче вдвое
        var allFights = context.DuelResults.Where(r => !r.IsDecline);
        var fightsTotal = await allFights.CountAsync();
        var upsets = await allFights.CountAsync(r => r.WinnerWeightBefore * DuelBalance.UpsetWeightRatio <= r.LoserWeightBefore);
        var gloryTotal = await context.Swines.SumAsync(s => (int?)s.Glory) ?? 0;
        var gloryPositive = await context.Swines.CountAsync(s => s.Glory > 0);
        Console.WriteLine($"боёв {fightsTotal}, апсетов {upsets} "
            + $"({(fightsTotal == 0 ? 0 : 100 * upsets / fightsTotal)}%), слава в плюсе у {gloryPositive} свинов, сумма славы {gloryTotal}");

        Console.WriteLine();
        Console.WriteLine("RESULT " + string.Join(" ", new[]
        {
            $"consent={Settings.DuelConsent:F2}",
            $"duels={_duelsStarted}",
            $"accepted={_accepted}",
            $"declined={_declined}",
            $"upsets={upsets}",
            $"glory={gloryTotal}",
            $"bigDelta={bigFinal - bigStart}",
            $"smallDelta={smallFinal - smallStart}",
            $"totalDelta={finalTotal - startTotal}",
            "weights=" + string.Join(",", Profiles.Select(p => $"{p.Owner}:{finalWeights[p.TelegramId]}")),
        }));

        // Ряд пишем рядом с логом: из него аналитический скрипт рисует графики
        var seriesName = Environment.GetEnvironmentVariable("REPLAY_SERIES") ?? "series";
        Directory.CreateDirectory("results");
        File.WriteAllLines($"results/{seriesName}.jsonl", _series);

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
