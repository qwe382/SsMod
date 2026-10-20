using System.Collections;
using System.Numerics;
using System.Reflection;
using Content.Client.Shuttles.UI;
using Content.Client.UserInterface.Controls;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.BUIStates;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// «Сканер массы» — это штатный радар игры, свой рисовать не нужно.
//
// В контенте предмет HandHeldMassScanner — обычный RadarConsole: окно
// RadarConsoleWindow, внутри ShuttleNavControl, данные — NavInterfaceState.
// Мод не изобретает свой радар, а открывает то же окно и кормит его
// состоянием, собранным на клиенте (предмет в руках не нужен): центр — игрок,
// блипы — nearby-игроки и мобы.
//
// Состояние собирается рефлексией: в 276 у NavInterfaceState конструктор с
// пятью параметрами (последний — список блипов), в 277 — с четырьмя, а сами
// блипы лежат в поле Blips. Прямой вызов конструктора компилируется в
// конкретную сигнатуру и на другой версии падает с MissingMethodException.
public static class MassScanner
{
    // Стартовая и максимальная/минимальная дальность радара в тайлах. У
    // HandHeldMassScanner в прототипе 256; 64 — рабочий обзор станции.
    private const float DefaultZoom = 64f;
    private const float MinZoom = 8f;
    private const float MaxZoom = 256f;

    // Как часто пересобирать блипы: сущности двигаются, снимок должен жить.
    internal const float RefreshInterval = 0.4f;

    // Сколько блипов максимум: дальше радар превращается в кашу.
    private const int MaxBlips = 48;

    private static RadarConsoleWindow? _window;
    private static RadarRefresher? _refresher;
    private static RadarInput? _input;
    private static ShuttleNavControl? _radar;

    // Последнее отданное радару состояние: панорамирование меняет только
    // координаты центра, блипы пересобираются по таймеру.
    private static NavInterfaceState? _state;

    // Смещение обзора от игрока в мировых единицах (панорамирование мышью).
    private static Vector2 _pan;

    public static Control BuildTab()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        box.AddChild(new Label { Text = "Сканер массы" });

        var open = new Button { Text = "Открыть сканер массы" };
        open.OnPressed += _ => Open();
        box.AddChild(open);

        box.AddChild(new Label
        {
            Text = "Штатный радар игры (как у HandHeldMassScanner), без предмета в руках:",
        });
        box.AddChild(new Label { Text = "колесо — приближение/отдаление, ЛКМ/ПКМ — двигать радар." });

