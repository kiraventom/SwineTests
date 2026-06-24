using SwineBot;

namespace SwineTests;

public class MockDateTimeNowProvider : IDateTimeNowProvider
{
    public DateTime UtcNow { get; set; }
}

