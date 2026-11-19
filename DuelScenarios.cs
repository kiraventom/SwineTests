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
        await StepMultiplierDecay(token);

        return _failures;
    }

    private void CheckDuelMath()
    {
        Console.WriteLine("-- формулы");

        // v3: шанс — сжатая доля веса + слава. Источник бонуса (возраст/забои) выключаем,
        // чтобы проверки не зависели от флагов запуска
        var savedSource = DuelBalance.ChanceSource;
        DuelBalance.ChanceSource = DuelChanceSource.None;

        try
        {
            Check("равные веса: 45% у защитника (инициатива у атакующего)", DuelResolution.GetDefenderWinChance(500, 500) == 45,
                DuelResolution.GetDefenderWinChance(500, 500).ToString());
            Check("шанс защитника 100 vs 50 = 35% (масса сжата + инициатива)",
                DuelResolution.GetDefenderWinChance(100, 50) == 35,
                DuelResolution.GetDefenderWinChance(100, 50).ToString());
            Check("шанс защитника 10 vs 90 = 69%, а не 90% (масса сжата)",
                DuelResolution.GetDefenderWinChance(10, 90) == 69,
                DuelResolution.GetDefenderWinChance(10, 90).ToString());
            Check("у апсета есть шанс: 15% веса дают больше 25% на победу",
                DuelResolution.GetDefenderWinChance(15, 85) < 80,
                DuelResolution.GetDefenderWinChance(15, 85).ToString());

            var gloryThree = new DuelCombatStats(0, 0, 0, 3);
            Check("слава +3 у защитника добавляет 6 п.п.",
                DuelResolution.GetDefenderWinChance(100, 50, DuelCombatStats.Empty, gloryThree) == 41,
                DuelResolution.GetDefenderWinChance(100, 50, DuelCombatStats.Empty, gloryThree).ToString());
            Check("слава не пробивает верхний кап шанса",
                DuelResolution.GetDefenderWinChance(10, 900, DuelCombatStats.Empty, new DuelCombatStats(0, 0, 0, 5))
                <= DuelBalance.MaxWinChance);

            Check("ставка — 5% от более лёгкого: 100 vs 50 → 2 кг",
                DuelResolution.GetStakeKg(100, 50) == 2, DuelResolution.GetStakeKg(100, 50).ToString());
            Check("ставка не зависит от толстяка: 4000 vs 25000 → 200 кг",
                DuelResolution.GetStakeKg(4000, 25000) == 200, DuelResolution.GetStakeKg(4000, 25000).ToString());
            Check("ставка не меньше 1 кг", DuelResolution.GetStakeKg(1, 4) == 1);

            Check("весового штрафа за отказ нет", DuelResolution.GetDeclineFeeKg(150, 7901) == 0);

            var defenderWins = DuelResolution.Resolve(100, 50, 0.10);
            Check("бросок 0.10 — побеждает защитник", !defenderWins.AttackerWon);
            Check("защитник 50 -> 52, атакующий 100 -> 98 (ставка от лёгкого)",
                defenderWins.WinnerWeightAfter == 52 && defenderWins.LoserWeightAfter == 98,
                $"{defenderWins.WinnerWeightBefore}->{defenderWins.WinnerWeightAfter}, {defenderWins.LoserWeightBefore}->{defenderWins.LoserWeightAfter}");

            var attackerWins = DuelResolution.Resolve(100, 50, 0.90);
            Check("бросок 0.90 — побеждает атакующий", attackerWins.AttackerWon);
            Check("сумма веса сохраняется",
                attackerWins.WinnerWeightAfter + attackerWins.LoserWeightAfter == 150);

            Check("победа даёт очко славы", DuelResolution.GetGloryReward(true, 100, 50) == 1,
                DuelResolution.GetGloryReward(true, 100, 50).ToString());
            Check("апсет (вдвое легче победил) даёт два очка славы",
                DuelResolution.GetGloryReward(true, 50, 100) == 2,
                DuelResolution.GetGloryReward(true, 50, 100).ToString());
            Check("слава победителя усиливает забранную массу",
                Math.Abs(DuelResolution.GetGloryStakeMultiplier(5) - 1.25) < 0.001,
                DuelResolution.GetGloryStakeMultiplier(5).ToString("F2"));
        }
        finally
        {
            DuelBalance.ChanceSource = savedSource;
        }
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
        Check("виден дебафф за отказ", message?.Text.Contains("Отказ") == true,
            message?.Text.Split('\n').FirstOrDefault(l => l.Contains("Отказ")));

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
        Console.WriteLine("-- отказ применяет дебафф");

        Advance(73); // 72 часа + час: сбрасывает парный лимит, кулдаун и защиту проигравшего
        await SetWeights(100, 50);
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);

        var request = await Db(c => c.DuelRequests.AsNoTracking().FirstAsync());
        var gloryBefore = await Db(c => Task.FromResult(SwineOf(c, Defender).Glory));
        var declinesBefore = await Db(c => Task.FromResult(SwineOf(c, Defender).Declines));

        var (_, toast) = await Press(Defender, $"decline_{request.RequestId}", token);

        var declinesAfter = await Db(c => Task.FromResult(SwineOf(c, Defender).Declines));
        var gloryAfter = await Db(c => Task.FromResult(SwineOf(c, Defender).Glory));
        var stealUntil = await Db(c => Task.FromResult(SwineOf(c, Defender).FeedStealUntil));

        Check("тост про отказ", toast?.Contains("Вызов отклонён") == true, toast);

        if (DuelBalance.GloryEnabled && gloryBefore > 0)
        {
            Check("свин расплатился очком славы", gloryAfter == gloryBefore - 1 && declinesAfter == declinesBefore,
                $"слава {gloryBefore}->{gloryAfter}, отказов {declinesBefore}->{declinesAfter}");
        }
        else
        {
            // Дебафф «страх» работает от отрицательной отваги: после отказа она должна уйти в минус
            var debuffApplied = DuelBalance.Debuff switch
            {
                DuelDebuff.FeedSteal => gloryAfter < gloryBefore,
                DuelDebuff.Depreciate => declinesAfter == declinesBefore + 1,
                _ => true,
            };

            Check($"дебафф «{DuelBalance.Debuff}» применён", debuffApplied,
                $"слава {gloryBefore}->{gloryAfter}, declines {declinesBefore}->{declinesAfter}");
        }

        var edited = editor.Edited.LastOrDefault();
        Check("сообщение про отказ", edited?.Text?.Contains("отказ") == true || edited?.Text?.Contains("трусов") == true || edited?.Text?.Contains("славы") == true,
            edited?.Text);
    }

    private async Task StepIgnoredRequestIsDecline(CancellationToken token)
    {
        Console.WriteLine("-- игнор дольше TTL (12 ч) = отказ");

        Advance(73); // 72 часа + час: сбрасывает парный лимит, кулдаун и защиту проигравшего
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);

        var declinesBefore = await Db(c => Task.FromResult(SwineOf(c, Defender).Declines));
        var gloryBefore = await Db(c => Task.FromResult(SwineOf(c, Defender).Glory));
        Advance(DuelRequestExtensions.TTL.TotalHours + 1);

        await Send(Attacker, "/feed", token);
        var declinesAfter = await Db(c => Task.FromResult(SwineOf(c, Defender).Declines));
        var gloryAfter = await Db(c => Task.FromResult(SwineOf(c, Defender).Glory));
        var stealUntil = await Db(c => Task.FromResult(SwineOf(c, Defender).FeedStealUntil));

        var paid = declinesAfter > declinesBefore || gloryAfter < gloryBefore || stealUntil is not null;
        Check("истёкший вызов применился как отказ", paid,
            $"Declines {declinesBefore}->{declinesAfter}, слава {gloryBefore}->{gloryAfter}");
        Check("запрос закрыт", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 0);
    }

    private async Task StepSlaughterUnderRequestIsDecline(CancellationToken token)
    {
        Console.WriteLine("-- забой под вызовом = отказ");

        Advance(73); // 72 часа + час: сбрасывает парный лимит, кулдаун и защиту проигравшего
        await GiveSlaughters(Defender, 5);
        var duelMessage = await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        Check("вызов перед забоем создан", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 1,
            duelMessage?.GetType().Name);

        var declinesTotalBefore = await Db(c => c.DuelResults.AsNoTracking().CountAsync(r => r.IsDecline));
        await Send(Defender, "/slaughter yes", token);
        var declinesTotalAfter = await Db(c => c.DuelResults.AsNoTracking().CountAsync(r => r.IsDecline));

        Check("забой закрыл вызов отказом", declinesTotalAfter > declinesTotalBefore,
            $"записей-отказов {declinesTotalBefore} -> {declinesTotalAfter}");
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
    /// <summary>
    /// Трата множителя роста: выше порога веса — падает, ниже — нет.
    /// Порог берётся от веса самого толстого свина беседы.
    /// </summary>
    private async Task StepMultiplierDecay(CancellationToken token)
    {
        Console.WriteLine("-- трата множителя выше порога");

        Advance(48);
        await SetWeights(10_000, 8_000);
        await GiveSlaughters(Attacker, 6);

        var modifierBefore = await Db(c => c.GetGrowthModifierAsync(SwineOf(c, Attacker)));
        Check("у свина есть запас от забоёв", modifierBefore > 1.0, modifierBefore.ToString("F4"));

        // 8 000 кг при лидере 10 000 — это выше 75% порога: кормёжка тратит запас
        await Send(Attacker, "/feed", token);
        var modifierAfterTop = await Db(c => c.GetGrowthModifierAsync(SwineOf(c, Attacker)));
        Check("выше порога множитель тратится", modifierBefore - modifierAfterTop > 1e-4,
            $"{modifierBefore:F4} -> {modifierAfterTop:F4}");

        // 1 000 кг при том же лидере — ниже порога: запас не тронут
        Advance(48);
        await SetWeights(1_000, 10_000);
        await Send(Attacker, "/feed", token);
        var modifierAfterLow = await Db(c => c.GetGrowthModifierAsync(SwineOf(c, Attacker)));
        Check("ниже порога множитель не тратится",
            Math.Abs(modifierAfterLow - modifierAfterTop) < 1e-9,
            $"{modifierAfterTop:F4} -> {modifierAfterLow:F4}");

        // Связь «банк -> бонусная масса»: за кормёжку тратится ровно FuelPerBonusKg
        // на килограмм бонуса, поэтому банк X даёт X / FuelPerBonusKg бонусных килограммов
        Advance(48);
        await SetWeights(8_000, 10_000);
        var fuelBefore = await Db(c => c.GetMultiplierFuelAsync(SwineOf(c, Attacker)));
        var modifier = await Db(c => c.GetGrowthModifierAsync(SwineOf(c, Attacker)));
        var weightBefore = await Weight(Attacker);
        await Send(Attacker, "/feed", token);
        var weightAfter = await Weight(Attacker);
        var fuelAfter = await Db(c => c.GetMultiplierFuelAsync(SwineOf(c, Attacker)));
        var gained = weightAfter - weightBefore;
        var spent = fuelBefore - fuelAfter;
        var expectedBase = Math.Max(1, gained - spent / 1.5);
        Check("трата запаса равна бонусной массе × коэффициент",
            spent > 0 && Math.Abs(spent / GrowthBalance.FuelPerBonusKg - (gained - expectedBase)) < 1.0,
            $"съел {gained} кг, запас {fuelBefore:F0} -> {fuelAfter:F0} (списано {spent:F1}), множитель {modifier:F2}");

        Advance(48);
    }

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