        return box;
    }

    public static void Open()
    {
        var self = LocalPlayer();
        if (self == null)
        {
            Console.WriteLine("[SsMod] сканер массы: нет персонажа");
            return;
        }

        try
        {
            if (_window is not { Disposed: false })
            {
                // Как CreateWindow в BoundUserInterface: фабрика типов сама
                // прокидывает [Dependency] в контролы (иначе радар падал бы в
                // Draw на незаполненном IMapManager).
                var ui = IoCManager.Resolve<IUserInterfaceManager>();
                _window = ui.CreateWindow<RadarConsoleWindow>();
                _state = null;
                _radar = null;
                _input = null;
                _pan = Vector2.Zero;
            }

            PushState(self.Value);

            if (_window.IsOpen)
                _window.Close();

            _window.OpenCentered();
            AttachInput();
            AttachRefresher();
            Console.WriteLine("[SsMod] сканер массы: открыт (радар игры)");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] сканер массы: {e}");
            _window = null;
            _state = null;
        }
    }

    private static EntityUid? LocalPlayer()
    {
        var ent = IoCManager.Resolve<IEntityManager>();
        var player = IoCManager.Resolve<IPlayerManager>().LocalEntity;

        if (player is not { Valid: true } self ||
            !ent.EntityExists(self) ||
            !ent.TryGetComponent(self, out TransformComponent? xform) ||
            xform.MapID == MapId.Nullspace)
            return null;

        return self;
    }

    // Панорамирование: смещение в мировых единицах (экранные пиксели считает
    // RadarInput). Меняется только центр состояния — блипы не трогаем.
    internal static void Pan(Vector2 worldDelta)
    {
        _pan += worldDelta;
        ApplyCenter();
    }

    private static void ApplyCenter()
    {
        if (_window == null || _state == null || LocalPlayer() is not { } self)
            return;

        var ent = IoCManager.Resolve<IEntityManager>();
        var transform = ent.System<SharedTransformSystem>();

        var world = transform.GetWorldPosition(self) + _pan;
        var center = transform.ToCoordinates(new MapCoordinates(world, transform.GetMapId(self)));

        try
        {
            _state.Coordinates = ent.GetNetCoordinates(center);
            _window.UpdateState(_state);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] сканер массы: панорама: {e.Message}");
        }
    }

    // Пересборка блипов, пока окно открыто (зовёт RadarRefresher).
    internal static void Refresh()
    {
        if (_window is not { IsOpen: true } || _window.Disposed)
            return;

        if (LocalPlayer() is not { } self)
            return;

        try
        {
            PushState(self);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] сканер массы: {e.Message}");
        }
    }

    private static void AttachRefresher()
    {
        if (_refresher is { Disposed: false })
            return;

        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        var refresher = new RadarRefresher();
        LayoutContainer.SetAnchorPreset(refresher, LayoutContainer.LayoutPreset.Wide);
        refresher.MouseFilter = Control.MouseFilterMode.Ignore;
        ui.PopupRoot.AddChild(refresher);
        _refresher = refresher;
    }

    // Свой ввод поверх радара: штатный ShuttleNavControl умеет только зум
    // колесом (и то в диапазоне 64..256), а Draggable у него false, поэтому
    // панорамирование вешаем на прозрачный слой.
    private static void AttachInput()
    {
        if (_window == null)
            return;

        _radar ??= FindRadar(_window);
        if (_radar == null)
        {
            Console.Error.WriteLine("[SsMod] сканер массы: ShuttleNavControl не найден");
            return;
        }

        // Разрешаем приближаться ближе 64 тайлов (штатный минимум радара).
        RelaxMinZoom(_radar);

        if (_input is { Disposed: false } && _input.Parent != null)
            return;

        // Стартовый обзор. WorldRange — поле MapGridControl (256 по умолчанию).
        _radar.AddRadarRange(DefaultZoom - _radar.WorldRange);

        var input = new RadarInput(_radar);
        LayoutContainer.SetAnchorPreset(input, LayoutContainer.LayoutPreset.Wide);
        _radar.AddChild(input);
        _input = input;
    }

    private static ShuttleNavControl? FindRadar(Control root)
    {
        foreach (var child in root.Children)
        {
            if (child is ShuttleNavControl radar)
                return radar;

            if (FindRadar(child) is { } nested)
                return nested;
        }

        return null;
    }

    private static void RelaxMinZoom(ShuttleNavControl radar)
    {
        var field = typeof(MapGridControl).GetField("WorldMinRange",
            BindingFlags.NonPublic | BindingFlags.Instance);

        field?.SetValue(radar, MinZoom);
    }

    private static void PushState(EntityUid self)
    {
        if (_window == null)
            return;

        var ent = IoCManager.Resolve<IEntityManager>();
        var transform = ent.System<SharedTransformSystem>();

        var stateType = typeof(NavInterfaceState);
        var docksType = typeof(Dictionary<NetEntity, List<DockingPortState>>);

        // Ищем конструктор по первым четырём параметрам — они одинаковые в 276
        // и 277; хвостовые (список блипов в 276) заполняем значениями по умолчанию.
        ConstructorInfo? ctor = null;
        foreach (var candidate in stateType.GetConstructors())
        {
            var ps = candidate.GetParameters();
            if (ps.Length >= 4 &&
                ps[0].ParameterType == typeof(float) &&
                ps[1].ParameterType == typeof(NetCoordinates?) &&
                ps[2].ParameterType == typeof(Angle?) &&
                ps[3].ParameterType == docksType)
            {
                ctor = candidate;
                break;
            }
        }

        if (ctor == null)
        {
            Console.Error.WriteLine("[SsMod] сканер массы: конструктор NavInterfaceState не найден");
            return;
        }

        var selfMap = transform.GetMapId(self);
        var world = transform.GetWorldPosition(self) + _pan;
        var center = transform.ToCoordinates(new MapCoordinates(world, selfMap));

        var args = new object?[ctor.GetParameters().Length];
        args[0] = MaxZoom;
        args[1] = (NetCoordinates?) ent.GetNetCoordinates(center);
        args[2] = (Angle?) Angle.Zero;
        args[3] = Activator.CreateInstance(docksType);
        for (var i = 4; i < args.Length; i++)
            args[i] = DefaultValue(ctor.GetParameters()[i].ParameterType);

        var created = ctor.Invoke(args);
        if (created is not NavInterfaceState state)
            return;

        // Как у HandHeldMassScanner с followEntity: радар смотрит от игрока и не
        // крутится вместе с ним.
        stateType.GetField("RotateWithEntity")?.SetValue(state, false);

        // В 277 радар рисует ещё и лазеры из состояния: пустой список, но не null.
        EnsureEmptyList(stateType, state, "Lasers");

        FillBlips(stateType, state, self, selfMap, transform, ent);

        _state = state;
        _window.UpdateState(state);
    }

    // Блипы — то, что на радаре рисуется поверх гридов. В 276 поля Blips нет,
    // там радар умеет только гриды; в 277 блипы есть.
    private static void FillBlips(Type stateType, object state, EntityUid self, MapId selfMap,
        SharedTransformSystem transform, IEntityManager ent)
    {
        var field = stateType.GetField("Blips");
        if (field == null)
            return;

        var list = field.GetValue(state) as IList;
        if (list == null)
        {
            list = Activator.CreateInstance(field.FieldType) as IList;
            if (list == null)
                return;

            try
            {
                field.SetValue(state, list);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SsMod] сканер массы: блипы недоступны: {e.Message}");
                return;
            }
        }
        else
        {
            list.Clear();
        }

        var listType = field.FieldType;
        var blipType = listType.GetGenericArguments()[0];
        var blipCtor = blipType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 4);
        if (blipCtor == null)
            return;

        var selfPos = transform.GetWorldPosition(self);
        var rangeSq = MaxZoom * MaxZoom;

        // Только игроки и мобы. Предметы на станции считаются сотнями, их блипы
        // сливаются в сплошное пятно и закрывают весь радар.
        var found = new List<(Vector2 World, float DistSq, float Mass, bool IsPlayer)>();
        var query = ent.AllEntityQueryEnumerator<TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out var meta))
        {
            if (uid == self || meta.Deleted)
                continue;

            var isPlayer = ent.HasComponent<ActorComponent>(uid);
            if (!isPlayer && !ent.HasComponent<MobStateComponent>(uid))
                continue;

            if (transform.GetMapId(uid) != selfMap)
                continue;

            var world = transform.GetWorldPosition(uid);
            var distSq = (world - selfPos).LengthSquared();
            if (distSq > rangeSq)
                continue;

            var mass = ent.TryGetComponent(uid, out PhysicsComponent? physics) ? physics.FixturesMass : 0f;
            found.Add((world, distSq, mass, isPlayer));
        }

        // Ближайшие первыми: при переполнении кап оставляет тех, кто рядом.
        found.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

        var shapeType = blipCtor.GetParameters()[3].ParameterType;

        foreach (var item in found)
        {
            if (list.Count >= MaxBlips)
                break;

            var color = item.IsPlayer
                ? new Color(0.3f, 1f, 0.4f)
                : new Color(1f, 0.35f, 0.35f);

            var shapeName = item.IsPlayer ? "Square" : "Triangle";
            var shape = Enum.TryParse(shapeType, shapeName, out var parsed)
                ? parsed
                : Activator.CreateInstance(shapeType);

            // Крупные мобы чуть заметнее, но без раздувания до половины экрана.
            var scale = Math.Clamp(0.45f + MathF.Log10(MathF.Max(item.Mass, 1f)) * 0.15f, 0.45f, 1.1f);

            var blip = blipCtor.Invoke(new object?[]
            {
                ent.GetNetCoordinates(transform.ToCoordinates(new MapCoordinates(item.World, selfMap))),
                color,
                scale,
                shape,
            });

            list.Add(blip);
        }
    }

    private static void EnsureEmptyList(Type stateType, object state, string name)
    {
        var field = stateType.GetField(name);
        if (field == null || field.GetValue(state) != null)
            return;

        if (Activator.CreateInstance(field.FieldType) is not { } empty)
            return;

        try
        {
            field.SetValue(state, empty);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] сканер массы: {name} недоступен: {e.Message}");
        }
    }

    private static object? DefaultValue(Type type)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type);

        // Списки (в 276 — блипы) должны быть пустыми, а не null.
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? Activator.CreateInstance(type)
            : null;
    }
}

