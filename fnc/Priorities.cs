using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace SsMod.Fnc;

// Приоритет цели для мелбота: друг / враг / нейтрально.
public enum Priority
{
    Neutral,
    Friend,
    Enemy,
}

// Система приоритетов: хранит метку для каждого персонажа (по NetEntity).
// Метка ставится через ПКМ-меню (см. PriorityVerbs.cs), читается ботами через
// выбор цели (fnc/Targeting.cs) и красит ESP (fnc/Esp.cs).
public static class Priorities
{
    private static readonly Dictionary<NetEntity, Priority> Labels = new();

    public static Priority Get(NetEntity target)
    {
        return Labels.TryGetValue(target, out var value) ? value : Priority.Neutral;
    }

    public static Priority Get(EntityUid target)
    {
        return Get(IoCManager.Resolve<IEntityManager>().GetNetEntity(target));
    }

    public static void Set(NetEntity target, Priority priority)
    {
        if (priority == Priority.Neutral)
            Labels.Remove(target);
        else
            Labels[target] = priority;

        Console.WriteLine($"[SsMod] приоритет {target}: {priority}");
    }

    public static bool IsFriend(EntityUid target) => Get(target) == Priority.Friend;

    public static bool IsEnemy(EntityUid target) => Get(target) == Priority.Enemy;

    // Цвет метки. Им же красится имя в фрейме «Персонажи» и квадраты-кнопки
    // «друг / враг / нейтрал» — чтобы цвет в таблице и в кнопке совпадал.
    public static Color ColorOf(Priority priority) => priority switch
    {
        Priority.Friend => new Color(0.2f, 1f, 0.35f),
        Priority.Enemy => new Color(1f, 0.25f, 0.25f),
        _ => new Color(1f, 0.9f, 0.3f),
    };

    // Сколько меток стоит сейчас (для окна мода).
    public static int Count => Labels.Count;

    // Снять все метки сразу (кнопка Del prior во вкладке Gun-bot). Возвращаем,
    // сколько сняли, — чтобы было что показать в окне.
    public static int Clear()
    {
        var count = Labels.Count;
        Labels.Clear();
        Console.WriteLine($"[SsMod] приоритеты сброшены: снято меток {count}");
        return count;
    }
}
