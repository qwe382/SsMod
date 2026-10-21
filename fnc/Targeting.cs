using System.Numerics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.GameObjects;

namespace SsMod.Fnc;

// Выбор персонажей для ботов: мелбот, gun-bot и крио-бот берут цели отсюда.
// Раньше этот код жил прямо в мелботе, из-за чего у каждого бота была своя
// копия и свой набор правил.
//
// Система приоритетов (см. Priorities.cs) применяется здесь: автоцелью может
// стать только помеченный Make Enemy. Make Friend не атакуется никогда,
// нейтральный — только вручную, курсором.
internal static class Targeting
{
    // Снимок живых существ на кадр. AllEntityQueryEnumerator — это
    // Dictionary.GetEnumerator() поверх живого словаря: если в него заходит
    // новый игрок, итерация падает на середине. Поэтому сначала копируем uid'ы,
    // и только потом читаем позиции.
    private static readonly List<EntityUid> Living = new();

    // Сколько живых было в последнем снимке (для диагностики).
    public static int Count => Living.Count;

    // Живые мобы: снимок на кадр. Список общий и перезаполняется при каждом
    // вызове — кадр обслуживает ровно один активный бот, этого достаточно.
    public static List<EntityUid> Alive(IEntityManager ent)
    {
        Living.Clear();

        var query = ent.AllEntityQueryEnumerator<MobStateComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var mobState, out _))
        {
            if (mobState.CurrentState == MobState.Alive)
                Living.Add(uid);
        }

        return Living;
    }

    // Ближайший персонаж к точке мира (курсор) в радиусе radius тайлов.
    // 1 тайл = 1 единица мира, зумы тут ни при чём.
    //
    // Своих не отдаём никогда, даже если курсор стоит ровно на них: Make Friend
    // означает «не атаковать», а не только «не выбирать автоцелью».
    public static EntityUid? UnderPoint(IEntityManager ent, EntityUid self, Vector2 world, float radius)
    {
        var transform = ent.System<SharedTransformSystem>();
        var best = radius * radius;
        EntityUid? found = null;

        foreach (var uid in Alive(ent))
        {
            if (uid == self || !Usable(ent, uid))
                continue;

            if (Priorities.Get(uid) == Priority.Friend)
                continue;

            var dist = (transform.GetWorldPosition(uid) - world).LengthSquared();
            if (dist >= best)
                continue;

            best = dist;
            found = uid;
        }

        return found;
    }

    // Ближайший помеченный враг в радиусе range тайлов и на нашей карте.
    // Здесь и работает система приоритетов: чужие метки сюда не попадают.
    public static EntityUid? NearestEnemy(IEntityManager ent, EntityUid self, float range)
    {
        var transform = ent.System<SharedTransformSystem>();
        var selfWorld = transform.GetWorldPosition(self);
        var selfMap = transform.GetMapId(self);
        var best = range * range;
        EntityUid? found = null;

        foreach (var uid in Alive(ent))
        {
            if (uid == self || !Usable(ent, uid))
                continue;

            if (Priorities.Get(uid) != Priority.Enemy)
                continue;

            if (transform.GetMapId(uid) != selfMap)
                continue;

            var dist = (transform.GetWorldPosition(uid) - selfWorld).LengthSquared();
            if (dist >= best)
                continue;

            best = dist;
            found = uid;
        }

        return found;
    }

    // Персонаж ещё годен в цели: у него есть трансформ и он не удалён.
    public static bool Usable(IEntityManager ent, EntityUid uid)
    {
        if (!ent.HasComponent<TransformComponent>(uid))
            return false;

        return !ent.TryGetComponent(uid, out MetaDataComponent? meta) || !meta.Deleted;
    }
}
