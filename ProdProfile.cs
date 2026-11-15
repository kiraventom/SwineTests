namespace SwineTests;

/// <summary>
/// Профиль свина из прод-базы (группа "сво любители", GroupId=1), снятый по последним
/// 90 дням: как часто владелец заходит кормить, сколько кормёжек делает за сессию
/// и забивает ли свина с регулярностью.
/// </summary>
public record ProdProfile(
    long TelegramId,
    string Name,
    string Owner,
    int StartWeight,
    double GapHours,
    double FeedsPerSession,
    int SlaughterEveryDays,
    int PreSlaughters,
    int PreSlaughterWeight)
{
    public static readonly ProdProfile[] All =
    [
        new(240044466,  "люти феникс",             "Kevish",       7901, 14.3, 1.8,  0,  1,   702),
        new(446879301,  "надпись не хочу умирать", "Rustam",       6589, 19.1, 2.0,  0,  5,  1795),
        new(1496402318, "пеппа",                   "Anastasia",    2809, 24.0, 1.09, 0,  2,   754),
        new(5995101292, "банановый жмых",          "Юлия",         2630, 21.1, 1.08, 0,  1,  3616),
        new(247039030,  "надпись не хочу сво",     "Вячесlove",     948, 40.7, 3.6, 30,  10, 11942),
        new(8121637965, "Мак'C'Вин",               "Vyacheslove",   567, 86.7, 3.75, 0,  1,    45),
        new(182878419,  "Халяльчик",               "Anton",         150, 17.7, 1.0,  0,  0,     0),
    ];

    /// <summary>Свины, у которых вес меньше этого, считаются «малышами».</summary>
    public const int SmallWeightBorder = 1000;
}
