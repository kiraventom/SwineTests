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

public record Settings(int DaysCount, bool Verbose, bool NoSlaughter, double DuelChance, bool Replay, double DuelConsent, int DuelsFromDay, string ChanceSource, string Debuff, bool Glory, int StopSlaughterDay, bool IncludeKiller, double StakeShare, string Duelists, int MinChance, int MaxChance, double DuelMinWeightRatio, string CallModel, string GrowthModel, double GrowthK, double ModFuel, double EliteShare, string SpoilerOwner, double SpoilerShare, double HeirShare, string DbPath, string AchievDataFile);

internal class Program
{
    private const string USAGE = "Usage: SwineTests --days <days_count> [--db <path>] [--achiev <path>] [--verbose] [--no-slaughter] [--duel-chance <0..1>] [--replay] [--consent <0..1>] [--duels-from <day>] [--chance age|witness|achievements|none] [--debuff feed-steal|depreciate|none] [--no-glory] [--stop-slaughter <day>] [--killer] [--stake <0..1>] [--duelists Kevish,Ганс] [--min-chance <5..50>] [--max-chance <50..95>] [--min-weight-ratio <0..1>] [--call-model nearest|any] [--growth sqrt|linear] [--growth-k <0..1>] [--mod-fuel <0..10>] [--elite-share <0..1>] [--spoiler <owner>] [--spoiler-share <0..1>] [--heir-share <0..1>]";

    private const string DEFAULT_ACHIEV_PATH = "../SwineBot/Achievements/achiev_data.json";

    /// <summary>Коэффициент суб-линейного роста по умолчанию — как в боте.</summary>
    private const double GrowthDefaultK = 0.020;

    /// <summary>Сколько запаса сжигает килограмм бонусной массы, по умолчанию — как в боте.</summary>
    private const double GrowthDefaultFuelPerBonus = 2.0;

    /// <summary>Доля веса лидера, с которой начинается трата.</summary>
    private const double GrowthDefaultEliteShare = 0.5;

    /// <summary>Доля запаса, которую наследует свин после забоя.</summary>
    private const double GrowthDefaultHeirShare = 0.4;

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

        // Настройки боевых механик передаём в бот до старта хоста
        SwineBot.Actions.Commands.Duel.DuelBalance.ChanceSource = settings.ChanceSource switch
        {
            "age" => SwineBot.Actions.Commands.Duel.DuelChanceSource.Age,
            "witness" => SwineBot.Actions.Commands.Duel.DuelChanceSource.WitnessedKills,
            "achievements" => SwineBot.Actions.Commands.Duel.DuelChanceSource.Achievements,
            _ => SwineBot.Actions.Commands.Duel.DuelChanceSource.None,
        };

        SwineBot.Actions.Commands.Duel.DuelBalance.Debuff = settings.Debuff switch
        {
            "feed-steal" => SwineBot.Actions.Commands.Duel.DuelDebuff.FeedSteal,
            "depreciate" => SwineBot.Actions.Commands.Duel.DuelDebuff.Depreciate,
            _ => SwineBot.Actions.Commands.Duel.DuelDebuff.None,
        };

        // Модель роста от забоёв: линейная (старая) или суб-линейная (корень)
        SwineBot.Model.GrowthBalance.SubLinear = settings.GrowthModel == "sqrt";
        SwineBot.Model.GrowthBalance.PerSqrtKg = settings.GrowthK;
        SwineBot.Model.GrowthBalance.FuelPerBonusKg = settings.ModFuel;
        SwineBot.Model.GrowthBalance.HeirInheritanceShare = settings.HeirShare;
        SwineBot.Model.GrowthBalance.EliteThresholdShare = settings.EliteShare;

        SwineBot.Actions.Commands.Duel.DuelBalance.GloryEnabled = settings.Glory;
        SwineBot.Actions.Commands.Duel.DuelBalance.StakeShare = settings.StakeShare;
        SwineBot.Actions.Commands.Duel.DuelBalance.MinWinChance = settings.MinChance;
        SwineBot.Actions.Commands.Duel.DuelBalance.MaxWinChance = settings.MaxChance;

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
                .AddSingleton<IBotMessageSender>(sp => sp.GetRequiredService<MockBotMessageSender>());

            SwineBot.Program.RegisterServices(builder.Services)
                .RemoveAll<Config>().AddSingleton<Config>(config)
                .RemoveAll<IDateTimeNowProvider>().AddSingleton<IDateTimeNowProvider, MockDateTimeNowProvider>()
                .RemoveAll<IBotMessageSender>().AddSingleton<IBotMessageSender>(sp => sp.GetRequiredService<MockBotMessageSender>())
                .RemoveAll<ICallbackQueryAnswerer>().AddSingleton<ICallbackQueryAnswerer>(sp => sp.GetRequiredService<MockCallbackQueryAnswerer>())
                .RemoveAll<IMessageEditor>().AddSingleton<IMessageEditor>(sp => sp.GetRequiredService<MockMessageEditor>())
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

