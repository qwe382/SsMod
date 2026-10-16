using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;

namespace SsMod.Fnc;

// Дальность оружия в руках — общая логика для мелбота и крио-бота.
//
// Движок проверяет дальность сам, но только на лёгкой атаке
// (MeleeWeaponSystem.ClientLightAttack сравнивает расстояние между центрами
// с MeleeWeaponComponent.Range). Тяжёлая (ПКМ) такой проверки не имеет, поэтому
// свой гейт держим здесь и не даём форсировать атаку вне дальности.
internal static class AttackReach
{
    // SharedHandsSystem берём у менеджера систем, а не через [Dependency]:
    // оверлеи — не entity system, через IoC этот тип не отдаётся
    // (UnregisteredDependencyException).
    private static SharedHandsSystem? _hands;
    private static bool _resolved;

    public static bool InReach(IEntityManager ent, EntityUid self, EntityUid target)
    {
        try
        {
            if (!TryGetReach(ent, self, out var reach))
                return true;

            var transform = ent.System<SharedTransformSystem>();
            var distance = (transform.GetWorldPosition(target) - transform.GetWorldPosition(self)).Length();

            return distance <= reach;
        }
        catch (Exception e)
        {
            // Сбой в проверке не должен глушить бота — считаем, что достаём.
            Console.WriteLine($"[SsMod] reach check failed: {e.Message}");
            return true;
        }
    }

    // false — ограничения нет: оружие дальнего боя (у него дальность задаёт
    // снаряд, а не компонент ближнего боя) или в руках что-то непонятное.
    public static bool TryGetReach(IEntityManager ent, EntityUid self, out float reach)
    {
        reach = 0f;

        var hands = Hands(ent);
        if (hands is null)
            return false;

        if (hands.TryGetActiveItem(self, out var held))
        {
            if (ent.HasComponent<GunComponent>(held))
                return false;

            if (ent.TryGetComponent(held, out MeleeWeaponComponent? heldMelee))
            {
                reach = heldMelee.Range;
                return true;
            }
        }

        // Без оружия — кулаки (компонент ближнего боя висит на самом персонаже).
        if (ent.TryGetComponent(self, out MeleeWeaponComponent? selfMelee))
        {
            reach = selfMelee.Range;
            return true;
        }

        return false;
    }

    private static SharedHandsSystem? Hands(IEntityManager ent)
    {
        if (_resolved)
            return _hands;

        _resolved = true;

        try
        {
            _hands = ent.System<SharedHandsSystem>();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] нет SharedHandsSystem ({e.Message})");
        }

        return _hands;
    }
}
