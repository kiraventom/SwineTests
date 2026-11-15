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

public record Settings(int DaysCount, bool Verbose, bool NoSlaughter, double DuelChance, bool Replay, double DuelConsent, string DbPath, string AchievDataFile);

internal class Program
{
    private const string USAGE = "Usage: SwineTests --days <days_count> [--db <path>] [--achiev <path>] [--verbose] [--no-slaughter] [--duel-chance <0..1>] [--replay] [--consent <0..1>]";

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

        var consent = 1.0;
        if (values.TryGetValue("--consent", out var consentStr)
            && (!double.TryParse(consentStr, System.Globalization.CultureInfo.InvariantCulture, out consent) || consent is < 0 or > 1))
        {
            Console.Error.WriteLine("--consent must be a number between 0 and 1");
            return null;
        }

        var dbPath = values.TryGetValue("--db", out var customDbPath) ? customDbPath : DefaultDbPath();
        var achievPath = values.TryGetValue("--achiev", out var customAchievPath) ? customAchievPath : DEFAULT_ACHIEV_PATH;

        if (!File.Exists(achievPath))
        {
            Console.Error.WriteLine($"Achievements data file \"{achievPath}\" not found");
            return null;
        }

        return new Settings(days, flags.Contains("--verbose"), flags.Contains("--no-slaughter"), duelChance, flags.Contains("--replay"), consent, dbPath, Path.GetFullPath(achievPath));
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
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning);
    }
}
