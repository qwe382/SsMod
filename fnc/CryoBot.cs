using System.Numerics;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.CombatMode;
using Content.Shared.Containers;
using Content.Shared.DragDrop;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Крио-бот: бой с заходами в капсулу криогенного сна.
//
// Цикл: бьём помеченного «Make Enemy» врага -> забираемся в капсулу -> сидим
// в ней PodTime секунд -> вылезаем движением -> снова бьём.
//
// Всё делается штатными путями клиента:
//
//  * атака — форсируем состояние кнопки Use в PlayerCommandStates (см.
//    CryoBotAttackKeyPatch). Все боевые системы берут «зажата ли ЛКМ» именно
//    оттуда (MeleeWeaponSystem.Update, GunSystem.Update), поэтому клик сам
//    уходит на сервер как обычная атака. Цель подставляем в мелботовские хуки
//    (подмена курсора + сброс Attacking) через MelBotOverlay.SetTarget.
//
//  * заход в капсулу — DragDropRequestEvent(self -> капсула): ровно то же, что
//    игрок делает, перетаскивая себя мышкой. Капсула (DragInsertContainer) при
//    self-дропе вставляет мгновенно (DelaySelfEntry=false).
//
//  * выход — движение: ExitContainerOnMoveSystem выбрасывает из контейнера по
//    событию ContainerRelayMovementEntityEvent, а его поднимает обработчик
//    нажатия кнопки движения. Поэтому шлём в InputSystem.HandleInputCommand
//    команду MoveUp Down/Up — она уходит и на сервер, как обычное движение.
//
// Бот работает только в боевом режиме: ближний бой без него движок не пускает
// (MeleeWeaponSystem.Update проверяет CombatMode.IsInCombatMode).
public sealed class CryoBotOverlay : Overlay
{
    // Активен ли оверлей. Пока он активен, цель атаки публикует только он —
    // мелбот уступает (MelBotOverlay.YieldToCryoBot).
    internal static bool Active { get; set; }

    // Держим ЛКМ «зажатой» (боевая фаза и цель в пределах дальности).
    internal static bool HoldAttack { get; set; }

    // Тайминги цикла, в секундах. Правятся в окне мода и в конфиге.
    public static float PodTime = 1.5f;
    public static float CombatTime = 1f;

    // Докуда искать врага и капсулу, в тайлах.
    private const float TargetRange = 12f;
    private const float PodRange = 8f;

    // Сколько ждать фактического захода/выхода, прежде чем считать попытку неудачной.
    private const float PodActionTimeout = 1.5f;

    // После стольких неудачных заходов перестаём мучить капсулу и просто бьём.
    private const int MaxPodFailures = 3;

    private enum Phase
    {
        Idle,
        Attack,
        Enter,
        Inside,
        Exit,
    }

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IInputManager _input = default!;

    private readonly SharedTransformSystem _transform;

    // Снимки на кадр: AllEntityQueryEnumerator — это энумератор живого словаря,
    // вход нового игрока ломает итерацию, поэтому сначала копируем uid'ы.
    private readonly List<EntityUid> _mobs = new();
    private readonly List<EntityUid> _pods = new();

    private Robust.Client.GameObjects.InputSystem? _inputSystem;

    private Phase _phase = Phase.Idle;
    private double _phaseStart;
    private EntityUid? _pod;
    private bool _enterSent;
    private BoundKeyState _moveState = BoundKeyState.Up;
    private int _podFailures;
    private double _lastHint;

