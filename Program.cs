using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using SwineBot;
using SwineBot.Model;
using SwineBot.Senders;
using static System.Environment;

namespace SwineTests;

/// <summary>
/// Настройки прогона. Поля, которые повторяют настройки бота (ChanceSource, Debuff, Glory,
/// StakeShare, MinChance, MaxChance, GrowthModel, GrowthK, ModFuel, EliteShare, HeirShare),
/// равны null, если флаг не задан: тогда работает значение из бота, а не копия в харнессе.
/// Иначе харнесс однажды снова начнёт мерить не то, что поедет в прод.
/// </summary>
public record Settings(int DaysCount, bool Verbose, bool NoSlaughter, double DuelChance, bool Replay, double DuelConsent, int DuelsFromDay, string ChanceSource, string Debuff, bool? Glory, int StopSlaughterDay, bool IncludeKiller, double? StakeShare, string Duelists, int? MinChance, int? MaxChance, double DuelMinWeightRatio, string CallModel, string GrowthModel, double? GrowthK, double? ModFuel, double? EliteShare, string SpoilerOwner, double SpoilerShare, double? HeirShare, string DbPath, string AchievDataFile);

internal class Program
{
    private const string USAGE = "Usage: SwineTests --days <days_count> [--db <path>] [--achiev <path>] [--verbose] [--no-slaughter] [--duel-chance <0..1>] [--replay] [--consent <0..1>] [--duels-from <day>] [--chance age|witness|achievements|none] [--debuff feed-steal|depreciate|none] [--no-glory] [--stop-slaughter <day>] [--killer] [--stake <0..1>] [--duelists Kevish,Ганс] [--min-chance <5..50>] [--max-chance <50..95>] [--min-weight-ratio <0..1>] [--call-model nearest|any] [--growth sqrt|linear] [--growth-k <0..1>] [--mod-fuel <0..10>] [--elite-share <0..1>] [--spoiler <owner>] [--spoiler-share <0..1>] [--heir-share <0..1>]";

    private const string DEFAULT_ACHIEV_PATH = "../SwineBot/Achievements/achiev_data.json";

    /// <summary>
    /// Telegram validates the token format when the bot client is constructed, so the fake
    /// token must look real. Nothing is ever sent to Telegram: all senders are mocked.
    /// </summary>
    /// <summary>Chance per user per simulated hour to challenge somebody to a duel.</summary>
    private const double DEFAULT_DUEL_CHANCE = 0.02;

    private const string FAKE_TOKEN = "123456789:AAEqeqeqeqeqeqeqeqeqeqeqeqeqeqeqeqe";

