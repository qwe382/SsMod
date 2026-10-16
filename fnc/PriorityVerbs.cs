using System.Collections.Generic;
using Content.Client.Verbs;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using HarmonyLib;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace SsMod.Fnc;

// ПКМ по персонажу -> пункты "Make Friend" / "Make Enemy".
// Меню строится из глаголов (verbs), которые отдаёт VerbSystem.GetVerbs;
// добавляем свои клиентские глаголы в готовый список.
[HarmonyPatch(typeof(VerbSystem), nameof(VerbSystem.GetVerbs))]
internal static class PriorityVerbsPatch
{
    private static void Postfix(NetEntity target, EntityUid user, ref SortedSet<Verb> __result)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();

        if (!entMan.TryGetEntity(target, out var uid))
            return;

        // Только по персонажам (мобам) и не по себе.
        if (uid.Value == user || !entMan.HasComponent<MobStateComponent>(uid.Value))
            return;

        __result.Add(new Verb
        {
            Text = "Make Friend",
            ClientExclusive = true,
            Priority = 100,
            Act = () => Priorities.Set(target, Priority.Friend),
        });

        __result.Add(new Verb
        {
            Text = "Make Enemy",
            ClientExclusive = true,
            Priority = 100,
            Act = () => Priorities.Set(target, Priority.Enemy),
        });
    }
}