    public CryoBotOverlay()
    {
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
        Active = true;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            Step(args);
        }
        catch (Exception e)
        {
            // Оверлей рисуется каждый кадр: исключение завалило бы рендер.
            Console.Error.WriteLine($"[SsMod] cryobot: {e.Message}");
            Reset();
        }
    }

    private void Step(in OverlayDrawArgs args)
    {
        var now = _timing.RealTime.TotalSeconds;

        if (_player.LocalEntity is not { Valid: true } self)
        {
            Reset();
            return;
        }

        var selfWorld = _transform.GetWorldPosition(self);
        var map = _transform.GetMapId(self);

        var target = FindEnemy(self, map, selfWorld);

        // Капсулу ищем только «в бою»: во время цикла она уже зафиксирована.
        if (_phase is Phase.Idle or Phase.Attack)
            _pod = FindPod(self, map, selfWorld);

        if (target is null)
        {
            Hint(now, "нет цели с меткой Make Enemy рядом");
            Reset();
            return;
        }

        switch (_phase)
        {
            case Phase.Idle:
                BeginAttack(now);
                break;

            case Phase.Attack:
            {
                var targetUid = target.Value;

                // Боевой режим обязателен и для ближнего боя, и для стрельбы:
                // MeleeWeaponSystem.Update и GunSystem.Update проверяют его сами.
                if (!_ent.TryGetComponent(self, out CombatModeComponent? combat) || !combat.IsInCombatMode)
                {
                    HoldAttack = false;
                    ClearTarget();
                    Hint(now, "не в боевом режиме — атака не сработает");
                    break;
                }

                var inReach = AttackReach.InReach(_ent, self, targetUid);

                // ЛКМ форсируем только когда реально достаём: иначе персонаж
                // махал бы по тому, куда случайно смотрит настоящий курсор.
                HoldAttack = inReach;

                if (inReach)
                    Publish(args, self, targetUid);

                if (now - _phaseStart < CombatTime)
                    break;

                HoldAttack = false;
                ClearTarget();

                // Враг вне дальности оружия — бить нечем, в капсулу тем более
                // незачем: продолжаем бой.
                if (!inReach)
                {
                    _phaseStart = now;
                    break;
                }

                _pod = FindPod(self, map, selfWorld);
                if (_pod is not { } || _podFailures >= MaxPodFailures)
                {
                    // Капсулы рядом нет (или она не пускает) — просто бьём дальше.
                    _phaseStart = now;
                    break;
                }

                _enterSent = false;
                Console.WriteLine("[SsMod] cryobot: бьём -> в капсулу");
                SetPhase(Phase.Enter, now);
                break;
            }

            case Phase.Enter:
            {
                HoldAttack = false;
                ClearTarget();

                if (now - _phaseStart > PodActionTimeout)
                {
                    // Не пустило: возвращаемся в бой, после нескольких неудач
                    // вообще перестаём трогать капсулу.
                    _podFailures++;
                    if (_podFailures >= MaxPodFailures)
                        Console.WriteLine("[SsMod] cryobot: капсула не пускает, цикл отключён");
                    else
                        Console.WriteLine($"[SsMod] cryobot: заход не удался (попытка {_podFailures})");

                    SetPhase(Phase.Attack, now);
                    break;
                }

                if (_pod is not { } pod)
                {
                    SetPhase(Phase.Inside, now);
                    break;
                }

                if (IsInside(self, pod))
                {
                    SetPhase(Phase.Inside, now);
                    break;
                }

                if (!_enterSent)
                {
                    SendEnter(self, pod);
                    _enterSent = true;
                }

                break;
            }

            case Phase.Inside:
            {
                HoldAttack = false;
                ClearTarget();

                if (now - _phaseStart >= PodTime)
                {
                    _moveState = BoundKeyState.Up;
                    SetPhase(Phase.Exit, now);
                }

                break;
            }

            case Phase.Exit:
            {
                HoldAttack = false;
                ClearTarget();

                if (_pod is not { } pod || !IsInside(self, pod))
                {
                    // Вылезли движением — можно бить снова.
                    StopMove(self);
                    _podFailures = 0;
                    Console.WriteLine("[SsMod] cryobot: вылез, бьём");
                    SetPhase(Phase.Attack, now);
                    break;
                }

                if (now - _phaseStart > PodActionTimeout)
                {
                    // Застряли — не бьёмся в стену бесконечно.
                    StopMove(self);
                    Console.WriteLine("[SsMod] cryobot: не вылез из капсулы");
                    SetPhase(Phase.Attack, now);
                    break;
                }

                // Движение внутри контейнера выбрасывает из него. Чередуем
                // Down/Up: повтор одного и того же состояния InputSystem глотает
                // как дубликат, поэтому нужна именно смена.
                SendMove(self, _moveState == BoundKeyState.Down ? BoundKeyState.Up : BoundKeyState.Down);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- фазы

    private void BeginAttack(double now)
    {
        _phaseStart = now;
        _phase = Phase.Attack;
    }

    private void SetPhase(Phase phase, double now)
    {
        _phase = phase;
        _phaseStart = now;
    }

    private void Reset()
    {
        HoldAttack = false;
        ClearTarget();
        _phase = Phase.Idle;
        _pod = null;
        _enterSent = false;
    }

    private static void ClearTarget()
    {
        MelBotOverlay.SetTarget(null, default, default, default, true);
    }

    // Кричим в лог не чаще раза в 3 секунды.
    private void Hint(double now, string text)
    {
        if (now - _lastHint < 3.0)
            return;

        _lastHint = now;
        Console.WriteLine($"[SsMod] cryobot: {text}");
    }

    // ------------------------------------------------------- поиск целей

    private EntityUid? FindEnemy(EntityUid self, MapId map, Vector2 selfWorld)
    {
        _mobs.Clear();

        var mobs = _ent.AllEntityQueryEnumerator<MobStateComponent, MetaDataComponent>();
        while (mobs.MoveNext(out var uid, out var mob, out _))
        {
            if (uid != self && mob.CurrentState == MobState.Alive)
                _mobs.Add(uid);
        }

        EntityUid? best = null;
        var bestDist = TargetRange * TargetRange;

        foreach (var uid in _mobs)
        {
            if (uid == self)
                continue;

            if (!_ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.Deleted)
                continue;

            if (!_ent.HasComponent<TransformComponent>(uid))
                continue;

            // Бьём только тех, кому через ПКМ поставили Make Enemy.
            if (!Priorities.IsEnemy(uid))
                continue;

            if (_transform.GetMapId(uid) != map)
                continue;

            var dist = (_transform.GetWorldPosition(uid) - selfWorld).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            best = uid;
        }

        return best;
    }

    private EntityUid? FindPod(EntityUid self, MapId map, Vector2 selfWorld)
    {
        _pods.Clear();

        var pods = _ent.AllEntityQueryEnumerator<CryostorageComponent, MetaDataComponent>();
        while (pods.MoveNext(out var uid, out _, out _))
            _pods.Add(uid);

        EntityUid? best = null;
        var bestDist = PodRange * PodRange;

        foreach (var uid in _pods)
        {
            if (!_ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.Deleted)
                continue;

            // Нужна именно капсула, в которую можно залезть, а не любое
            // криохранилище (у части из них нет DragInsertContainer).
            if (!_ent.HasComponent<DragInsertContainerComponent>(uid))
                continue;

            if (!_ent.HasComponent<TransformComponent>(uid))
                continue;

            if (_transform.GetMapId(uid) != map)
                continue;

            var dist = (_transform.GetWorldPosition(uid) - selfWorld).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            best = uid;
        }

        return best;
    }

    // ------------------------------------------------------- действия

    private bool IsInside(EntityUid self, EntityUid pod)
    {
        return _ent.TryGetComponent(self, out TransformComponent? xform) && xform.ParentUid == pod;
    }

    // Ровно то, что делает игрок, перетаскивая себя на капсулу: на сервере это
    // SharedDragDropSystem -> DragDropTargetEvent -> DragInsertContainerSystem.
    private void SendEnter(EntityUid self, EntityUid pod)
    {
        _ent.EntityNetManager?.SendSystemNetworkMessage(
            new DragDropRequestEvent(_ent.GetNetEntity(self), _ent.GetNetEntity(pod)));
    }

    // Движение уходит и локально (предсказание), и на сервер: сервер поднимает
    // ContainerRelayMovementEntityEvent, и ExitContainerOnMoveSystem выбрасывает
    // нас из контейнера.
    private void SendMove(EntityUid self, BoundKeyState state)
    {
        var input = InputSystem();
        var session = _player.LocalSession;
        if (input is null || session is null)
            return;

        var function = EngineKeyFunctions.MoveUp;
        var id = _input.NetworkBindMap.KeyFunctionID(function);
        var coords = _transform.ToCoordinates(_transform.GetMapCoordinates(self));

        var message = new ClientFullInputCmdMessage(
            _timing.CurTick,
            _timing.TickFraction,
            id,
            coords,
            new ScreenCoordinates(0, 0, default),
            state,
            EntityUid.Invalid);

        input.HandleInputCommand(session, function, message);
        _moveState = state;
    }

    private void StopMove(EntityUid self)
    {
        if (_moveState != BoundKeyState.Down)
            return;

        SendMove(self, BoundKeyState.Up);
    }

    private Robust.Client.GameObjects.InputSystem? InputSystem()
    {
        if (_inputSystem is not null)
            return _inputSystem;

        try
        {
            _inputSystem = _ent.System<Robust.Client.GameObjects.InputSystem>();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] cryobot: нет InputSystem ({e.Message})");
        }

        return _inputSystem;
    }

    // ------------------------------------------------------- отрисовка

    private void Publish(in OverlayDrawArgs args, EntityUid self, EntityUid target)
    {
        var targetWorld = _transform.GetWorldPosition(target);
        var screen = args.Viewport.WorldToLocal(targetWorld) + Compat.BoxTopLeft(args.ViewportBounds);

        MelBotOverlay.SetTarget(
            target,
            _transform.GetMapCoordinates(target),
            screen,
            _input.MouseScreenPosition.Window,
            true);

        var handle = args.ScreenHandle;
        var selfScreen = args.Viewport.WorldToLocal(_transform.GetWorldPosition(self)) + Compat.BoxTopLeft(args.ViewportBounds);
        handle.DrawLine(selfScreen, screen, new Color(1f, 0.25f, 0.25f, 0.6f));

        if (_pod is { } pod && _ent.HasComponent<TransformComponent>(pod))
        {
            var podScreen = args.Viewport.WorldToLocal(_transform.GetWorldPosition(pod)) + Compat.BoxTopLeft(args.ViewportBounds);
            handle.DrawLine(selfScreen, podScreen, new Color(0.4f, 0.8f, 1f, 0.5f));
        }
    }
}

// Переключатель и тайминги крио-бота в окне мода.
public static class CryoBot
{
    public static Control BuildControls()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var check = new CheckBox
        {
            Text = "Cryo-Bot",
            Pressed = IoCManager.Resolve<IOverlayManager>().HasOverlay<CryoBotOverlay>(),
            ToolTip = "Сам бьёт помеченных Make Enemy и чередует заходы в капсулу криогенного сна",
        };
        box.AddChild(check);

        var grid = new GridContainer { Columns = 2 };

        // Тайминги не прячем и не сворачиваем: пока бот выключен, они просто серые.
        void Refresh() => Compat.SetGrayed(grid, !check.Pressed);

        check.OnToggled += args =>
        {
            Toggle(args.Pressed);
            Refresh();
        };

        grid.AddChild(new Label { Text = "Бить, с", MinWidth = 110f });
        var combat = new FloatSpinBox(0.1f, 2) { Value = CryoBotOverlay.CombatTime, HorizontalExpand = true };
        combat.OnValueChanged += args => CryoBotOverlay.CombatTime = Math.Clamp(args.Value, 0.1f, 30f);
        grid.AddChild(combat);

        grid.AddChild(new Label { Text = "В капсуле, с", MinWidth = 110f });
        var pod = new FloatSpinBox(0.1f, 2) { Value = CryoBotOverlay.PodTime, HorizontalExpand = true };
        pod.OnValueChanged += args => CryoBotOverlay.PodTime = Math.Clamp(args.Value, 0.1f, 30f);
        grid.AddChild(pod);

        box.AddChild(grid);

        Refresh();
        return box;
    }

    public static void Toggle(bool on)
    {
        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (overlays.HasOverlay<CryoBotOverlay>())
                return;

            overlays.AddOverlay(new CryoBotOverlay());
            Console.WriteLine("[SsMod] cryobot: включён");
        }
        else
        {
            overlays.RemoveOverlay<CryoBotOverlay>();
            CryoBotOverlay.Active = false;
            CryoBotOverlay.HoldAttack = false;
            MelBotOverlay.SetTarget(null, default, default, default, true);
            Console.WriteLine("[SsMod] cryobot: выключен");
        }
    }
}

// Пока крио-бот в боевой фазе и цель в пределах дальности — отдаём всем боевым
// системам «ЛКМ зажата». Они читают состояние кнопок из PlayerCommandStates
// (MeleeWeaponSystem.Update, GunSystem.Update), так что атака уходит на сервер
// обычным порядком, как будто игрок сам держит кнопку.
[HarmonyLib.HarmonyPatch(typeof(Robust.Shared.Input.PlayerCommandStates),
    nameof(Robust.Shared.Input.PlayerCommandStates.GetState))]
internal static class CryoBotAttackKeyPatch
{
    private static void Postfix(BoundKeyFunction function, ref BoundKeyState __result)
    {
        if (__result == BoundKeyState.Down || !CryoBotOverlay.HoldAttack)
            return;

        if (function == EngineKeyFunctions.Use)
            __result = BoundKeyState.Down;
    }
}
