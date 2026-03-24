using Telegram.Bot.Types;

namespace SwineTests;

public enum TestUserStrategy { RegularOverfeed, IrregularOverfeed, Random, RegularNoOverfeed, IrregularNoOverfeed }

public record FeedRecord(DateTime DT, int Amount);

public class TestUser(TestUserStrategy strategy)
{
    public TestUserStrategy Strategy { get; } = strategy;

    public List<FeedRecord> FeedLog { get; } = [];

    public int Weight => 1 + FeedLog.Sum(fl => fl.Amount);

    public Update TryGenerateUpdate(DateTime dt)
    {
        bool shouldFeed;
        if (FeedLog.Count == 0)
        {
            shouldFeed = true;
        }
        else
        {
            double hoursSinceLastFeed = (dt - FeedLog.Last().DT).TotalHours;
            var overfeedMaxChance = Math.Max(1, 24 - (int)Math.Round(hoursSinceLastFeed));
            var noOverfeedMaxChance = Math.Max(1, 48 - (int)Math.Round(hoursSinceLastFeed));

            shouldFeed = Strategy switch
            {
                TestUserStrategy.RegularNoOverfeed => hoursSinceLastFeed > 24, // every > 24 hours
                TestUserStrategy.RegularOverfeed => hoursSinceLastFeed is < 14 and > 10, // every ~12 hours
                TestUserStrategy.IrregularNoOverfeed => hoursSinceLastFeed > 24 ? Random.Shared.Next(0, noOverfeedMaxChance) == 0 : false, // every > 24 hours with increasing chance
                TestUserStrategy.IrregularOverfeed => hoursSinceLastFeed < 24 ? Random.Shared.Next(0, overfeedMaxChance) == 0 : false, // every < 24 hours with increasing chance
                TestUserStrategy.Random => Random.Shared.Next(0, noOverfeedMaxChance) == 0 // increasing chance
            };
        }

        if (shouldFeed)
        {
            var update = GenerateUpdate("/feed");
            return update;
        }

        return null;
    }

    private Update GenerateUpdate(string command)
    {
        var update = new Update()
        {
            Message = new Message()
            {
                Chat = new Chat()
                {
                    Id = 1337,
                    Title = "Test"
                },
                From = new User()
                {
                    Id = GetHashCode(),
                    FirstName = "John " + GetHashCode(),
                    Username = "@" + GetHashCode()
                },
                Text = command
            }
        };

        return update;
    }
}

