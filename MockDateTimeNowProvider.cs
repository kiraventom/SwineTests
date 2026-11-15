using SwineBot;

namespace SwineTests;

public class MockDateTimeNowProvider : IDateTimeNowProvider
{
    // Не default(DateTime): бот считает окна вида "за последние 24 часа"
    // и на 0001-01-01 падает с переполнением
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}
