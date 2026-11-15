using SwineBot.Achievements;
using SwineBot.Model;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SwineTests;

public enum TestUserStrategy
{
    RegularNoOverfeed,
    Overfeed12,
    Overfeed8,
    Overfeed6,
    Overfeed4,
    Overfeed12Then6
}

public class TestUser(TestUserStrategy strategy)
{
    public const long GroupTelegramId = -1001337;

    private DateTime? _lastFeedDT;

    public TestUserStrategy Strategy { get; } = strategy;

    /// <summary>
    /// Stable id per strategy: the database is recreated on every run, but the id must
    /// not depend on object identity (GetHashCode) if we want reproducible results.
    /// </summary>
    public static long GetTelegramId(TestUserStrategy strategy) => 1_000 + (int)strategy;

    public long TelegramId => GetTelegramId(Strategy);

    public Update GenerateSlaughter() => GenerateUpdate("/slaughter yes");

    public Update GenerateTop() => GenerateUpdate("/top");

    /// <summary>Any command as this user, e.g. <c>/duel 1001</c> or <c>/info</c>.</summary>
    public Update GenerateCommand(string command) => GenerateUpdate(command);

    public Update GenerateHistory() => GenerateUpdate("/history");

    public Update TryGenerateFeed(UserContext context, AchievementController achievController, DateTime dt)
    {
        bool shouldFeed;
        if (_lastFeedDT is null)
        {
            shouldFeed = true;
        }
        else
        {
            double hoursSinceLastFeed = (dt - _lastFeedDT.Value).TotalHours;

            shouldFeed = Strategy switch
            {
                TestUserStrategy.RegularNoOverfeed => hoursSinceLastFeed > 24, // every > 24 hours
                TestUserStrategy.Overfeed12 => hoursSinceLastFeed is < 14 and > 10, // every ~12 hours
                TestUserStrategy.Overfeed8 => hoursSinceLastFeed is < 10 and > 6, // every ~8 hours
                TestUserStrategy.Overfeed6 => hoursSinceLastFeed is < 8 and > 4, // every ~6 hours
                TestUserStrategy.Overfeed4 => hoursSinceLastFeed is < 6 and > 2, // every ~4 hours
                TestUserStrategy.Overfeed12Then6 when GetAchievementLevel(AchievementType.Overfeed, context, achievController) is { Value: 31 } => hoursSinceLastFeed is < 8 and > 4, // every ~6 hours
                TestUserStrategy.Overfeed12Then6 => hoursSinceLastFeed is < 14 and > 10, // every ~12 hours
                _ => hoursSinceLastFeed > 24, // keeps the switch exhaustive
            };
        }

        if (shouldFeed)
        {
            _lastFeedDT = dt;
            var update = GenerateUpdate("/feed");
            return update;
        }

        return null;
    }

    private AchievementLevel GetAchievementLevel(AchievementType type, UserContext context, AchievementController controller)
    {
        var user = context.Users.First(u => u.TelegramId == TelegramId);
        var swine = context.Swines.First(s => s.OwnerId == user.UserId);
        var info = context.Infos.First(i => i.SwineId == swine.SwineId);
        var achievements = context.Achievements
            .Where(a => a.SwineInfoId == info.InfoId)
            .Where(a => a.Type == type);

        var achiev = achievements.FirstOrDefault();
        if (achiev is null)
            return null;

        return controller.GetLevel(achiev);
    }

    private Update GenerateUpdate(string command)
    {
        var entityLength = command.IndexOf(' ');
        if (entityLength < 0)
            entityLength = command.Length;

        var update = new Update()
        {
            Message = new Message()
            {
                Chat = new Chat()
                {
                    // Как настоящая супергруппа: у неё есть ссылки вида t.me/c/...
                    Id = GroupTelegramId,
                    Type = ChatType.Supergroup,
                    Title = "Test"
                },
                From = new Telegram.Bot.Types.User()
                {
                    Id = TelegramId,
                    FirstName = Strategy.ToString(),
                    Username = Strategy.ToString()
                },
                Entities = [ new MessageEntity() {Offset = 0, Length = entityLength, Type = MessageEntityType.BotCommand }],
                Text = command
            }
        };

        return update;
    }
}
