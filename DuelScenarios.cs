using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwineBot;
using SwineBot.Actions.Commands.Duel;
using SwineBot.Model;
using SwineBot.Updates;
using Telegram.Bot.Types;

namespace SwineTests;

/// <summary>
/// Проверки дуэлей «по шагам»: один атакующий, один защитник, всё предсказуемо.
/// Идут на чистой базе перед длинной симуляцией.
/// </summary>
public class DuelScenarios(IServiceScopeFactory spf, MockBotMessageSender sender, MockCallbackQueryAnswerer answerer,
    MockMessageEditor editor, IDateTimeNowProvider now, IReadOnlyList<TestUser> users)
{
    private const long GROUP_TG_ID = TestUser.GroupTelegramId;

    private readonly List<string> _failures = [];

    private TestUser Attacker => users[0];
    private TestUser Defender => users[1];

    public async Task<IReadOnlyList<string>> Run(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("=== Duel scenarios ===");

        CheckDuelMath();

        await Send(Attacker, "/feed", token);
        await Send(Defender, "/feed", token);

        await StepRequest(token);
        await StepDuelBlockedButFeedingAllowed(token);
        await StepAttackerCannotAnswer(token);
        await StepAccept(token);
        await StepCooldown(token);
        await StepDeclineTakesSlaughterBonus(token);
        await StepIgnoredRequestIsDecline(token);
        await StepSlaughterUnderRequestIsDecline(token);
        await StepInfoDoesNotCountDeclines(token);

        return _failures;
    }

    private void CheckDuelMath()
    {
        Console.WriteLine("-- формулы");

        Check("шанс защитника 100 vs 50 = 33%", DuelResolution.GetDefenderWinChance(100, 50) == 33,
            DuelResolution.GetDefenderWinChance(100, 50).ToString());
        Check("шанс защитника 10 vs 90 = 90%", DuelResolution.GetDefenderWinChance(10, 90) == 90);

        Check("каждый забой режет шанс на 2 п.п.",
            DuelResolution.GetDefenderWinChance(100, 50, defenderKills: 5) == 23,
            DuelResolution.GetDefenderWinChance(100, 50, defenderKills: 5).ToString());
        Check("забои атакующего поднимают шанс защитника",
            DuelResolution.GetDefenderWinChance(100, 50, attackerKills: 5) == 43,
            DuelResolution.GetDefenderWinChance(100, 50, attackerKills: 5).ToString());
        Check("забои не выбивают шанс за нижнюю границу",
            DuelResolution.GetDefenderWinChance(10, 90, defenderKills: 100) == DuelBalance.MinWinChance,
            DuelResolution.GetDefenderWinChance(10, 90, defenderKills: 100).ToString());
        Check("серийный убийца против малыша теряет преимущество",
            DuelResolution.GetDefenderWinChance(150, 7901, attackerKills: 0, defenderKills: 15)
            < DuelResolution.GetDefenderWinChance(150, 7901),
            $"{DuelResolution.GetDefenderWinChance(150, 7901, 0, 15)} vs {DuelResolution.GetDefenderWinChance(150, 7901)}");

        Check("ставка 20% от 100 кг", DuelResolution.GetStakeKg(100) == 20, DuelResolution.GetStakeKg(100).ToString());
        Check("ставка не меньше 1 кг", DuelResolution.GetStakeKg(4) == 1);
        Check("проигравший не падает ниже 1 кг", DuelResolution.GetStakeKg(1) == 0);

        Check("отказ отдаёт 10% slaughter-бонуса",
            DuelResolution.GetDeclineSlaughterShareKg(11942) == 1194,
            DuelResolution.GetDeclineSlaughterShareKg(11942).ToString());
        Check("без забоев отдавать нечего", DuelResolution.GetDeclineSlaughterShareKg(0) == 0);
        Check("весового штрафа за отказ нет", DuelResolution.GetDeclineFeeKg(150, 7901) == 0);

        var defenderWins = DuelResolution.Resolve(100, 50, 0.10);
        Check("бросок 0.10 — побеждает защитник", !defenderWins.AttackerWon);
        Check("защитник 50 -> 70, атакующий 100 -> 80",
            defenderWins.WinnerWeightAfter == 70 && defenderWins.LoserWeightAfter == 80,
            $"{defenderWins.WinnerWeightBefore}->{defenderWins.WinnerWeightAfter}, {defenderWins.LoserWeightBefore}->{defenderWins.LoserWeightAfter}");

        var attackerWins = DuelResolution.Resolve(100, 50, 0.90);
        Check("бросок 0.90 — побеждает атакующий", attackerWins.AttackerWon);
        Check("сумма веса сохраняется", attackerWins.WinnerWeightAfter + attackerWins.LoserWeightAfter == 150);
    }

    private async Task StepRequest(CancellationToken token)
    {
        Console.WriteLine("-- вызов");

        await SetWeights(100, 50);
        await GiveSlaughters(Defender, 3);

        var message = await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        Check("вызов ушёл с двумя кнопками", message?.Keyboard?.InlineKeyboard?.Sum(r => r.Count()) == 2);
        Check("виден шанс на победу", message?.Text.Contains("Шанс на победу") == true);
        Check("видна ставка", message?.Text.Contains("Ставка") == true);
        Check("виден штраф бонусом забоев", message?.Text.Contains("бонуса забоев") == true,
            message?.Text.Split('\n').FirstOrDefault(l => l.Contains("Отказ")));
        Check("видно число забитых", message?.Text.Contains("Забитых предшественников") == true);

        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstOrDefaultAsync());
        Check("запрос сохранён с MessageId", request?.MessageId is not null, $"MessageId={request?.MessageId}");
    }

    private async Task StepDuelBlockedButFeedingAllowed(CancellationToken token)
    {
        Console.WriteLine("-- вызов блокирует только дуэли");

        var feed = await Send(Defender, "/feed", token);
        Check("кормление не блокируется", feed?.GetType().Name == "FeedMessage", feed?.GetType().Name);

        var duel = await Send(Defender, $"/duel {Attacker.TelegramId}", token);
        Check("новый вызов блокирован напоминанием", duel?.GetType().Name == "DuelReminderMessage", duel?.GetType().Name);
    }

    private async Task StepAttackerCannotAnswer(CancellationToken token)
    {
        Console.WriteLine("-- чужое нажатие");

        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstAsync());
        var (_, toast) = await Press(Attacker, $"accept_{request.RequestId}", token);

        Check("атакующий получил отказ по правам", toast?.Contains("владелец") == true, toast);
        Check("запрос не тронут", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 1);
    }

    private async Task StepAccept(CancellationToken token)
    {
        Console.WriteLine("-- принятие вызова");

        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstAsync());
        var (attackerBefore, defenderBefore) = (await Weight(Attacker), await Weight(Defender));

        var (_, toast) = await Press(Defender, $"accept_{request.RequestId}", token);

        var (attackerAfter, defenderAfter) = (await Weight(Attacker), await Weight(Defender));
        Check("тост про исход", toast is "Ваш свин победил!" or "Ваш свин проиграл", toast);
        Check("запрос удалён", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 0);
        Check("суммарный вес сохранён", attackerAfter + defenderAfter == attackerBefore + defenderBefore,
            $"{attackerBefore}+{defenderBefore} -> {attackerAfter}+{defenderAfter}");

        var result = await Db(c => c.DuelResults.AsNoTracking().OrderByDescending(r => r.DuelResultId).FirstAsync());
        Check("результат записан как бой", !result.IsDecline);

        var edited = editor.Edited.LastOrDefault();
        Check("сообщение заменено итогом", edited?.Text?.Contains("побеждает") == true, edited?.Text);
        Check("кнопки сняты", edited?.Buttons == 0, $"buttons={edited?.Buttons}");
    }

    private async Task StepCooldown(CancellationToken token)
    {
        Console.WriteLine("-- кулдаун");

        var attackerMessage = await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        Check("атакующий на кулдауне", attackerMessage?.GetType().Name == "TooSoonToDuelMessage", attackerMessage?.GetType().Name);
    }

    private async Task StepDeclineTakesSlaughterBonus(CancellationToken token)
    {
        Console.WriteLine("-- отказ отдаёт часть бонуса забоев");

        Advance(DuelBalance.CooldownHours + 1);
        await SetWeights(100, 50);
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);

        var request = await Db(c => c.DuelRequests.AsNoTracking().FirstAsync());
        var defenderSlaughterWeight = await Db(c => c.GetSlaughterWeightAsync(AttackerSwine(c, Defender).OwnerId, 1));
        var attackerWeightBefore = await Weight(Attacker);

        var (_, toast) = await Press(Defender, $"decline_{request.RequestId}", token);

        var bonuses = await Db(c => c.SlaughterBonuses.AsNoTracking().OrderByDescending(b => b.SlaughterBonusId).Take(2).ToListAsync());
        var expected = DuelResolution.GetDeclineSlaughterShareKg(defenderSlaughterWeight);

        Check("тост про отказ", toast?.Contains("Вызов отклонён") == true, toast);
        Check("вес за отказ не отбирается", await Weight(Attacker) == attackerWeightBefore, (await Weight(Attacker)).ToString());
        Check($"защитник отдал {expected} бонуса", bonuses.Any(b => b.Weight == -expected), string.Join(",", bonuses.Select(b => b.Weight)));
        Check($"вызывающий получил {expected} бонуса", bonuses.Any(b => b.Weight == expected), string.Join(",", bonuses.Select(b => b.Weight)));

        var edited = editor.Edited.LastOrDefault();
        Check("сообщение про отказ и трусость", edited?.Text?.Contains("трусов") == true, edited?.Text);
    }

    private async Task StepIgnoredRequestIsDecline(CancellationToken token)
    {
        Console.WriteLine("-- игнор 24 часа = отказ");

        Advance(DuelBalance.CooldownHours + 1);
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);

        var before = await Db(c => c.SlaughterBonuses.AsNoTracking().CountAsync());
        Advance(DuelRequestExtensions.TTL.TotalHours + 1);

        await Send(Attacker, "/feed", token);
        var after = await Db(c => c.SlaughterBonuses.AsNoTracking().CountAsync());

        Check("истёкший вызов применился как отказ", after > before, $"{before} -> {after}");
        Check("запрос закрыт", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 0);
    }

    private async Task StepSlaughterUnderRequestIsDecline(CancellationToken token)
    {
        Console.WriteLine("-- забой под вызовом = отказ");

        Advance(DuelBalance.CooldownHours + 1);
        await GiveSlaughters(Defender, 5);
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);

        var before = await Db(c => c.SlaughterBonuses.AsNoTracking().CountAsync());
        await Send(Defender, "/slaughter yes", token);
        var after = await Db(c => c.SlaughterBonuses.AsNoTracking().CountAsync());

        Check("забой закрыл вызов отказом", after > before, $"{before} -> {after}");
        Check("вызовов не осталось", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 0);
    }

    private async Task StepInfoDoesNotCountDeclines(CancellationToken token)
    {
        Console.WriteLine("-- отказы не попадают в статистику /info");

        await Exec(c => c.DuelResults.RemoveRange(c.DuelResults.AsTracking().ToList()));
        await Exec(c =>
        {
            c.DuelResults.Add(new DuelResult
            {
                AttackerId = SwineId(c, Attacker), DefenderId = SwineId(c, Defender), AttackerWon = true, IsDecline = false,
                DateTime = ((MockDateTimeNowProvider)now).UtcNow,
                WinnerWeightBefore = 100, WinnerWeightAfter = 120, LoserWeightBefore = 50, LoserWeightAfter = 30,
            });
            c.DuelResults.Add(new DuelResult
            {
                AttackerId = SwineId(c, Defender), DefenderId = SwineId(c, Attacker), AttackerWon = true, IsDecline = true,
                DateTime = ((MockDateTimeNowProvider)now).UtcNow,
                WinnerWeightBefore = 50, WinnerWeightAfter = 50, LoserWeightBefore = 120, LoserWeightAfter = 120,
            });
        });

        var winnerInfo = await Send(Attacker, "/info", token);
        Check("у победителя 1 победа и 0 поражений",
            winnerInfo?.Text.Contains("1 победа") == true && winnerInfo.Text.Contains("0 поражений") == true,
            winnerInfo?.Text.Split('\n').FirstOrDefault(l => l.Contains("дуэл")));

        var loserInfo = await Send(Defender, "/info", token);
        Check("у проигравшего 0 побед и 1 поражение",
            loserInfo?.Text.Contains("0 побед") == true && loserInfo.Text.Contains("1 поражение") == true,
            loserInfo?.Text.Split('\n').FirstOrDefault(l => l.Contains("дуэл")));
    }

    private int SwineId(UserContext context, TestUser user) => SwineOf(context, user).SwineId;

    private static Swine SwineOf(UserContext context, TestUser user)
    {
        var dbUser = context.Users.First(u => u.TelegramId == user.TelegramId);
        return context.Swines.First(s => s.OwnerId == dbUser.UserId);
    }

    private static Swine AttackerSwine(UserContext context, TestUser user) => SwineOf(context, user);

    private async Task<SwineBot.BotMessages.IBotMessage> Send(TestUser user, string command, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();
        var before = sender.MessagesHistory.Count;

        var result = await handler.Handle(user.GenerateCommand(command), token);
        if (!IsSuccess(result))
            _failures.Add($"{user.Strategy} {command}: {result}");

        return sender.MessagesHistory.Count > before ? sender.MessagesHistory[^1] : null;
    }

    private async Task<(UpdateHandleResult Result, string Toast)> Press(TestUser user, string data, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CallbackQueryHandler>();

        var query = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString(),
            From = new Telegram.Bot.Types.User { Id = user.TelegramId, FirstName = user.Strategy.ToString() },
            Data = data,
            Message = new Message { Id = 1, Chat = new Chat { Id = GROUP_TG_ID } },
        };

        var answersBefore = answerer.Answers.Count;
        var result = await handler.Handle(query, token);
        var toast = answerer.Answers.Count > answersBefore ? answerer.Answers[^1] : null;
        return (result, toast);
    }

    private static bool IsSuccess(UpdateHandleResult result)
        => result is UpdateHandleResult.MessageOK
            or UpdateHandleResult.InlineQueryOK
            or UpdateHandleResult.CallbackQueryOK
            or UpdateHandleResult.MessageSuccesfulMigration;

    /// <summary>Создаёт защитнику историю забоёв (и slaughter-бонус, который можно отобрать).</summary>
    private async Task GiveSlaughters(TestUser user, int count)
    {
        await Exec(c =>
        {
            var swine = SwineOf(c, user);
            for (int i = 0; i < count; ++i)
            {
                c.Slaughters.Add(new Slaughter
                {
                    UserId = swine.OwnerId,
                    GroupId = swine.GroupId,
                    SwineName = $"предок {i}",
                    SwineWeight = 500,
                    DateTime = ((MockDateTimeNowProvider)now).UtcNow.AddDays(-30 - i),
                });
            }
        });
    }

    private async Task SetWeights(int attackerWeight, int defenderWeight)
    {
        await Exec(c =>
        {
            var swines = c.Swines.AsTracking().ToList();
            swines.First(s => s.OwnerId == c.Users.First(u => u.TelegramId == Attacker.TelegramId).UserId).Weight = attackerWeight;
            swines.First(s => s.OwnerId == c.Users.First(u => u.TelegramId == Defender.TelegramId).UserId).Weight = defenderWeight;
        });
    }

    private async Task<int> Weight(TestUser user)
        => await Db(c => Task.FromResult(SwineOf(c, user).Weight));

    private async Task<T> Db<T>(Func<UserContext, Task<T>> action)
    {
        using var scope = spf.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserContext>());
    }

    private async Task Exec(Action<UserContext> action)
    {
        using var scope = spf.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<UserContext>();
        action(context);
        await context.SaveChangesAsync();
    }

    private void Advance(double hours)
        => ((MockDateTimeNowProvider)now).UtcNow = ((MockDateTimeNowProvider)now).UtcNow.AddHours(hours);

    private void Check(string name, bool ok, string details = null)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(details is null ? "" : $"  <- {details}")}");
        if (!ok)
            _failures.Add(name);
    }
}
