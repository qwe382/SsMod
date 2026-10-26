using System;
using System.Numerics;
using System.Reflection;
using Content.Client.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using HarmonyLib;
using Robust.Client.Player;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using Robust.Client.UserInterface.Controls;

namespace SsMod.Fnc;

// Авто-решётка: клик по клетке со стержнями в активной руке — мод сам ставит
// призрак решётки (проект) и сразу запускает стройку. Сервер сам отыграет
// задержку и достроит решётку.
//
// Используется штатный путь клиента: ConstructionSystem.TrySpawnGhost +
// TryStartConstruction — у призрака есть дедуп (два на клетку не поставятся)
// и клиентские проверки (дальность, «клетка занята» с попапом).
public static class AutoGrille
{
    internal static bool Enabled { get; private set; }

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Авто-решётка (клик со стержнями)",
            Pressed = Enabled,
            ToolTip = "Клик по клетке со стержнями в руке — сразу строит решётку",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;
        Console.WriteLine($"[SsMod] авто-решётка = {on}");
    }

    public static void TryBuild(EntityUid user, EntityCoordinates coordinates, EntityUid? target)
    {
        if (target is { Valid: true })
            return; // клик по сущности, не по полу

        var ent = IoCManager.Resolve<IEntityManager>();
        var player = IoCManager.Resolve<IPlayerManager>();
        if (player.LocalEntity is not { Valid: true } self || self != user)
            return;

        // Стержни в активной руке?
        if (!ent.System<SharedHandsSystem>().TryGetActiveItem(self, out var item)
            || item is not { } held || !IsRod(ent, held))
            return;

        var proto = IoCManager.Resolve<IPrototypeManager>();
        if (!proto.TryIndex<ConstructionPrototype>("Grille", out var grille))
            return;

        // Клик может попасть мимо центра клетки — снапим к центру (координаты
        // локальны гриду, клетка размером 1).
        var pos = coordinates.Position;
        var snapped = new EntityCoordinates(coordinates.EntityId,
            new Vector2(MathF.Floor(pos.X) + 0.5f, MathF.Floor(pos.Y) + 0.5f));

        var construction = ent.System<Content.Client.Construction.ConstructionSystem>();

        // Призрак уже стоит — клик по клетке дозаказывает стройку.
        foreach (var ghost in ent.EntityQuery<ConstructionGhostComponent>())
        {
            if (ent.GetComponent<TransformComponent>(ghost.Owner).Coordinates != snapped)
                continue;

            StartConstruction(construction, ghost.Owner);
            return;
        }

        if (construction.TrySpawnGhost(grille, snapped, Direction.North, out var spawned))
            StartConstruction(construction, spawned.Value);
    }

    // Стержни = стек MetalRod. Поле называется StackType (новые версии) или
    // StackTypeId (276), поэтому читаем рефлексией.
    private static bool _stackResolved;
    private static MemberInfo? _stackMember;

    private static bool IsRod(IEntityManager ent, EntityUid item)
    {
        if (!ent.TryGetComponent(item, out StackComponent? stack))
            return false;

        if (!_stackResolved)
        {
            _stackResolved = true;
            var type = stack.GetType();
            _stackMember = (MemberInfo?) type.GetField("StackType")
                           ?? type.GetField("StackTypeId");
        }

        return _stackMember switch
        {
            FieldInfo field => field.GetValue(stack)?.ToString() == "MetalRod",
            PropertyInfo property => property.GetValue(stack)?.ToString() == "MetalRod",
            _ => false,
        };
    }

    // TryStartConstruction различается числом параметров (в 290 добавлен
    // optional with) — зовём рефлексией, недостающим optional-параметрам
    // подставляем null.
    private static void StartConstruction(Content.Client.Construction.ConstructionSystem construction,
        EntityUid ghost)
    {
        var method = typeof(Content.Client.Construction.ConstructionSystem)
            .GetMethod("TryStartConstruction", BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
            return;

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = ghost;

        try
        {
            method.Invoke(construction, args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] авто-решётка: {e.InnerException?.Message ?? e.Message}");
        }
    }
}

// Клик со стержнями по полу. Патч по имени — как у InteractQueue: если в чужой
// версии сигнатура ушла, Entry переживает сбой класса, остальной мод работает.
[HarmonyPatch(typeof(SharedInteractionSystem), nameof(SharedInteractionSystem.UserInteraction))]
internal static class AutoGrillePatch
{
    private static void Prefix(EntityUid user, EntityCoordinates coordinates, EntityUid? target,
        bool altInteract)
    {
        if (!AutoGrille.Enabled || altInteract)
            return;

        // Клиент прогоняет один клик несколько раз (реплей неподтверждённого
        // ввода каждый тик) — без проверки заказ стройки уходит повторно и
        // плодит лишние сообщения/делы. Строим только на первом прогоне.
        if (!IoCManager.Resolve<IGameTiming>().IsFirstTimePredicted)
            return;

        try
        {
            AutoGrille.TryBuild(user, coordinates, target);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] авто-решётка: {e.InnerException?.Message ?? e.Message}");
        }
    }
}
