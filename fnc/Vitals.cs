using Content.Shared.Damage.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.GameObjects;

namespace SsMod.Fnc;

// Здоровье и стамина персонажа — общее для ESP (полоски) и фрейма «Персонажи»
// (таблица и дашборд). Раньше это жило внутри ESP.
internal static class Vitals
{
    // HP: сколько урона накопилось. Максимум берём из MobThresholds — на клиенте
    // это единственный источник максимума (см. EspOverlay.TryGetBar).
    public static bool Health(IEntityManager ent, EntityUid uid, out float current, out float max)
    {
        current = 0f;
        max = 0f;

        // Название компонента урона различается между сборками, поэтому через Compat.
        if (Compat.GetTotalDamage(ent, uid) is not { } totalDamage)
            return false;

        if (!ent.TryGetComponent(uid, out MobThresholdsComponent? thresholds) || thresholds.Thresholds.Count == 0)
            return false;

        foreach (var key in thresholds.Thresholds.Keys)
            max = MathF.Max(max, (float) key);

        if (max <= 0f)
            return false;

        current = MathF.Max(max - totalDamage, 0f);
        return true;
    }

    public static bool Stamina(IEntityManager ent, EntityUid uid, out float current, out float max)
    {
        current = 0f;
        max = 0f;

        if (!ent.TryGetComponent(uid, out StaminaComponent? stamina))
            return false;

        if (stamina.CritThreshold <= 0f)
            return false;

        max = stamina.CritThreshold;
        current = MathF.Max(max - stamina.StaminaDamage, 0f);
        return true;
    }

    // Состояние моба одним словом.
    public static string StateText(MobState? state) => state switch
    {
        MobState.Alive => "жив",
        MobState.Critical => "крит",
        MobState.Dead => "мёртв",
        _ => "?",
    };
}
