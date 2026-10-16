using System.Numerics;
using Content.Client.Gameplay;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Weapons.Melee;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Mel-Bot: когда курсор подошёл к персонажу вплотную, рисуем линию
// от нашего персонажа до ближайшего под курсором.
//
// Радиус захвата — SnapTiles тайлов от курсора, в мировых координатах
// (1 тайл = 1 единица мира).
//
// Позицию курсора берём через args.Viewport.LocalToWorld, а рис через
// args.Viewport.WorldToLocal — это согласованная пара одного вьюпорта в одном
// масштабе. Сравнивать WorldToScreen с MouseScreenPosition нельзя: у них разные
// масштабы (Clyde.Viewport.cs:156 умножает на PixelsPerMeter и RenderScale,
// ViewportContainer.cs:155 сверху делит на _viewportResolution).
//
// Через EyeManager.PixelToMap дистанцию тоже не считаем: EyeManager.cs:157 при
// курсоре мимо вьюпорта молча отдаёт default (мир в нуле).
//
// Рисуем в ScreenSpace (это умолчание Overlay.Space) и берём args.ScreenHandle.
// args.WorldHandle — это каст к DrawingHandleWorld (OverlayDrawArgs.cs:63),
// в ScreenSpace он бросает InvalidCastException, поэтому WorldHandle тут нельзя.
//
// Перебор идёт по всем живым MobStateComponent, а не через broadphase:
// в этом билке GetEntitiesIntersecting под курсором отдаёт сотни тел
// (в логе сущностей=537), но ни одного с ActorComponent — поиск по физике
// персонажей не находит в принципе.
//
// Работа целиком на чтение: сущности, компоненты и позиции никто не трогает,
// меняется только то, что мы рисуем.

public sealed class MelBotOverlay : Overlay
{
    internal static EntityUid? CurrentTarget { get; private set; }
    internal static MapCoordinates CurrentTargetMapCoordinates { get; private set; }
    internal static Vector2 CurrentTargetScreenPosition { get; private set; }
    internal static WindowId CurrentWindow { get; private set; }

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly SharedTransformSystem _transform;

    // Радиус захвата в тайлах (1 тайл = 1 единица мира).
    public const float SnapTiles = 2f;

    // Радиус дуг вокруг цели, в тайлах. Задан в мире, чтобы дуги облегали
    // персонажа одинаково на любом зуме.
    public const float ArcRadiusTiles = 0.5f;

    // Раствор дуги в градусах. Круг делится на 4 дуги с промежутками 60° между ними.
    public const float ArcSpan = 30f;
    public const int ArcCount = 4;
    public const float ArcSpin = 90f; // градусов в секунду

    // Сегментов на дугу: чем больше, тем глаже обводка.
    private const int SegmentsPerArc = 32;

    // Толщина в пикселях. Толщины у DrawLine в движке нет: он рисует
    // GL_LINES из двух вершин (Clyde.Rendering.cs:718), то есть всегда 1 px.
    // Поэтому вся толщина делается заливкой TriangleFan — см. DrawThickLine.
    private const float LineWidth = 2f;
    private const float ArcWidth = 4f;

    private static readonly Color Line = new(1f, 0.85f, 0.2f, 0.5f);
    private static readonly Color Arc = new(1f, 0.45f, 0.15f, 0.3f);

    // Снимок живых существ на кадр. Заполняется один раз и переиспользуется:
    // AllEntityQueryEnumerator — это Dictionary.GetEnumerator() поверх живого
    // словаря, и если в него заходит новый игрок, итерация падает на середине.
    // Поэтому сначала копируем uid'ы, потом уже читаем позиции.
    private readonly List<EntityUid> _livingEntities = new();

    // Захваченная цель: пока зажата кнопка атаки, держим именно её, даже если
    // курсор ушёл далеко. Сбрасывается при отпускании кнопки или потере цели.
    private EntityUid? _lockTarget;

