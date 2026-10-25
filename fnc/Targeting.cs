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
            if (Compat.GetMobState(mobState) == MobState.Alive)
                Living.Add(uid);
        }

        return Living;
    }

    // Ближайший персонаж к точке мира (курсор) в радиусе radius тайлов.
    // 1 тайл = 1 единица мира, зумы тут ни при чём.
    //
    // Свои не отдаём никогда, даже если курсор стоит ровно на них: Make Friend
    // означает «не атаковать», а не только «не выбирать автоцелью».
    public static EntityUid? UnderPoint(IEntityManager ent, EntityUid self, Vector2 world, float radius)
    {
        var transform = ent.System<SharedTransformSystem>();
        var best = radius * radius;
        EntityUid? found = null;

        foreach (var uid in Alive(ent))
        {
            if (uid == self || !Usable(ent, uid) || InSameBody(ent, uid, self))
                continue;

            if (PriorityOf(ent, uid) == Priority.Friend)
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
            if (uid == self || !Usable(ent, uid) || InSameBody(ent, uid, self))
                continue;

            if (PriorityOf(ent, uid) != Priority.Enemy)
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

    // Один «организм»: uid — предок self или внутри self. Позитронный мозг —
    // моб с MobStateComponent, лежащий внутри робота: без этой проверки мелбот
    // брал его (или само тело робота) целью и бил сам за себя.
    private static bool InSameBody(IEntityManager ent, EntityUid uid, EntityUid self)
    {
        return IsAncestor(ent, uid, self) || IsAncestor(ent, self, uid);
    }

    // Метка цели: своя, а если цели нет — наследуется от носителя. Чужой
    // позитронный мозг лежит контейнером внутри тела робота, метку игрок
    // ставит по телу — без наследования «Make Friend» на роботе не спасал
    // от прицеливания в его мозг.
    private static Priority PriorityOf(IEntityManager ent, EntityUid uid)
    {
        var priority = Priorities.Get(uid);
        if (priority != Priority.Neutral)
            return priority;

        var current = uid;
        while (ent.TryGetComponent(current, out TransformComponent? transform)
               && transform.ParentUid != EntityUid.Invalid)
        {
            current = transform.ParentUid;
            priority = Priorities.Get(current);
            if (priority != Priority.Neutral)
                return priority;
        }

        return Priority.Neutral;
    }

    // Идём по родителям from, пока не встретим to.
    private static bool IsAncestor(IEntityManager ent, EntityUid from, EntityUid to)
    {
        var current = from;
        while (ent.TryGetComponent(current, out TransformComponent? transform))
        {
            current = transform.ParentUid;
            if (current == to)
                return true;
            if (current == EntityUid.Invalid || current == from)
                return false;
        }

        return false;
    }

    // Персонаж ещё годен в цели: у него есть трансформ и он не удалён.
    public static bool Usable(IEntityManager ent, EntityUid uid)
    {
        if (!ent.HasComponent<TransformComponent>(uid))
            return false;

        return !ent.TryGetComponent(uid, out MetaDataComponent? meta) || !meta.Deleted;
    }
}
