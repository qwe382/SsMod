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
    // Пункты меню красим в цвета категорий ESP: друг — зелёный, враг — красный.
    // Текст пункта рисует Label и разбирает его как разметку
    // (ContextMenuElement.Text -> FormattedMessage.FromMarkupPermissive), поэтому
    // цвет задаём прямо в тексте. Поля для стиля у Verb нет смысла использовать:
    // своих стилей у мода нет.
    //
    // На работу глаголов цвет не влияет: наши глаголы ClientExclusive, и
    // VerbSystem.ExecuteVerb вызывает Act по объекту, а не ищет по тексту.
    private const string FriendText = "[color=#33FF59]Make Friend[/color]";
    private const string EnemyText = "[color=#FF4040]Make Enemy[/color]";

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
            Text = FriendText,
            ClientExclusive = true,
            Priority = 100,
            Act = () => Priorities.Set(target, Priority.Friend),
        });

        __result.Add(new Verb
        {
            Text = EnemyText,
            ClientExclusive = true,
            Priority = 100,
            Act = () => Priorities.Set(target, Priority.Enemy),
        });
    }
}
