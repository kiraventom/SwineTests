using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SwineBot;
using SwineBot.Achievements;
using SwineBot.Achievements.Checkers;
using SwineBot.BotMessages;
using SwineBot.Model;
using Telegram.Bot;
using static System.Environment;
using static SwineTests.TestService;

namespace SwineTests;

public record Settings(int SwinesCount, int TriesCount, int DaysCount);

internal class Program
{
    private const string USAGE = "Usage: SwineTests --swines <swines_count> --tries <tries_count> --days <days_count>";

    private const string PROJECT_NAME = nameof(SwineTests);

    private static async Task Main(string[] args)
    {
        var settings = ParseSettings(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        Log.Information("Building host...");

        var connection = new SqliteConnection("DataSource=:memory:");

        try
        {
            connection.Open();

            var builder = Host.CreateApplicationBuilder();

            builder.Services
                .AddSerilog(ConfigureLogger)
                .AddSingleton<Settings>(settings)
                .AddSingleton<IDateTimeNowProvider, MockDateTimeNowProvider>()
                .AddSingleton<IFeedGeneratorFactory, FeedGeneratorFactory>()
                .AddSingleton<IThrowupCalculatorFactory, ThrowupCalculatorFactory>()
                .AddSingleton<IMessageFactory, MessageFactory>()
                .AddSingleton<IBotMessageSender, MockBotMessageSender>()
                .AddSingleton<IAchievementController, AchievementController>()
                .AddDbContext<UserContext>(o => o.UseSqlite(connection))
                .AddScoped<IUpdateHandler, MockUpdateHandler>()
                .AddSingleton<IAchievementCheckerFactory, AchievementCheckerFactory>()
                .AddTransient<AchievementCheckerBuilder>()
                .AddHostedService<TestService>();

            var host = builder.Build();

            using (var scope = host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<UserContext>();
                context.Database.EnsureCreated();
            }

            Log.Information("Starting host...");

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to run host, terminating");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            connection.Dispose();
        }
    }

    private static void ConfigureLogger(IServiceProvider provider, LoggerConfiguration logger)
    {
        var dataDir = Environment.GetFolderPath(SpecialFolder.LocalApplicationData);
        var logsDirPath = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(logsDirPath);
        var logFilePath = Path.Combine(logsDirPath, $"{PROJECT_NAME}.log");

        logger.MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning);
    }

    private static Settings ParseSettings(string[] args)
    {
        var settings = args.Chunk(2).ToDictionary(p => p[0], p => p[1]);

        if (!settings.TryGetValue("--swines", out var swinesStr) || !int.TryParse(swinesStr, out var swines))
        {
            Console.Error.WriteLine("--swines is not set or is set incorrectly");
            Console.WriteLine(USAGE);
            return null;
        }

        if (!settings.TryGetValue("--tries", out var triesStr) || !int.TryParse(triesStr, out var tries))
        {
            Console.Error.WriteLine("--tries is not set or is set incorrectly");
            Console.WriteLine(USAGE);
            return null;
        }

        if (!settings.TryGetValue("--days", out var daysStr) || !int.TryParse(daysStr, out var days))
        {
            Console.Error.WriteLine("--days is not set or is set incorrectly");
            Console.WriteLine(USAGE);
            return null;
        }

        return new Settings(swines, tries, days);
    }
}
