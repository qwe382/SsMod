using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace SsMod.Fnc;

// Приоритет цели для мелбота: друг / враг / нейтрально.
public enum Priority
{
    Neutral,
    Friend,
    Enemy,
}

// Система приоритетов: хранит метку для каждого персонажа (по NetEntity).
// Метка ставится через ПКМ-меню (см. PriorityVerbs.cs), читается мелботом.
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
}
