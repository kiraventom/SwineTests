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
/// Идут на чистой базе перед длинной симуляцией, после себя оставляют её пустой.
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

        // Пользователи и свины появляются в базе только после первого апдейта
        await Send(Attacker, "/feed", token);
        await Send(Defender, "/feed", token);

        await SetWeights(100, 50);
        await StepRequest(token);
        await StepAttackerCannotAnswer(token);
        await StepDefenderIsBlocked(token);
        await StepAccept(token);
        await StepRepeatedPress(token);
        await StepCooldown(token);
        await StepDecline(token);
        await StepExpiry(token);
        await StepStalePress(token);
        await StepInfoDoesNotCountDeclines(token);

        return _failures;
    }

    private void CheckDuelMath()
    {
        Console.WriteLine("-- формулы");

        Check("шанс защитника 100 vs 50 = 33%", DuelResolution.GetDefenderWinChance(100, 50) == 33,
            DuelResolution.GetDefenderWinChance(100, 50).ToString());
        Check("шанс защитника 10 vs 90 = 90%", DuelResolution.GetDefenderWinChance(10, 90) == 90);
        Check("ставка 20% от 100 кг", DuelResolution.GetStakeKg(100) == 20, DuelResolution.GetStakeKg(100).ToString());
        Check("ставка не меньше 1 кг", DuelResolution.GetStakeKg(4) == 1);
        Check("проигравший не падает ниже 1 кг", DuelResolution.GetStakeKg(1) == 0);
        // Матожидание боя для защитника равно нулю, поэтому честная цена отказа — только
        // символический минимум: иначе отказ (каждый день) перетаскивает вес к малышам
        Check("штраф за отказ = символический минимум (100 vs 50)",
            DuelResolution.GetDeclineFeeKg(100, 50) == 1, DuelResolution.GetDeclineFeeKg(100, 50).ToString());
        Check("штраф за отказ = символический минимум (150 vs 7901)",
            DuelResolution.GetDeclineFeeKg(150, 7901) == 1, DuelResolution.GetDeclineFeeKg(150, 7901).ToString());
        Check("штраф не съедает последний килограмм", DuelResolution.GetDeclineFeeKg(7901, 2) == 1,
            DuelResolution.GetDeclineFeeKg(7901, 2).ToString());
        Check("матожидание боя для защитника ≈ 0 при любой разнице весов",
            Math.Abs(DuelResolution.GetExpectedDelta(100, 50)) < 1
            && Math.Abs(DuelResolution.GetExpectedDelta(150, 7901)) < 1
            && Math.Abs(DuelResolution.GetExpectedDelta(2630, 7901)) < 1,
            $"{DuelResolution.GetExpectedDelta(100, 50):F2}, {DuelResolution.GetExpectedDelta(150, 7901):F2}, {DuelResolution.GetExpectedDelta(2630, 7901):F2}");

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

        var message = await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        Check("вызов ушёл с двумя кнопками", message?.Keyboard?.InlineKeyboard?.Sum(r => r.Count()) == 2);
        Check("виден шанс на победу", message?.Text.Contains("Шанс на победу") == true);
        Check("видна ставка", message?.Text.Contains("Ставка") == true);
        Check("виден штраф за отказ", message?.Text.Contains("Отказ") == true);

        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstOrDefaultAsync());
        Check("запрос сохранён с MessageId", request?.MessageId is not null, $"MessageId={request?.MessageId}");
    }

    private async Task StepAttackerCannotAnswer(CancellationToken token)
    {
        Console.WriteLine("-- чужое нажатие");

        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstAsync());
        var (_, toast) = await Press(Attacker, $"accept_{request.RequestId}", token);

        Check("атакующий получил отказ по правам", toast?.Contains("владелец") == true, toast);
        Check("запрос не тронут", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 1);
    }

    private async Task StepDefenderIsBlocked(CancellationToken token)
    {
        Console.WriteLine("-- защитник заблокирован");

        var message = await Send(Defender, "/feed", token);
        Check("вместо кормления — напоминание", message?.GetType().Name == "DuelReminderMessage", message?.GetType().Name);
        Check("в напоминании ссылка на вызов", message?.Text.Contains("https://t.me/c/") == true);
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
        Check("вес перешёл одной стороне",
            attackerAfter > attackerBefore && defenderAfter < defenderBefore
            || attackerAfter < attackerBefore && defenderAfter > defenderBefore,
            $"атакующий {attackerBefore}->{attackerAfter}, защитник {defenderBefore}->{defenderAfter}");

        var result = await Db(c => c.DuelResults.AsNoTracking().OrderByDescending(r => r.DuelResultId).FirstAsync());
        Check("результат записан как бой", !result.IsDecline);
        Check("ставка одинаковая с обеих сторон",
            result.WinnerWeightAfter - result.WinnerWeightBefore == result.LoserWeightBefore - result.LoserWeightAfter);

        var edited = editor.Edited.LastOrDefault();
        Check("сообщение заменено итогом", edited?.Text?.Contains("побеждает") == true, edited?.Text);
        Check("кнопки сняты", edited?.Buttons == 0, $"buttons={edited?.Buttons}");
    }

    private async Task StepRepeatedPress(CancellationToken token)
    {
        Console.WriteLine("-- повторное нажатие");

        var lastResult = await Db(c => c.DuelResults.AsNoTracking().OrderByDescending(r => r.DuelResultId).FirstAsync());
        var (_, toast) = await Press(Defender, $"accept_{lastResult.DuelResultId}", token);
        Check("тост «неактуален»", toast?.Contains("неактуален") == true, toast);
    }

    private async Task StepCooldown(CancellationToken token)
    {
        Console.WriteLine("-- кулдаун");

        var attackerMessage = await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        Check("атакующий на кулдауне", attackerMessage?.GetType().Name == "TooSoonToDuelMessage", attackerMessage?.GetType().Name);

        var defenderMessage = await Send(Defender, $"/duel {Attacker.TelegramId}", token);
        Check("защитник тоже на кулдауне", defenderMessage?.GetType().Name == "TooSoonToDuelMessage", defenderMessage?.GetType().Name);
    }

    private async Task StepDecline(CancellationToken token)
    {
        Console.WriteLine("-- отказ");

        Advance(DuelBalance.CooldownHours + 1);
        await SetWeights(100, 50);

        await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstAsync());
        var (attackerBefore, defenderBefore) = (await Weight(Attacker), await Weight(Defender));

        var (_, toast) = await Press(Defender, $"decline_{request.RequestId}", token);
        var (attackerAfter, defenderAfter) = (await Weight(Attacker), await Weight(Defender));
        var expectedFee = DuelResolution.GetDeclineFeeKg(attackerBefore, defenderBefore);

        Check("тост про отказ", toast?.Contains("Вызов отклонён") == true, toast);
        Check($"штраф {expectedFee} кг ушёл вызывающему", attackerAfter == attackerBefore + expectedFee && defenderAfter == defenderBefore - expectedFee,
            $"атакующий {attackerBefore}->{attackerAfter}, защитник {defenderBefore}->{defenderAfter}");

        var result = await Db(c => c.DuelResults.AsNoTracking().OrderByDescending(r => r.DuelResultId).FirstAsync());
        Check("запись помечена как отказ", result.IsDecline);

        var edited = editor.Edited.LastOrDefault();
        Check("сообщение про отказ со штрафом", edited?.Text?.Contains("отказывается") == true && edited.Text.Contains("Штраф") == true, edited?.Text);
    }

    private async Task StepExpiry(CancellationToken token)
    {
        Console.WriteLine("-- истечение вызова");

        Advance(DuelBalance.CooldownHours + 1);
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);

        Advance(DuelRequestExtensions.TTL.TotalHours + 1);
        var feedMessage = await Send(Defender, "/feed", token);
        Check("защитник снова может кормить", feedMessage?.GetType().Name == "FeedMessage", feedMessage?.GetType().Name);
        Check("просроченный запрос вычищен", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 0);
    }

    private async Task StepStalePress(CancellationToken token)
    {
        Console.WriteLine("-- нажатие по просроченному вызову");

        Advance(DuelBalance.CooldownHours + 1);
        await Send(Attacker, $"/duel {Defender.TelegramId}", token);
        var request = await Db(c => c.DuelRequests.AsNoTracking().OrderBy(dr => dr.RequestId).FirstAsync());

        Advance(DuelRequestExtensions.TTL.TotalHours + 1);
        var (_, toast) = await Press(Defender, $"accept_{request.RequestId}", token);

        Check("тост «устарел»", toast == "Вызов устарел", toast);
        Check("сообщение про истечение", editor.Edited.LastOrDefault()?.Text?.Contains("истёк") == true, editor.Edited.LastOrDefault()?.Text);
        Check("запрос удалён", await Db(c => c.DuelRequests.AsNoTracking().CountAsync()) == 0);
    }

    private async Task StepInfoDoesNotCountDeclines(CancellationToken token)
    {
        Console.WriteLine("-- отказы не попадают в статистику /info");

        await Exec(c => c.DuelResults.RemoveRange(c.DuelResults.AsTracking().ToList()));
        await Exec(c =>
        {
            c.DuelResults.Add(new DuelResult
            {
                AttackerId = AttackerSwineId(c), DefenderId = DefenderSwineId(c), AttackerWon = true, IsDecline = false,
                DateTime = ((MockDateTimeNowProvider)now).UtcNow, WinnerWeightBefore = 100, WinnerWeightAfter = 120, LoserWeightBefore = 50, LoserWeightAfter = 30,
            });
            c.DuelResults.Add(new DuelResult
            {
                AttackerId = DefenderSwineId(c), DefenderId = AttackerSwineId(c), AttackerWon = true, IsDecline = true,
                DateTime = ((MockDateTimeNowProvider)now).UtcNow, WinnerWeightBefore = 50, WinnerWeightAfter = 52, LoserWeightBefore = 120, LoserWeightAfter = 118,
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

    private int AttackerSwineId(UserContext context) => SwineOf(context, Attacker).SwineId;
    private int DefenderSwineId(UserContext context) => SwineOf(context, Defender).SwineId;

    private static Swine SwineOf(UserContext context, TestUser user)
    {
        var dbUser = context.Users.First(u => u.TelegramId == user.TelegramId);
        return context.Swines.First(s => s.OwnerId == dbUser.UserId);
    }

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
