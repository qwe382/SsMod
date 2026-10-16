using Content.Client.Gameplay;
using Content.Client.Interaction;
using Content.Client.Disposal.Unit;
using Content.Shared.Disposal.Components;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Power.Components;
using HarmonyLib;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input.Binding;
using Robust.Shared.IoC;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

public static class QuickFlush
{
    private static bool _enabled;

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Быстрый смыв",
            Pressed = _enabled,
            ToolTip = "Автосмыв мусорки при перетаскивании живого существа",
        };
    }

    public static void Toggle(bool on)
    {
        _enabled = on;
    }

    internal static void TryFlush(EntityUid dragged, EntityUid target)
    {
        if (!_enabled || !IoCManager.Resolve<IEntityManager>().EntityExists(dragged))
            return;

        var ent = IoCManager.Resolve<IEntityManager>();
        if (!ent.TryGetComponent(dragged, out MobStateComponent? mobState) ||
            mobState.CurrentState != MobState.Alive ||
            !ent.HasComponent<DisposalUnitComponent>(target))
        {
            return;
        }

        QuickFlushOverlay.Start(dragged, target);
    }
}

[HarmonyPatch(typeof(DragDropSystem), "OnUseMouseUp")]
internal static class QuickFlushDragPatch
{
    private readonly record struct DropState(EntityUid Dragged, EntityUid? Disposal);

    private static void Prefix(
        DragDropSystem __instance,
        in PointerInputCmdHandler.PointerInputCmdArgs args,
        out DropState __state)
    {
        var draggedField = AccessTools.Field(typeof(DragDropSystem), "_draggedEntity");
        var draggedValue = draggedField?.GetValue(__instance);
        var dragged = draggedValue is EntityUid uid ? uid : default;
        EntityUid? disposal = null;

        var player = IoCManager.Resolve<IPlayerManager>().LocalEntity;
        if (draggedValue is EntityUid draggedUid && player is { Valid: true } user &&
            IoCManager.Resolve<IStateManager>().CurrentState is GameplayState screen)
        {
            var ent = IoCManager.Resolve<IEntityManager>();
            var validDragDrop = AccessTools.Method(typeof(DragDropSystem), "ValidDragDrop");
            var interaction = ent.System<SharedInteractionSystem>();
            foreach (var candidate in screen.GetClickableEntities(args.Coordinates))
            {
                if (candidate == draggedUid || validDragDrop == null)
                    continue;

                var valid = validDragDrop.Invoke(__instance, [user, draggedUid, candidate]);
                if (valid is not true ||
                    !interaction.InRangeUnobstructed(user, candidate) ||
                    !interaction.InRangeUnobstructed(user, draggedUid))
                    continue;

                if (ent.HasComponent<DisposalUnitComponent>(candidate))
                    disposal = candidate;

                break;
            }
        }

        __state = new DropState(dragged, disposal);
    }

    private static void Postfix(bool __result, DropState __state)
    {
        if (__result && __state.Disposal is { } disposal)
            QuickFlush.TryFlush(__state.Dragged, disposal);
    }
}

internal sealed class QuickFlushOverlay : Overlay
{
    private sealed class PendingFlush(EntityUid dragged, EntityUid unit)
    {
        public EntityUid Dragged { get; } = dragged;
        public EntityUid Unit { get; } = unit;
        public TimeSpan OpenAt { get; set; }
        public TimeSpan ActionAt { get; set; }
        public TimeSpan RetryAt { get; set; }
        public TimeSpan TimeoutAt { get; set; }
        public TimeSpan RestoreAt { get; set; }
        public bool ButtonsPressed { get; set; }
        public bool RestorePower { get; set; }
    }

    private static QuickFlushOverlay? _instance;

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private readonly List<PendingFlush> _pending = new();
    private readonly UserInterfaceSystem _ui;

    private QuickFlushOverlay()
    {
        IoCManager.InjectDependencies(this);
        _ui = _ent.System<UserInterfaceSystem>();
    }