    // Последняя напечатанная диагностика, чтобы не спамить каждый кадр.
    private string _lastDiag = "";
    private double _lastDiagTime;

    public MelBotOverlay()
    {
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        CurrentTarget = null;
        try
        {
            if (args.Viewport.Eye is not { } eye)
                return;

            if (_player.LocalEntity is not { Valid: true } self)
                return;

            // Mel-Bot включается только в боевом режиме: вне него ничего не
            // ищем, не рисуем и не захватываем.
            if (!_ent.TryGetComponent(self, out CombatModeComponent? combat) || !combat.IsInCombatMode)
            {
                _lockTarget = null;
                return;
            }

            // Координаты курсора приводим в мир через LocalToWorld самого вьюпорта.
            //
            // Почему не PixelToMap: EyeManager.PixelToMap (EyeManager.cs:157) идёт
            // через _uiManager.MouseGetControl и молча отдаёт default (мир в нуле),
            // если курсор не над вьюпортом.
            //
            // Почему не WorldToScreen + сравнение с мышью: у этих координат разные
            // масштабы. ClydeViewport.WorldToLocal (Clyde.Viewport.cs:156) умножает
            // на PixelsPerMeter и RenderScale, а ViewportContainer.WorldToScreen
            // (ViewportContainer.cs:155) сверху делит ещё на _viewportResolution.
            // LocalToWorld/WorldToLocal — согласованная пара одного вьюпорта, они и нужны.
            var screenMouse = IoCManager.Resolve<IInputManager>().MouseScreenPosition;
            CurrentWindow = screenMouse.Window;
            var mouse = screenMouse.Position;
            var localMouse = mouse - args.ViewportBounds.TopLeft;
            var mouseWorld = args.Viewport.LocalToWorld(localMouse).Position;

            _zoomDiag = eye.Zoom.X;

            // 1) Снимок списка живых существ. Сначала копируем uid'ы и только потом
            //    читаем позиции: держать энумератор живого словаря нельзя —
            //    вошедший игрок ломает итерацию, и кадр падает целиком.
            _livingEntities.Clear();
            var query = _ent.AllEntityQueryEnumerator<MobStateComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out var mobState, out _))
            {
                if (mobState.CurrentState == MobState.Alive)
                    _livingEntities.Add(uid);
            }

            // 2) Из снимка ищем ближайшего к курсору в пределах SnapTiles тайлов.
            //    Дистанция в мире: 1 тайл = 1 единица, никаких зумов не нужно.
            EntityUid? nearestUid = null;
            var found = false;
            var usable = 0;
            var near = 0;
            var bestDist = SnapTiles * SnapTiles;

            // Временная детализация для отладки пропусков: кто в кадре и почему не выбран.
            var missLog = new List<string>();

            foreach (var uid in _livingEntities)
            {
                if (uid == self)
                    continue;

                if (!_ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.Deleted)
                    continue;

                if (!_ent.HasComponent<TransformComponent>(uid))
                    continue;

                usable++;

                var world = _transform.GetWorldPosition(uid);
                var dist = (world - mouseWorld).LengthSquared();
                var distT = MathF.Sqrt(dist);
                var name = meta?.EntityName ?? uid.ToString();

                if (dist >= bestDist)
                {
                    missLog.Add($"skip:{name} dist={distT:0.#}t name={name}");
                    continue;
                }

                near++;
                bestDist = dist;
                nearestUid = uid;
                found = true;
            }

            LogDiag(_livingEntities.Count, usable, near, found, mouseWorld);

            // Временный детальный лог пропусков: печатаем только при непустом списке
            // или при изменении состояния, чтобы не спамить.
            if (missLog.Count > 0)
            {
                var missText = string.Join(", ", missLog);
                var state = $"всего={_livingEntities.Count}, с трансформом={usable}, рядом={near}, цель={found}, зум={_zoomDiag:0.#}, мышь={mouseWorld.X:0.#},{mouseWorld.Y:0.#}, пропуски:[{missText}]";
                if (state != _lastDiag || (_timing.RealTime.TotalSeconds - _lastDiagTime) >= 1.0)
                {
                    _lastDiag = state;
                    _lastDiagTime = _timing.RealTime.TotalSeconds;
                    Console.WriteLine($"[SsMod] melbot: {state}");
                }
            }