    private static async Task<int> Main(string[] args)
    {
        var settings = ParseSettings(args);
        if (settings is null)
        {
            Console.Error.WriteLine(USAGE);
            return 2;
        }

        // Настройки боевых механик передаём в бот до старта хоста.
        // Трогаем только то, что явно задано флагом: по умолчанию харнесс работает
        // на дефолтах бота, иначе легко снова померить не ту конфигурацию.
        if (settings.ChanceSource is { } chanceSource)
        {
            SwineBot.Actions.Commands.Duel.DuelBalance.ChanceSource = chanceSource switch
            {
                "age" => SwineBot.Actions.Commands.Duel.DuelChanceSource.Age,
                "witness" => SwineBot.Actions.Commands.Duel.DuelChanceSource.WitnessedKills,
                "achievements" => SwineBot.Actions.Commands.Duel.DuelChanceSource.Achievements,
                _ => SwineBot.Actions.Commands.Duel.DuelChanceSource.None,
            };
        }

        if (settings.Debuff is { } debuff)
        {
            SwineBot.Actions.Commands.Duel.DuelBalance.Debuff = debuff switch
            {
                "feed-steal" => SwineBot.Actions.Commands.Duel.DuelDebuff.FeedSteal,
                "depreciate" => SwineBot.Actions.Commands.Duel.DuelDebuff.Depreciate,
                _ => SwineBot.Actions.Commands.Duel.DuelDebuff.None,
            };
        }

        // Модель роста от забоёв: линейная (старая) или суб-линейная (корень)
        if (settings.GrowthModel is { } growthModel)
            SwineBot.Model.GrowthBalance.SubLinear = growthModel == "sqrt";

        if (settings.GrowthK is { } growthK)
            SwineBot.Model.GrowthBalance.PerSqrtKg = growthK;

        if (settings.ModFuel is { } modFuel)
            SwineBot.Model.GrowthBalance.FuelPerBonusKg = modFuel;

        if (settings.HeirShare is { } heirShare)
            SwineBot.Model.GrowthBalance.HeirInheritanceShare = heirShare;

        if (settings.EliteShare is { } eliteShare)
            SwineBot.Model.GrowthBalance.EliteThresholdShare = eliteShare;

        if (settings.Glory is { } glory)
            SwineBot.Actions.Commands.Duel.DuelBalance.GloryEnabled = glory;

        if (settings.StakeShare is { } stakeShare)
            SwineBot.Actions.Commands.Duel.DuelBalance.StakeShare = stakeShare;

        if (settings.MinChance is { } minChance)
            SwineBot.Actions.Commands.Duel.DuelBalance.MinWinChance = minChance;

        if (settings.MaxChance is { } maxChance)
            SwineBot.Actions.Commands.Duel.DuelBalance.MaxWinChance = maxChance;

        PrintEffectiveBalance();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateBootstrapLogger();

        Log.Information("Building host...");

        if (File.Exists(settings.DbPath))
            File.Delete(settings.DbPath);

        var config = new Config(FAKE_TOKEN, "USERNAME", $"Data Source={settings.DbPath};Cache=Shared", settings.AchievDataFile);

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services
                .AddSerilog(ConfigureLogger)
                .AddSingleton(settings)
                .AddSingleton<MockBotMessageSender>()
                .AddSingleton<MockCallbackQueryAnswerer>()
                .AddSingleton<MockMessageEditor>()
                .AddSingleton<MockCommandMenuSender>()
                .AddSingleton<IBotMessageSender>(sp => sp.GetRequiredService<MockBotMessageSender>());

            SwineBot.Program.RegisterServices(builder.Services)
                .RemoveAll<Config>().AddSingleton<Config>(config)
                .RemoveAll<IDateTimeNowProvider>().AddSingleton<IDateTimeNowProvider, MockDateTimeNowProvider>()
                .RemoveAll<IBotMessageSender>().AddSingleton<IBotMessageSender>(sp => sp.GetRequiredService<MockBotMessageSender>())
                .RemoveAll<ICallbackQueryAnswerer>().AddSingleton<ICallbackQueryAnswerer>(sp => sp.GetRequiredService<MockCallbackQueryAnswerer>())
                .RemoveAll<IMessageEditor>().AddSingleton<IMessageEditor>(sp => sp.GetRequiredService<MockMessageEditor>())
                // Меню команд в прогонах никуда не уходит: иначе сценарии светились бы в Telegram
                .RemoveAll<ICommandMenuSender>().AddSingleton<ICommandMenuSender>(sp => sp.GetRequiredService<MockCommandMenuSender>())
                .AddSingleton<IHostedService>(provider => settings.Replay
                    ? ActivatorUtilities.CreateInstance<ReplayService>(provider)
                    : ActivatorUtilities.CreateInstance<TestService>(provider));

            var host = builder.Build();

            using (var scope = host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<UserContext>();
                await context.Database.MigrateAsync();
            }

            Log.Information("Starting host...");

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to run host, terminating");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }

        return ExitCode;
    }

    /// <summary>
    /// Печатает действующие значения баланса. Без этого лог нельзя привязать к конфигурации,
    /// и ошибку «харнесс переопределил коэффициент своим» снова никто не заметит.
    /// </summary>
    private static void PrintEffectiveBalance()
    {
        // DuelBalance и GrowthBalance — статические классы, поэтому к ним обращаемся по имени типа
        Console.WriteLine("=== Баланс (действующие значения)");
        Console.WriteLine($"duels: chance={SwineBot.Actions.Commands.Duel.DuelBalance.ChanceSource}, "
                          + $"debuff={SwineBot.Actions.Commands.Duel.DuelBalance.Debuff}, "
                          + $"glory={SwineBot.Actions.Commands.Duel.DuelBalance.GloryEnabled}, "
                          + $"stake={SwineBot.Actions.Commands.Duel.DuelBalance.StakeShare:F3}, "
                          + $"min/max-chance={SwineBot.Actions.Commands.Duel.DuelBalance.MinWinChance}/{SwineBot.Actions.Commands.Duel.DuelBalance.MaxWinChance}");
        Console.WriteLine($"growth: model={(SwineBot.Model.GrowthBalance.SubLinear ? "sqrt" : "linear")}, "
                          + $"k={SwineBot.Model.GrowthBalance.PerSqrtKg:F4}, "
                          + $"fuel-per-bonus-kg={SwineBot.Model.GrowthBalance.FuelPerBonusKg:F2}, "
                          + $"elite-share={SwineBot.Model.GrowthBalance.EliteThresholdShare:F2}, "
                          + $"heir-share={SwineBot.Model.GrowthBalance.HeirInheritanceShare:F2}");
    }

