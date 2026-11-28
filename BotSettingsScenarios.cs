using Microsoft.Extensions.DependencyInjection;
using SwineBot;

namespace SwineTests;

/// <summary>
/// Проверки настроек бота на стороне Telegram, без которых не работает часть сценариев.
/// Сейчас это inline-режим: кнопка «Выбрать оппонента» в /duel — switch_inline_query_current_chat,
/// без inline-режима она не появляется. Раньше это был ручной шаг в BotFather.
///
/// Сети нет: отправку подменяет MockBotSettingsSender. База не нужна, шаг идёт до /pet и дуэлей.
/// </summary>
public class BotSettingsScenarios(IServiceScopeFactory spf)
{
    private readonly List<string> _failures = [];

    public async Task<IReadOnlyList<string>> Run(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("=== Bot settings scenarios ===");

        try
        {
            await StepInlineMode(token);
            await StepTelegramFailure(token);
        }
        finally
        {
            using var scope = spf.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<MockBotSettingsSender>();

            sender.Fail = null;
            sender.EnabledInlinePlaceholders.Clear();
        }

        return _failures;
    }

    private async Task StepInlineMode(CancellationToken token)
    {
        Console.WriteLine("-- inline-режим при старте");

        using var scope = spf.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<BotSettings>();
        var sender = scope.ServiceProvider.GetRequiredService<MockBotSettingsSender>();

        var enabled = await settings.EnableInlineMode(token);

        Check("inline-режим включён при старте", enabled, $"enabled={enabled}");
        Check("плейсхолдер — тот же текст, что у кнопки выбора оппонента",
            sender.EnabledInlinePlaceholders.SequenceEqual([BotSettings.INLINE_QUERY_PLACEHOLDER]),
            string.Join(", ", sender.EnabledInlinePlaceholders));
        Check("плейсхолдер непустой", BotSettings.INLINE_QUERY_PLACEHOLDER is { Length: > 0 and <= 64 },
            BotSettings.INLINE_QUERY_PLACEHOLDER);
    }

    private async Task StepTelegramFailure(CancellationToken token)
    {
        Console.WriteLine("-- Telegram недоступен");

        using var scope = spf.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<BotSettings>();
        var sender = scope.ServiceProvider.GetRequiredService<MockBotSettingsSender>();

        sender.Fail = new HttpRequestException("simulated: api.telegram.org is unreachable");

        var enabled = await settings.EnableInlineMode(token);

        // /duel умеет и по tg id, поэтому сбой — предупреждение, а не остановка бота
        Check("падение включения inline-режима не считается фатальным", !enabled, $"enabled={enabled}");
    }

    private void Check(string name, bool ok, string details = null)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(details is null ? "" : $"  <- {details}")}");
        if (!ok)
            _failures.Add(name);
    }
}