    public static void Start(EntityUid dragged, EntityUid unit)
    {
        var overlays = IoCManager.Resolve<IOverlayManager>();
        _instance ??= new QuickFlushOverlay();

        if (_instance._pending.Any(p => p.Unit == unit))
            return;

        var now = _instance._timing.RealTime;
        var isSelf = _instance._player.LocalEntity == dragged;
        var selfDelay = isSelf ? _instance.GetInsertDelay(dragged, unit) : TimeSpan.Zero;
        var pending = new PendingFlush(dragged, unit)
        {
            OpenAt = now + selfDelay,
            // Короткая пауза, чтобы окно успело создать BUI.
            ActionAt = now + selfDelay + TimeSpan.FromSeconds(0.1),
            TimeoutAt = now + selfDelay + TimeSpan.FromSeconds(8),
        };
        _instance._pending.Add(pending);

        if (!overlays.HasOverlay<QuickFlushOverlay>())
            overlays.AddOverlay(_instance);

        // Для чужого персонажа окно открывается сразу; для своего — после EntryDelay.
        if (!isSelf && _instance._player.LocalEntity != null &&
            _instance._ent.TryGetComponent(unit, out UserInterfaceComponent? ui))
        {
            _instance._ui.OpenUi((unit, ui), DisposalUnitComponent.DisposalUnitUiKey.Key,
                predicted: true);
        }
    }

    private TimeSpan GetInsertDelay(EntityUid dragged, EntityUid unit)
    {
        if (!_ent.TryGetComponent(unit, out DisposalUnitComponent? disposal))
            return TimeSpan.Zero;

        var self = _player.LocalEntity == dragged;
        var seconds = self ? disposal.EntryDelay : disposal.DraggedEntryDelay;
        return TimeSpan.FromSeconds(Math.Max(0f, seconds) + 0.05f);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        var now = _timing.RealTime;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];
            if (!_ent.EntityExists(pending.Unit) || !_ent.EntityExists(pending.Dragged))
            {
                _pending.RemoveAt(i);
                continue;
            }

            if (!pending.ButtonsPressed)
            {
                if (now < pending.OpenAt)
                    continue;

                if (!_ui.TryGetOpenUi<DisposalUnitBoundUserInterface>(pending.Unit,
                        DisposalUnitComponent.DisposalUnitUiKey.Key, out var bui))
                {
                    // При самосбросе интерфейс открывается только после EntryDelay.
                    if (now >= pending.RetryAt &&
                        _ent.TryGetComponent(pending.Unit, out UserInterfaceComponent? ui) &&
                        _player.LocalEntity is { } actor)
                    {
                        _ui.OpenUi((pending.Unit, ui), DisposalUnitComponent.DisposalUnitUiKey.Key,
                            actor, predicted: true);
                        pending.RetryAt = now + TimeSpan.FromSeconds(0.1);
                    }

                    if (now >= pending.TimeoutAt)
                        _pending.RemoveAt(i);
                    continue;
                }

                if (now < pending.ActionAt)
                    continue;

                if (!_ent.TryGetComponent(pending.Unit, out DisposalUnitComponent? disposal))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                pending.ButtonsPressed = true;
                var window = GetWindow(bui);
                pending.RestorePower = window?.Power.Pressed == true;

                // Смотрим состояние именно отображённой кнопки и вызываем BUI-
                // обработчик клика, как делает подписка реального UI.
                if (window?.Engage.Pressed != true)
                    PressButton(bui, DisposalUnitComponent.UiButton.Engage);

                // Питание выключаем только если оно включено, затем возвращаем через 0,1 с.
                if (pending.RestorePower)
                {
                    PressButton(bui, DisposalUnitComponent.UiButton.Power);
                    pending.RestoreAt = now + TimeSpan.FromSeconds(0.05);
                }

                continue;
            }

            if (pending.RestorePower && now >= pending.RestoreAt &&
                _ui.TryGetOpenUi<DisposalUnitBoundUserInterface>(pending.Unit,
                    DisposalUnitComponent.DisposalUnitUiKey.Key, out var restoreBui))
            {
                pending.RestorePower = false;
                PressButton(restoreBui, DisposalUnitComponent.UiButton.Power);
            }

            if (!pending.RestorePower)
                _pending.RemoveAt(i);
        }

        if (_pending.Count == 0)
        {
            IoCManager.Resolve<IOverlayManager>().RemoveOverlay<QuickFlushOverlay>();
            _instance = null;
        }
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
    }

    private static DisposalUnitWindow? GetWindow(DisposalUnitBoundUserInterface bui)
    {
        return AccessTools.Field(typeof(DisposalUnitBoundUserInterface), "_disposalUnitWindow")
            ?.GetValue(bui) as DisposalUnitWindow;
    }

    private static void PressButton(DisposalUnitBoundUserInterface bui, DisposalUnitComponent.UiButton button)
    {
        AccessTools.Method(typeof(DisposalUnitBoundUserInterface), "ButtonPressed")!
            .Invoke(bui, [button]);
    }

}