    private static Settings ParseSettings(string[] args)
    {
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();

        for (int i = 0; i < args.Length; ++i)
        {
            var arg = args[i];
            if (!arg.StartsWith("--"))
            {
                Console.Error.WriteLine($"Unexpected argument \"{arg}\"");
                return null;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                values[arg] = args[++i];
            else
                flags.Add(arg);
        }

        if (!values.TryGetValue("--days", out var daysStr) || !int.TryParse(daysStr, out var days) || days <= 0)
        {
            Console.Error.WriteLine("--days is not set or is set incorrectly");
            return null;
        }

        var duelChance = DEFAULT_DUEL_CHANCE;
        if (values.TryGetValue("--duel-chance", out var duelChanceStr)
            && (!double.TryParse(duelChanceStr, System.Globalization.CultureInfo.InvariantCulture, out duelChance) || duelChance is < 0 or > 1))
        {
            Console.Error.WriteLine("--duel-chance must be a number between 0 and 1");
            return null;
        }

        var consent = 0.55;
        if (values.TryGetValue("--consent", out var consentStr)
            && (!double.TryParse(consentStr, System.Globalization.CultureInfo.InvariantCulture, out consent) || consent is < 0 or > 1))
        {
            Console.Error.WriteLine("--consent must be a number between 0 and 1");
            return null;
        }

        var duelsFromDay = 0;
        if (values.TryGetValue("--duels-from", out var duelsFromStr)
            && (!int.TryParse(duelsFromStr, out duelsFromDay) || duelsFromDay < 0))
        {
            Console.Error.WriteLine("--duels-from must be a non-negative day number");
            return null;
        }

        string chanceSource = null;
        if (values.TryGetValue("--chance", out var chanceStr))
        {
            if (!new[] { "age", "witness", "achievements", "none" }.Contains(chanceStr))
            {
                Console.Error.WriteLine("--chance must be age, witness, achievements or none");
                return null;
            }

            chanceSource = chanceStr;
        }

        string debuff = null;
        if (values.TryGetValue("--debuff", out var debuffStr))
        {
            if (!new[] { "feed-steal", "depreciate", "none" }.Contains(debuffStr))
            {
                Console.Error.WriteLine("--debuff must be feed-steal, depreciate or none");
                return null;
            }

            debuff = debuffStr;
        }

        var stopSlaughterDay = -1;
        if (values.TryGetValue("--stop-slaughter", out var stopStr)
            && (!int.TryParse(stopStr, out stopSlaughterDay) || stopSlaughterDay < 0))
        {
            Console.Error.WriteLine("--stop-slaughter must be a non-negative day number");
            return null;
        }

        double? stakeShare = null;
        if (values.TryGetValue("--stake", out var stakeStr))
        {
            if (!double.TryParse(stakeStr, System.Globalization.CultureInfo.InvariantCulture, out var stake)
                || stake is <= 0 or > 1)
            {
                Console.Error.WriteLine("--stake must be a number between 0 and 1");
                return null;
            }

            stakeShare = stake;
        }

        int? minChance = null;
        if (values.TryGetValue("--min-chance", out var minStr))
        {
            if (!int.TryParse(minStr, out var minParsed))
            {
                Console.Error.WriteLine("--min-chance must be an integer");
                return null;
            }

            minChance = minParsed;
        }

        int? maxChance = null;
        if (values.TryGetValue("--max-chance", out var maxStr))
        {
            if (!int.TryParse(maxStr, out var maxParsed))
            {
                Console.Error.WriteLine("--max-chance must be an integer");
                return null;
            }

            maxChance = maxParsed;
        }

        var minWeightRatio = 0.0;
        if (values.TryGetValue("--min-weight-ratio", out var ratioStr)
            && (!double.TryParse(ratioStr, System.Globalization.CultureInfo.InvariantCulture, out minWeightRatio) || minWeightRatio is < 0 or > 1))
        {
            Console.Error.WriteLine("--min-weight-ratio must be a number between 0 and 1");
            return null;
        }

        string growthModel = null;
        if (values.TryGetValue("--growth", out var growthStr))
        {
            if (!new[] { "sqrt", "linear" }.Contains(growthStr))
            {
                Console.Error.WriteLine("--growth must be sqrt or linear");
                return null;
            }

            growthModel = growthStr;
        }

        double? growthK = null;
        if (values.TryGetValue("--growth-k", out var growthKStr))
        {
            if (!double.TryParse(growthKStr, System.Globalization.CultureInfo.InvariantCulture, out var k)
                || k is <= 0 or > 1)
            {
                Console.Error.WriteLine("--growth-k must be a number between 0 and 1");
                return null;
            }

            growthK = k;
        }

        double? modFuel = null;
        if (values.TryGetValue("--mod-fuel", out var fuelStr))
        {
            if (!double.TryParse(fuelStr, System.Globalization.CultureInfo.InvariantCulture, out var fuel)
                || fuel is < 0 or > 10)
            {
                Console.Error.WriteLine("--mod-fuel must be a number between 0 and 10");
                return null;
            }

            modFuel = fuel;
        }

        double? eliteShare = null;
        if (values.TryGetValue("--elite-share", out var shareStr))
        {
            if (!double.TryParse(shareStr, System.Globalization.CultureInfo.InvariantCulture, out var share)
                || share is <= 0 or > 1)
            {
                Console.Error.WriteLine("--elite-share must be a number between 0 and 1");
                return null;
            }

            eliteShare = share;
        }

        var spoilerOwner = values.TryGetValue("--spoiler", out var spoilerStr) ? spoilerStr : "";
        var spoilerShare = 0.7;
        if (values.TryGetValue("--spoiler-share", out var spoilerShareStr)
            && (!double.TryParse(spoilerShareStr, System.Globalization.CultureInfo.InvariantCulture, out spoilerShare) || spoilerShare is <= 0 or > 1))
        {
            Console.Error.WriteLine("--spoiler-share must be a number between 0 and 1");
            return null;
        }

        double? heirShare = null;
        if (values.TryGetValue("--heir-share", out var heirStr))
        {
            if (!double.TryParse(heirStr, System.Globalization.CultureInfo.InvariantCulture, out var heir)
                || heir is < 0 or > 1)
            {
                Console.Error.WriteLine("--heir-share must be a number between 0 and 1");
                return null;
            }

            heirShare = heir;
        }

        var callModel = "nearest";
        if (values.TryGetValue("--call-model", out var callModelStr))
        {
            if (callModelStr is not ("nearest" or "any"))
            {
                Console.Error.WriteLine("--call-model must be nearest or any");
                return null;
            }

            callModel = callModelStr;
        }

        var dbPath = values.TryGetValue("--db", out var customDbPath) ? customDbPath : DefaultDbPath();
        var achievPath = values.TryGetValue("--achiev", out var customAchievPath) ? customAchievPath : DEFAULT_ACHIEV_PATH;

        if (!File.Exists(achievPath))
        {
            Console.Error.WriteLine($"Achievements data file \"{achievPath}\" not found");
            return null;
        }

        // --no-glory только выключает славу; без флага значение берётся из бота
        bool? glory = flags.Contains("--no-glory") ? false : null;

        return new Settings(days, flags.Contains("--verbose"), flags.Contains("--no-slaughter"), duelChance, flags.Contains("--replay"), consent, duelsFromDay, chanceSource, debuff, glory, stopSlaughterDay, flags.Contains("--killer"), stakeShare, values.TryGetValue("--duelists", out var duelistsStr) ? duelistsStr : "", minChance, maxChance, minWeightRatio, callModel, growthModel, growthK, modFuel, eliteShare, spoilerOwner, spoilerShare, heirShare, dbPath, Path.GetFullPath(achievPath));
    }

    private static string DefaultDbPath()
    {
        var dataDir = Path.Combine(GetFolderPath(SpecialFolder.LocalApplicationData), "SwineTests");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "users.db");
    }

    private static void ConfigureLogger(IServiceProvider provider, LoggerConfiguration logger)
    {
        logger.MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information);
    }
}
