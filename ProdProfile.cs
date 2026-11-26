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
    int PreSlaughterWeight,
    int AgeDays)
{
    public static readonly ProdProfile[] All =
    [
        new(240044466,  "люти феникс",             "Kevish",       7901, 14.3, 1.8,  0,  1,   702, 300),
        new(446879301,  "надпись не хочу умирать", "Rustam",       6589, 19.1, 2.0,  0,  5,  1795, 228),
        new(1496402318, "пеппа",                   "Anastasia",    2809, 24.0, 1.09, 0,  2,   754, 233),
        new(5995101292, "банановый жмых",          "Юлия",         2630, 21.1, 1.08, 0,  1,  3616, 110),
        new(247039030,  "надпись не хочу сво",     "Вячесlove",     948, 40.7, 3.6, 30,  10, 11942, 17),
        new(8121637965, "Мак'C'Вин",               "Vyacheslove",   567, 86.7, 3.75, 0,  1,    45, 255),
        new(182878419,  "Халяльчик",               "Anton",         150, 17.7, 1.0,  0,  0,     0, 165),
    ];

    /// <summary>
    /// Синтетический «настоящий» убийца: кормит раз в 12 часов, забивает раз в 20 дней,
    /// множитель кормления как у Вячесlove (slaughter-вес 11942). Нужен, чтобы проверять
    /// контру на свине, который реально конкурентен бережливым.
    /// </summary>
    public static readonly ProdProfile RealKiller = new(
        999000111, "Кровавый Ганс", "Ганс", 4000, 12.0, 2.0, 20, 10, 11942, 0);

    /// <summary>Свины, у которых вес меньше этого, считаются «малышами».</summary>
    public const int SmallWeightBorder = 1000;

    public static ProdProfile[] WithKiller(bool includeKiller)
        => includeKiller ? [.. All, RealKiller] : All;
}