// Пока окно радара открыто, раз в Interval пересобираем блипы: сущности
// двигаются, а состояние радара — снимок.
internal sealed class RadarRefresher : Control
{
    private double _since;

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        _since += args.DeltaSeconds;
        if (_since < MassScanner.RefreshInterval)
            return;

        _since = 0;
        MassScanner.Refresh();
    }
}

// Прозрачный слой поверх ShuttleNavControl: колесо — зум, ЛКМ/ПКМ — панорама.
internal sealed class RadarInput : Control
{
    private readonly ShuttleNavControl _radar;
    private bool _dragging;

    public RadarInput(ShuttleNavControl radar)
    {
        _radar = radar;
        MouseFilter = MouseFilterMode.Stop;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (args.Function != EngineKeyFunctions.Use &&
            args.Function != EngineKeyFunctions.UseSecondary)
            return;

        _dragging = true;
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (args.Function != EngineKeyFunctions.Use &&
            args.Function != EngineKeyFunctions.UseSecondary)
            return;

        _dragging = false;
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);

        if (!_dragging)
            return;

        var half = _radar.Size / 2f;
        if (half.X < 1f || half.Y < 1f)
            return;

        var worldPerPixel = _radar.WorldRange / half.X;
        MassScanner.Pan(new Vector2(-args.Relative.X, args.Relative.Y) * worldPerPixel);
        args.Handle();
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);

        // Штатная формула MapGridControl, но диапазон расширен до 8..256:
        // стандартный минимум радара — 64 тайла.
        _radar.AddRadarRange(-args.Delta.Y * _radar.WorldRange / 8f);
        args.Handle();
    }
}