            // Выбор активной цели. Пока зажата любая кнопка атаки — держим
            // захваченную цель, даже если курсор уже далеко (SnapTiles не мешает).
            // Кнопку отпустили — захват снимается, цель снова ищется под курсором.
            var input = _ent.System<Robust.Client.GameObjects.InputSystem>();
            var held = input.CmdStates.GetState(EngineKeyFunctions.Use) == BoundKeyState.Down
                       || input.CmdStates.GetState(EngineKeyFunctions.UseSecondary) == BoundKeyState.Down;

            EntityUid? activeUid;
            if (held)
            {
                if (_lockTarget is { } locked && _ent.EntityExists(locked) && _ent.HasComponent<TransformComponent>(locked))
                {
                    activeUid = locked;
                }
                else if (found)
                {
                    _lockTarget = nearestUid;
                    activeUid = nearestUid;
                }
                else
                {
                    activeUid = null;
                }
            }
            else
            {
                _lockTarget = null;
                activeUid = nearestUid;
            }

            if (activeUid is not { } target || !_ent.HasComponent<TransformComponent>(target))
                return;

            var targetWorld = _transform.GetWorldPosition(target);
            CurrentTarget = target;

            // Рисуем в ScreenSpace, поэтому мировые точки переводим в экранные
            // WorldToLocal того же вьюпорта — он в тех же координатах, что и LocalToWorld,
            // из которого получена mouseWorld. Плюс GlobalPixelPosition вьюпорта.
            var handle = args.ScreenHandle;
            var origin = args.ViewportBounds.TopLeft;
            var fromScreen = args.Viewport.WorldToLocal(_transform.GetWorldPosition(self)) + origin;
            var targetScreen = args.Viewport.WorldToLocal(targetWorld) + origin;
            CurrentTargetMapCoordinates = _transform.GetMapCoordinates(target);
            CurrentTargetScreenPosition = targetScreen;

            // 0.3 тайла в пикселях экрана. Масштаб берём у самой камеры:
            // две соседние мировые точки (на расстоянии ArcRadiusTiles) дают
            // в WorldToLocal экранное расстояние — это и есть радиус дуги в пикселях.
            var targetLocal = args.Viewport.WorldToLocal(targetWorld);
            var edgeLocal = args.Viewport.WorldToLocal(targetWorld + new Vector2(ArcRadiusTiles, 0f));
            var arcRadius = (edgeLocal - targetLocal).Length();

