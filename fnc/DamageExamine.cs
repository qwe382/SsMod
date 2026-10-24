using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using Content.Shared.Mobs.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace SsMod.Fnc;

// Твик: при осмотре (Shift) персонажа показываем разбивку урона по типам.
//
// Разбивка берётся из DamageableComponent — она сетевая, клиенту приходит
// словарь «тип урона -> сколько», ничего у сервера спрашивать не нужно.
// Показываем только то, что больше нуля, и только у персонажей и мобов
// (у стен и контейнеров разбивка здоровья ни к чему).
//
// Дописывается в тот же хук осмотра, что и состав растворов (ReagentExamine).
public static class DamageExamine
{
    // Одно и то же сообщение может прийти в отрисовку не один раз —
    // помечаем обработанные, чтобы не задвоить строки.
    private static readonly ConditionalWeakTable<FormattedMessage, object> Seen = new();

    private static bool _loggedHook;

    // Почему осмотр не дописал урон — пишем в лог первые несколько раз,
    // чтобы было видно причину («не показывает» без объяснений).
    private static int _diagLogged;

    internal static void Append(FormattedMessage message, EntityUid target)
    {
        if (!_loggedHook)
        {
            _loggedHook = true;
            Console.WriteLine("[SsMod] examine damage: осмотр перехватывается");
        }

        if (Seen.TryGetValue(message, out _))
            return;

        Seen.Add(message, message);

        var ent = IoCManager.Resolve<IEntityManager>();

        // Только персонажи и мобы.
        if (!ent.HasComponent<MobStateComponent>(target))
        {
            Diag($"цель {target} — не персонаж (нет MobStateComponent)");
            return;
        }

        var dict = Compat.GetDamageDict(ent, target);
        if (dict == null)
        {
            Diag($"цель {target} — DamageableComponent не найден");
            return;
        }

        var total = 0f;
        var entries = new List<(string Name, string Color, float Value)>(dict.Count);

        foreach (var (id, value) in dict)
        {
            if (value <= 0.01f)
                continue;

            total += value;
            entries.Add((Name(id), Color(id), value));
        }

        // Персонаж цел: так и пишем, чтобы осмотр не выглядел сломанным.
        if (entries.Count == 0)
        {
            Diag($"цель {target} — урона нет (типов в словаре {dict.Count})");
            message.AddMarkupOrThrow("\n[color=gray]Урона нет[/color]");
            return;
        }

        // Больший урон — выше.
        entries.Sort((a, b) => b.Value.CompareTo(a.Value));

        var sb = new StringBuilder();
        sb.Append("\n[color=white]Урон[/color] [color=gray]")
            .Append(Num(total))
            .Append("[/color]");

        foreach (var entry in entries)
        {
            sb.Append("\n[color=")
                .Append(entry.Color)
                .Append(']')
                .Append(FormattedMessage.EscapeText(entry.Name))
                .Append(": ")
                .Append(Num(entry.Value))
                .Append("[/color]");
        }

        message.AddMarkupOrThrow(sb.ToString());
    }

    private static void Diag(string text)
    {
        if (_diagLogged++ >= 8)
            return;

        Console.WriteLine($"[SsMod] examine damage: {text}");
    }

    private static string Num(float value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    // Названия типов урона по-русски; что не знаем — как есть в прототипе.
    private static string Name(string id) => id switch
    {
        "Blunt" => "Удар",
        "Slash" => "Порез",
        "Piercing" => "Прокол",
        "Heat" => "Жар",
        "Shock" => "Электро",
        "Cold" => "Холод",
        "Caustic" => "Кислота",
        "Poison" => "Яд",
        "Cellular" => "Клетки",
        "Radiation" => "Радиация",
        "Asphyxiation" => "Удушье",
        "Airloss" => "Удушье",
        "Bloodloss" => "Кровопотеря",
        "Structural" => "Разрушение",
        _ => id,
    };

    // Цвета по группам урона: физический — красный, ожоговый — оранжевый,
    // токсины — зелёный, воздух/кровь — голубой.
    private static string Color(string id) => id switch
    {
        "Blunt" or "Slash" or "Piercing" => "#FF5C5C",
        "Heat" or "Shock" or "Cold" or "Caustic" => "#FFA55C",
        "Poison" or "Cellular" or "Radiation" => "#7CE87C",
        "Asphyxiation" or "Airloss" or "Bloodloss" => "#6BD6FF",
        _ => "#BBBBBB",
    };
}
