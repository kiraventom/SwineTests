using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwineBot.Actions.Commands;
using Telegram.Bot.Types;

namespace SwineTests;

/// <summary>
/// Проверки меню команд: бот ставит его сам при старте из тех же [CommandInfo],
/// по которым /start собирает справку. Здесь важно, что в меню попадают ровно
/// публичные команды (никаких /duelcancel и /duelremind), имена проходят правила
/// Telegram, а сетевая ошибка не роняет старт.
///
/// База не нужна, поэтому шаг идёт первым — до /pet и дуэлей.
/// </summary>
public class CommandMenuScenarios(IServiceScopeFactory spf, MockCommandMenuSender sender)
{
    private readonly List<string> _failures = [];

    public async Task<IReadOnlyList<string>> Run(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("=== Command menu scenarios ===");

        try
        {
            await StepRealMenu(token);
            await StepHandleNormalization(token);
            await StepTelegramFailure(token);
        }
        finally
        {
            sender.Fail = null;
            sender.Sent.Clear();
        }

        return _failures;
    }

    private async Task StepRealMenu(CancellationToken token)
    {
        Console.WriteLine("-- меню из реальных [CommandInfo]");

        using var scope = spf.CreateScope();
        var menu = scope.ServiceProvider.GetRequiredService<BotCommandMenu>();
        var infos = scope.ServiceProvider.GetRequiredService<ICommandInfos>();

        var registered = await menu.Register(token);

        Check("меню отправлено в Telegram", registered && sender.Sent.Count == 1, $"registered={registered}, requests={sender.Sent.Count}");

        var commands = sender.Sent.LastOrDefault() ?? [];
        var names = commands.Select(c => c.Command).ToList();
        var publicNames = infos.Infos.Where(i => !i.Hidden).Select(i => i.Handle.TrimStart('/')).ToList();

        Check("в меню ровно публичные команды из [CommandInfo]",
            names.OrderBy(n => n).SequenceEqual(publicNames.OrderBy(n => n)),
            $"меню: {string.Join(", ", names)}; ожидалось: {string.Join(", ", publicNames)}");

        // Регрессия на PR #8: команда дуэлей работала, но появлялась в меню только после
        // ручного шага в BotFather
        Check("/duel попал в меню без ручного шага в BotFather", names.Contains("duel"), string.Join(", ", names));

        Check("скрытые и служебные команды в меню не попали",
            !names.Contains("duelcancel") && !names.Contains("duelremind"),
            string.Join(", ", names));

        Check("имена команд по правилам Telegram",
            commands.All(c => IsValidCommandName(c.Command)),
            string.Join(", ", names));

        Check("описания непустые и не длиннее 256 символов",
            commands.All(c => !string.IsNullOrWhiteSpace(c.Description) && c.Description.Length <= BotCommandMenu.MAX_DESCRIPTION_LENGTH),
            string.Join(", ", commands.Select(c => $"{c.Command}:{c.Description?.Length ?? 0}")));

        Check("дублей в меню нет", names.Distinct().Count() == names.Count, string.Join(", ", names));
    }

    private async Task StepHandleNormalization(CancellationToken token)
    {
        Console.WriteLine("-- нормализация handle");

        using var scope = spf.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<BotCommandMenu>();

        var infos = new FakeCommandInfos(
        [
            // Handle с аргументом: в меню должно уйти имя без хвоста, как в /start
            new CommandInfo("/setname имя", "/setname имя", "Поменять имя свинки", false, true, typeof(object)),
            // Регистр не важен: Telegram ждёт строчные
            new CommandInfo("/Feed", "/Feed", "Покормить своего свина", false, true, typeof(object)),
            // Дефис Telegram не примет — такую команду пропускаем, а не роняем старт
            new CommandInfo("/bad-name", "/bad-name", "Сломано", false, true, typeof(object)),
            new CommandInfo("/", "/", "Пустое имя", false, true, typeof(object)),
            // Скрытая команда в меню не идёт
            new CommandInfo("/hidden", "/hidden", "Внутренняя", true, true, typeof(object)),
            // Описание длиннее лимита Telegram обрезается, но команда остаётся
            new CommandInfo("/long", "/long", new string('я', 300), false, true, typeof(object)),
        ]);

        var menu = new BotCommandMenu(infos, sender, logger);
        var commands = menu.Build();

        Check("handle с аргументом обрезан до имени",
            commands.Any(c => c.Command == "setname"),
            string.Join(", ", commands.Select(c => c.Command)));

        Check("регистр handle приведён к нижнему",
            commands.Any(c => c.Command == "feed") && !commands.Any(c => c.Command == "Feed"),
            string.Join(", ", commands.Select(c => c.Command)));

        Check("невалидные имена пропущены",
            !commands.Any(c => c.Command.Contains('-')) && commands.All(c => c.Command.Length > 0),
            string.Join(", ", commands.Select(c => c.Command)));

        Check("скрытая команда не попала в меню",
            !commands.Any(c => c.Command == "hidden"),
            string.Join(", ", commands.Select(c => c.Command)));

        Check("длинное описание обрезано до 256",
            commands.FirstOrDefault(c => c.Command == "long")?.Description.Length == BotCommandMenu.MAX_DESCRIPTION_LENGTH,
            commands.FirstOrDefault(c => c.Command == "long")?.Description.Length.ToString());

        await Task.CompletedTask;
    }

    private async Task StepTelegramFailure(CancellationToken token)
    {
        Console.WriteLine("-- Telegram недоступен");

        using var scope = spf.CreateScope();
        var menu = scope.ServiceProvider.GetRequiredService<BotCommandMenu>();

        sender.Fail = new HttpRequestException("simulated: api.telegram.org is unreachable");

        var registered = await menu.Register(token);

        // Меню — не причина не запускаться: в Telegram останется прежний список
        Check("падение отправки меню не считается фатальным", !registered, $"registered={registered}");
    }

    private static bool IsValidCommandName(string command)
        => command is { Length: >= 1 and <= BotCommandMenu.MAX_COMMAND_LENGTH }
           && command.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private void Check(string name, bool ok, string details = null)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(details is null ? "" : $"  <- {details}")}");
        if (!ok)
            _failures.Add(name);
    }

    private class FakeCommandInfos(IReadOnlyList<CommandInfo> infos) : ICommandInfos
    {
        public IReadOnlyList<CommandInfo> Infos { get; } = infos;
    }
}