            DrawThickLine(handle, fromScreen, targetScreen, LineWidth, Line);
            DrawArcs(handle, targetScreen, arcRadius);
        }
        catch (Exception e)
        {
            // Оверлей рисуется каждый кадр: исключение здесь завалило бы рендер.
            // Логируем не один раз, а при смене текста — иначе повторяющийся сбой
            // останется невидимым.
            Console.Error.WriteLine($"[SsMod] melbot draw failed: {e.Message}");
        }
    }

    // Диагностика: печатаем, только если состояние изменилось или прошла секунда.
    private void LogDiag(int total, int usable, int near, bool found, Vector2 mouseWorld)
    {
        var now = _timing.RealTime.TotalSeconds;
        var text = $"всего={total}, с трансформом={usable}, рядом={near}, цель={found}, "
                   + $"зум={_zoomDiag:0.##}, мышь={mouseWorld.X:0.#},{mouseWorld.Y:0.#}";
        if (text == _lastDiag && now - _lastDiagTime < 1.0)
            return;

        _lastDiag = text;
        _lastDiagTime = now;
        Console.WriteLine($"[SsMod] melbot: {text}");
    }

    private float _zoomDiag;

    // Толстый отрезок заливкой: 4 вершины = 2 треугольника.
    //
    // DrawLine так не умеет: Clyde.Rendering.cs:718 собирает GL_LINES ровно
    // из двух вершин, толщины у линии нет (glLineWidth на десктопных профилях
    // OpenGL зафиксирован на 1.0). Прямоугольник вдоль отрезка строим сами.
    private static void DrawThickLine(DrawingHandleScreen handle, Vector2 from, Vector2 to, float width, Color color)
    {
        var dir = to - from;
        var len = dir.Length();
        if (len <= 0.0001f)
            return;

        // Перпендикуляр к отрезку: половина толщины в каждую сторону.
        var normal = new Vector2(-dir.Y, dir.X) / len * (width / 2f);

        Span<Vector2> quad = stackalloc Vector2[4];
        quad[0] = from - normal;
        quad[1] = to - normal;
        quad[2] = to + normal;
        quad[3] = from + normal;

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, quad, color);
    }

    // Круг вокруг цели, разделённый на 4 дуги по 30°, разнесённые по окружности
    // и вращающиеся вокруг неё.
    //
    // Дуги обводятся толстыми отрезками: 4 * SegmentsPerArc коротких линий,
    // состыкованных в гладкую кривую.
    private void DrawArcs(DrawingHandleScreen handle, Vector2 center, float radius)
    {
        // Спин по реальному времени: скорость не зависит от тиков сервера.
        var spin = (float) _timing.RealTime.TotalSeconds * (ArcSpin * (MathF.PI / 180f));
        var span = ArcSpan * (MathF.PI / 180f);

        // Дуги разнесены по всей окружности: шаг = 360 / 4 = 90°,
        // между ними остаются пустые промежутки по 60°.
        var step = MathF.Tau / ArcCount;

        for (var i = 0; i < ArcCount; i++)
        {
            // Стартовый угол дуги: разносим по окружности и крутим общим спином.
            var start = spin + step * i;

            for (var s = 0; s < SegmentsPerArc; s++)
            {
                var a0 = start + span * s / SegmentsPerArc;
                var a1 = start + span * (s + 1) / SegmentsPerArc;
                DrawThickLine(handle, Point(center, a0, radius), Point(center, a1, radius), ArcWidth, Arc);
            }
        }
    }

    private static Vector2 Point(Vector2 center, float angle, float radius)
    {
        // -Cos по Y: дуги крутятся в экранных координатах по часовой стрелке.
        return center + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * radius;
    }
}

// Переключатель в окне мода (Ui/SsModWindow.cs, фрейм 1).
public static class MelBot
{
    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Mel-Bot",
            Pressed = IoCManager.Resolve<IOverlayManager>().HasOverlay<MelBotOverlay>(),
            ToolTip = "Линия от персонажа к тому, кто под курсором",
        };
    }

    public static void Toggle(bool on)
    {
        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (overlays.HasOverlay<MelBotOverlay>())
                return;

            overlays.AddOverlay(new MelBotOverlay());
        }
        else
        {
            overlays.RemoveOverlay<MelBotOverlay>();
        }
    }
}

