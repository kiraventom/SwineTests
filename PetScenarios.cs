using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwineBot;
using SwineBot.BotMessages;
using SwineBot.Model;
using SwineBot.Updates;

namespace SwineTests;

/// <summary>
/// Проверки команды /pet. Идут на чистой базе до дуэлей: здесь важны и вес, и ачивки,
/// а дуэльные сценарии их уже двигают.
///
/// Секрет проверяется детерминированно: TickCount — мок (0 — «попал», 1 — «мимо»),
/// а дополнительный бросок 1/100 выключается через PetBalance.SecretChanceDivisor = 1.
/// В обычном прогоне тик всегда «мимо», поэтому случайных +100 кг в медианах не бывает.
/// </summary>
public class PetScenarios(IServiceScopeFactory spf, MockBotMessageSender sender, MockTickProvider tick, IReadOnlyList<TestUser> users)
{
    private readonly List<string> _failures = [];

    private TestUser Owner => users[0];

    public async Task<IReadOnlyList<string>> Run(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("=== Pet scenarios ===");

        var savedDivisor = PetBalance.SecretChanceDivisor;

        try
        {
            await StepPlainPet(token);
            await StepSecretPet(token);
        }
        finally
        {
            PetBalance.SecretChanceDivisor = savedDivisor;
            tick.TickCount = 1;
            await SetWeight(1);
        }

        return _failures;
    }

    private async Task StepPlainPet(CancellationToken token)
    {
        Console.WriteLine("-- поглаживание без секрета");

        tick.TickCount = 1;

        // Пользователь и свин появляются в базе только по первому апдейту, поэтому
        // первое поглаживание — оно же создание свина с весом 1
        await Send(Owner, "/pet", token);
        Check("новый свин после /pet весит 1 кг", await Weight() == 1, (await Weight()).ToString());

        await SetWeight(60);

        var messages = await Send(Owner, "/pet", token);
        var petMessage = messages.OfType<PetMessage>().LastOrDefault();

        // Текст уходит в Telegram в MarkdownV2, где «+», «.» и прочее экранированы:
        // для проверок снимаем экранирование, иначе сравнивать приходится с "\+"
        var text = petMessage?.Text.ToString().Replace("\\", "");

        Check("бот ответил на /pet", petMessage is not null, string.Join(", ", messages.Select(m => m.GetType().Name)));
        Check("обычное поглаживание не меняет вес", await Weight() == 60, (await Weight()).ToString());
        Check("текст поглаживания — про ласку, без прибавки",
            text is not null
            && (text.Contains("гладите") || text.Contains("чешете") || text.Contains("проводите"))
            && !text.Contains("+ 100 кг"), text);

        var achievs = await Db(c => c.Achievements.CountAsync(a => a.Type == AchievementType.Weight));
        Check("секрет не выпал — весовых ачивок не появилось", achievs == 0, $"Weight x{achievs}");
    }

    private async Task StepSecretPet(CancellationToken token)
    {
        Console.WriteLine("-- секрет: TickCount делится на 100");

        tick.TickCount = 0;
        PetBalance.SecretChanceDivisor = 1;
        await SetWeight(50);

        var messages = await Send(Owner, "/pet", token);
        var petMessage = messages.OfType<PetMessage>().LastOrDefault();
        var text = petMessage?.Text.ToString().Replace("\\", "");
        Console.WriteLine("    " + text);

        Check("секрет добавил ровно SecretBonusKg", await Weight() == 50 + PetBalance.SecretBonusKg,
            (await Weight()).ToString());
        Check("в тексте есть строка веса 50 кг + 100 кг → 150 кг",
            text?.Contains("50 кг + 100 кг → 150 кг") == true,
            text?.Split('\n').LastOrDefault());
        Check("весовая ачивка сработала на секрете",
            messages.OfType<AchievementMessage>().Any(),
            string.Join(", ", messages.Select(m => m.GetType().Name)));

        var weightAchievs = await Db(c => c.Achievements
            .Where(a => a.Type == AchievementType.Weight)
            .Select(a => a.Value)
            .ToListAsync());
        Check("ачивка «Сотка» выдана за переход через 100 кг",
            weightAchievs.Contains(100), string.Join(", ", weightAchievs));

        // Секрет — не кормёжка: иначе он ломал бы счётчики перекорма и график /history
        var feeds = await Db(c => c.Feeds.CountAsync());
        Check("секрет не записан как кормёжка", feeds == 0, $"Feeds x{feeds}");
    }

    private async Task<long> Weight() => await Db(c => Task.FromResult(SwineOf(c).Weight));

    private async Task SetWeight(int weight) => await Exec(c => SwineOf(c).Weight = weight);

    private static Swine SwineOf(UserContext context)
    {
        var user = context.Users.First(u => u.TelegramId == TestUser.GetTelegramId(TestUserStrategy.RegularNoOverfeed));

        // Контекст работает с NoTracking, поэтому для записи свина его надо взять на учёт
        return context.Swines.AsTracking().First(s => s.OwnerId == user.UserId);
    }

    private async Task<IReadOnlyList<IBotMessage>> Send(TestUser user, string command, CancellationToken token)
    {
        using var scope = spf.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IUpdateHandler>();
        var before = sender.MessagesHistory.Count;

        var result = await handler.Handle(user.GenerateCommand(command), token);
        if (!IsSuccess(result))
            _failures.Add($"{user.Strategy} {command}: {result}");

        return sender.MessagesHistory.Skip(before).ToList();
    }

    private static bool IsSuccess(UpdateHandleResult result)
        => result is UpdateHandleResult.MessageOK
            or UpdateHandleResult.InlineQueryOK
            or UpdateHandleResult.CallbackQueryOK
            or UpdateHandleResult.MessageSuccesfulMigration;

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

    private void Check(string name, bool ok, string details = null)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(details is null ? "" : $"  <- {details}")}");
        if (!ok)
            _failures.Add(name);
    }
}
