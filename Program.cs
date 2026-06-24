using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SwineBot;
using SwineBot.Model;
using static System.Environment;

namespace SwineTests;

public record Settings(int DaysCount);

internal class Program
{
    private const string USAGE = "Usage: SwineTests --days <days_count>";

    private const string PROJECT_NAME = nameof(SwineTests);

    private static async Task Main(string[] args)
    {
        var settings = ParseSettings(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        Log.Information("Building host...");

        var dbPath = "~/.config/SwineTests/users.db";
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var config = new Config("TOKEN", "@USERNAME", $"DataSource={dbPath};", "../SwineBot/Achievements/achiev_data.json");

        try
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services
                .AddSerilog(ConfigureLogger)
                .AddSingleton<Settings>(settings);

            SwineBot.Program.RegisterServices(builder.Services)
                .RemoveAll<Config>().AddSingleton<Config>(config)
                .RemoveAll<IDateTimeNowProvider>().AddSingleton<IDateTimeNowProvider, MockDateTimeNowProvider>()
                .RemoveAll<IBotMessageSender>().AddSingleton<IBotMessageSender, MockBotMessageSender>()
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

        if (!settings.TryGetValue("--days", out var daysStr) || !int.TryParse(daysStr, out var days))
        {
            Console.Error.WriteLine("--days is not set or is set incorrectly");
            Console.WriteLine(USAGE);
            return null;
        }

        return new Settings(days);
    }
}
