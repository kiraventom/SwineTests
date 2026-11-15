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

public record Settings(int DaysCount, bool Verbose, bool NoSlaughter, string DbPath, string AchievDataFile);

internal class Program
{
    private const string USAGE = "Usage: SwineTests --days <days_count> [--db <path>] [--achiev <path>] [--verbose] [--no-slaughter]";

    private const string DEFAULT_ACHIEV_PATH = "../SwineBot/Achievements/achiev_data.json";

    /// <summary>
    /// Telegram validates the token format when the bot client is constructed, so the fake
    /// token must look real. Nothing is ever sent to Telegram: all senders are mocked.
    /// </summary>
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
                .AddHostedService<TestService>();

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

        var dbPath = values.TryGetValue("--db", out var customDbPath) ? customDbPath : DefaultDbPath();
        var achievPath = values.TryGetValue("--achiev", out var customAchievPath) ? customAchievPath : DEFAULT_ACHIEV_PATH;

        if (!File.Exists(achievPath))
        {
            Console.Error.WriteLine($"Achievements data file \"{achievPath}\" not found");
            return null;
        }

        return new Settings(days, flags.Contains("--verbose"), flags.Contains("--no-slaughter"), dbPath, Path.GetFullPath(achievPath));
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