// Переносим обычный клик мыши в экранную точку, на которую указывает мелбот.
[HarmonyLib.HarmonyPatch(typeof(Robust.Client.GameObjects.InputSystem), nameof(Robust.Client.GameObjects.InputSystem.HandleInputCommand))]
internal static class MelBotClickPatch
{
    private static void Prefix(ref Robust.Shared.Input.IFullInputCmdMessage message)
    {
        if (message is not Robust.Shared.Input.ClientFullInputCmdMessage input ||
            input.State != Robust.Shared.Input.BoundKeyState.Down ||
            input.InputFunctionId != IoCManager.Resolve<Robust.Client.Input.IInputManager>()
                .NetworkBindMap.KeyFunctionID(Robust.Shared.Input.EngineKeyFunctions.Use) &&
            input.InputFunctionId != IoCManager.Resolve<Robust.Client.Input.IInputManager>()
                .NetworkBindMap.KeyFunctionID(Robust.Shared.Input.EngineKeyFunctions.UseSecondary) ||
            MelBotOverlay.CurrentTarget is null)
        {
            return;
        }

        var ent = IoCManager.Resolve<IEntityManager>();
        var transform = ent.System<SharedTransformSystem>();
        var targetMap = MelBotOverlay.CurrentTargetMapCoordinates;
        var coords = transform.ToCoordinates(targetMap);
        var clicked = IoCManager.Resolve<Robust.Client.State.IStateManager>().CurrentState is GameplayStateBase screen
            ? screen.GetClickedEntity(targetMap) ?? EntityUid.Invalid
            : EntityUid.Invalid;

        message = new Robust.Shared.Input.ClientFullInputCmdMessage(
            input.Tick,
            input.SubTick,
            input.InputFunctionId,
            coords,
            new ScreenCoordinates(MelBotOverlay.CurrentTargetScreenPosition, input.ScreenCoordinates.Window),
            input.State,
            clicked);
    }
}

// Боевые системы берут позицию курсора каждый кадр, а не координаты события клика.
// Пока нажата любая кнопка атаки, отдаём им экранную точку цели Mel-Bot.
[HarmonyLib.HarmonyPatch]
internal static class MelBotAttackCursorPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        var type = HarmonyLib.AccessTools.TypeByName("Robust.Client.Input.ClydeInputManager")!;
        return HarmonyLib.AccessTools.PropertyGetter(type, "MouseScreenPosition")!;
    }

    private static bool Prefix(ref ScreenCoordinates __result)
    {
        if (MelBotOverlay.CurrentTarget is null)
            return true;

        var input = IoCManager.Resolve<IEntityManager>().System<Robust.Client.GameObjects.InputSystem>();
        var states = input.CmdStates;
        var left = states.GetState(Robust.Shared.Input.EngineKeyFunctions.Use);
        var right = states.GetState(Robust.Shared.Input.EngineKeyFunctions.UseSecondary);
        if (left != Robust.Shared.Input.BoundKeyState.Down &&
            right != Robust.Shared.Input.BoundKeyState.Down)
        {
            return true;
        }

        __result = new ScreenCoordinates(MelBotOverlay.CurrentTargetScreenPosition, MelBotOverlay.CurrentWindow);
        return false;
    }
}

// Ближний бой сам по себе не повторяет удар при удержании кнопки: после первого
// удара MeleeWeaponComponent.Attacking остаётся true, и новые удары не идут до
// отпускания. Пока мод держит цель и зажата кнопка, гасим флаг перед апдейтом —
// получается непрерывная атака по цели.
[HarmonyLib.HarmonyPatch(typeof(Content.Client.Weapons.Melee.MeleeWeaponSystem),
    nameof(Content.Client.Weapons.Melee.MeleeWeaponSystem.Update))]
internal static class MelBotMeleePatch
{
    private static void Prefix(Content.Client.Weapons.Melee.MeleeWeaponSystem __instance)
    {
        if (MelBotOverlay.CurrentTarget is null)
            return;

        if (!IoCManager.Resolve<IGameTiming>().IsFirstTimePredicted)
            return;

        var ent = IoCManager.Resolve<IEntityManager>();
        var player = IoCManager.Resolve<IPlayerManager>();
        if (player.LocalEntity is not { Valid: true } self)
            return;

        var input = ent.System<Robust.Client.GameObjects.InputSystem>();
        if (input.CmdStates.GetState(EngineKeyFunctions.Use) != BoundKeyState.Down &&
            input.CmdStates.GetState(EngineKeyFunctions.UseSecondary) != BoundKeyState.Down)
        {
            return;
        }

        if (__instance.TryGetWeapon(self, out _, out var weapon) && weapon is not null)
            weapon.Attacking = false;
    }
}