        var chanceSource = values.TryGetValue("--chance", out var chanceStr) ? chanceStr : "none";
        if (!new[] { "age", "witness", "achievements", "none" }.Contains(chanceSource))
        {
            Console.Error.WriteLine("--chance must be age, witness, achievements or none");
            return null;
        }

        var debuff = values.TryGetValue("--debuff", out var debuffStr) ? debuffStr : "none";
        if (!new[] { "feed-steal", "depreciate", "none" }.Contains(debuff))
        {
            Console.Error.WriteLine("--debuff must be feed-steal, depreciate or none");
            return null;
        }

        var stopSlaughterDay = -1;
        if (values.TryGetValue("--stop-slaughter", out var stopStr)
            && (!int.TryParse(stopStr, out stopSlaughterDay) || stopSlaughterDay < 0))
        {
            Console.Error.WriteLine("--stop-slaughter must be a non-negative day number");
            return null;
        }

        var stakeShare = 0.05;
        if (values.TryGetValue("--stake", out var stakeStr)
            && (!double.TryParse(stakeStr, System.Globalization.CultureInfo.InvariantCulture, out stakeShare) || stakeShare is <= 0 or > 1))
        {
            Console.Error.WriteLine("--stake must be a number between 0 and 1");
            return null;
        }

        var minChance = 5;
        var maxChance = 95;
        if (values.TryGetValue("--min-chance", out var minStr) && !int.TryParse(minStr, out minChance))
        {
            Console.Error.WriteLine("--min-chance must be an integer");
            return null;
        }

        if (values.TryGetValue("--max-chance", out var maxStr) && !int.TryParse(maxStr, out maxChance))
        {
            Console.Error.WriteLine("--max-chance must be an integer");
            return null;
        }

        var minWeightRatio = 0.0;
        if (values.TryGetValue("--min-weight-ratio", out var ratioStr)
            && (!double.TryParse(ratioStr, System.Globalization.CultureInfo.InvariantCulture, out minWeightRatio) || minWeightRatio is < 0 or > 1))
        {
            Console.Error.WriteLine("--min-weight-ratio must be a number between 0 and 1");
            return null;
        }

        var growthModel = values.TryGetValue("--growth", out var growthStr) ? growthStr : "sqrt";
        if (!new[] { "sqrt", "linear" }.Contains(growthModel))
        {
            Console.Error.WriteLine("--growth must be sqrt or linear");
            return null;
        }

        var growthK = GrowthDefaultK;
        if (values.TryGetValue("--growth-k", out var growthKStr)
            && (!double.TryParse(growthKStr, System.Globalization.CultureInfo.InvariantCulture, out growthK) || growthK is <= 0 or > 1))
        {
            Console.Error.WriteLine("--growth-k must be a number between 0 and 1");
            return null;
        }

        var modFuel = GrowthDefaultFuelPerBonus;
        if (values.TryGetValue("--mod-fuel", out var fuelStr)
            && (!double.TryParse(fuelStr, System.Globalization.CultureInfo.InvariantCulture, out modFuel) || modFuel is < 0 or > 10))
        {
            Console.Error.WriteLine("--mod-fuel must be a number between 0 and 10");
            return null;
        }

        var eliteShare = GrowthDefaultEliteShare;
        if (values.TryGetValue("--elite-share", out var shareStr)
            && (!double.TryParse(shareStr, System.Globalization.CultureInfo.InvariantCulture, out eliteShare) || eliteShare is <= 0 or > 1))
        {
            Console.Error.WriteLine("--elite-share must be a number between 0 and 1");
            return null;
        }

        var spoilerOwner = values.TryGetValue("--spoiler", out var spoilerStr) ? spoilerStr : "";
        var spoilerShare = 0.7;
        if (values.TryGetValue("--spoiler-share", out var spoilerShareStr)
            && (!double.TryParse(spoilerShareStr, System.Globalization.CultureInfo.InvariantCulture, out spoilerShare) || spoilerShare is <= 0 or > 1))
        {
            Console.Error.WriteLine("--spoiler-share must be a number between 0 and 1");
            return null;
        }

        var heirShare = GrowthDefaultHeirShare;
        if (values.TryGetValue("--heir-share", out var heirStr)
            && (!double.TryParse(heirStr, System.Globalization.CultureInfo.InvariantCulture, out heirShare) || heirShare is < 0 or > 1))
        {
            Console.Error.WriteLine("--heir-share must be a number between 0 and 1");
            return null;
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

        return new Settings(days, flags.Contains("--verbose"), flags.Contains("--no-slaughter"), duelChance, flags.Contains("--replay"), consent, duelsFromDay, chanceSource, debuff, !flags.Contains("--no-glory"), stopSlaughterDay, flags.Contains("--killer"), stakeShare, values.TryGetValue("--duelists", out var duelistsStr) ? duelistsStr : "", minChance, maxChance, minWeightRatio, callModel, growthModel, growthK, modFuel, eliteShare, spoilerOwner, spoilerShare, heirShare, dbPath, Path.GetFullPath(achievPath));
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
