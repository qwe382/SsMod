using System.Globalization;
using System.Numerics;
using System.Reflection;
using Content.Client.Shuttles.UI;
using Content.Client.UserInterface.Controls;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.BUIStates;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
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

// «Сканер массы» — штатный радар игры в собственном окне-фрейме.
//
// В контенте предмет HandHeldMassScanner — обычный RadarConsole: окно
// RadarConsoleWindow, внутри ShuttleNavControl, данные — NavInterfaceState.
// Мод открывает то же окно (предмет в руках не нужен) и расширяет его:
// справа панель со своими координатами и точками, поверх радара — свои
// отметки персонажей и 1-пиксельные линии до них.
//
// Ориентация: радар повторяет поворот камеры (Eye.Rotation) — куда повёрнут
// экран, туда и «верх» карты. Состояние радара собирается рефлексией:
// в разных версиях клиента у NavInterfaceState разное число параметров
// конструктора, прямой вызов падает на чужой версии.
public static class MassScanner
{
    // Стартовая и максимальная/минимальная дальность радара в тайлах. У
    // HandHeldMassScanner в прототипе 256; 64 — рабочий обзор станции.
    private const float DefaultZoom = 64f;
    private const float MinZoom = 8f;
    private const float MaxZoom = 256f;

    // Как часто пересобирать отметки: сущности двигаются, снимок должен жить.
    internal const float RefreshInterval = 0.4f;

    // Сколько отметок максимум: дальше радар превращается в кашу.
    private const int MaxMarks = 64;

    // Геометрия MapGridControl: радиус экрана радара 320 UI-пикселей,
    // запас по краю 4, итого квадрат 648. Размеры у контрола фиксированные,
    // поэтому середину и масштаб считаем от них, а не от PixelSize.
    private const float UIRadius = 320f;
    private const float UISize = (UIRadius + 4f) * 2f;

    private static RadarConsoleWindow? _window;
    private static RadarRefresher? _refresher;
    private static RadarInput? _input;
    private static ShuttleNavControl? _radar;
    private static bool _layoutDone;

    // Последнее отданное радару состояние: панорамирование меняет только
    // координаты центра, поворот и отметки обновляются каждый кадр/тик.
    private static NavInterfaceState? _state;

    // Смещение обзора от игрока в мировых единицах (панорамирование мышью).
    private static Vector2 _pan;

    // Последний известный поворот камеры (радианы, 0 = экран вверх = север).
    private static Angle _lastRotation;

    // ---------- панель окна ----------

    private static Label? _coordsLabel;
    private static Label? _statusLabel;
    private static BoxContainer? _rowsHost;
    private static LineEdit? _pointEdit;
    private static CheckBox? _overlayCheck;
    private static CheckBox? _metersCheck;
    private static OptionButton? _modeButton;

    private sealed class RowRefs
    {
        public Label Text = null!;
        public int Index;
    }

    private static readonly List<RowRefs> Rows = new();

    // ---------- точки (вейпоинты) ----------

    internal sealed class Waypoint
    {
        public Vector2 World;
        public MapId Map;
    }

    private static readonly List<Waypoint> Waypoints = new();
    private static int _selected = -1;

    internal static Waypoint? SelectedWaypoint =>
        _selected >= 0 && _selected < Waypoints.Count ? Waypoints[_selected] : null;

    // Доступ для RadarMarks.
    internal static int WaypointCount => Waypoints.Count;

    internal static Waypoint? GetWaypoint(int index) =>
        index >= 0 && index < Waypoints.Count ? Waypoints[index] : null;

    internal static int SelectedWaypointIndex => _selected;

    // ---------- отметки персонажей ----------

    internal sealed class Mark
    {
        public Vector2 World;
        public bool Player;
    }

    private static readonly List<Mark> Marks = new();

    internal static IReadOnlyList<Mark> SnapshotMarks() => Marks;

    // ---------- оверлей в игре (палка/стрелка до точки) ----------

    public enum MarkMode
    {
        Stick,
        Arrow,
    }

    internal static bool OverlayEnabled;
    internal static MarkMode OverlayMode = MarkMode.Stick;
    internal static float OverlayLength = 160f;
    internal static float OverlayWidth = 1f;
    internal static bool OverlayMeters = true;

    public static void SetOverlay(bool on)
    {
        OverlayEnabled = on;

        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (!overlays.HasOverlay<ScannerMarkOverlay>())
                overlays.AddOverlay(new ScannerMarkOverlay());
        }
        else
        {
            overlays.RemoveOverlay<ScannerMarkOverlay>();
        }

        Console.WriteLine($"[SsMod] сканер: оверлей на экране = {on}");
    }

    // Регистрация ключей конфига (зовётся из Config.EnsureRegistered).
    public static void RegisterConfig()
    {
        Config.AddPublic("scanner.overlay",
            () => OverlayEnabled ? "true" : "false",
            v => SetOverlay(v.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes"));

        Config.AddPublic("scanner.mode",
            () => OverlayMode.ToString(),
            v => OverlayMode = Enum.Parse<MarkMode>(v, true));

        Config.AddPublic("scanner.length",
            () => OverlayLength.ToString("R", CultureInfo.InvariantCulture),
            v =>
            {
                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    OverlayLength = Math.Clamp(f, 40f, 500f);
            });

        Config.AddPublic("scanner.width",
            () => OverlayWidth.ToString("R", CultureInfo.InvariantCulture),
            v =>
            {
                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    OverlayWidth = Math.Clamp(f, 1f, 6f);
            });

        Config.AddPublic("scanner.meters",
            () => OverlayMeters ? "true" : "false",
            v => OverlayMeters = v.Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes");
    }

    // ---------- вкладка в главном окне ----------

    public static Control BuildTab()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        box.AddChild(new Label { Text = "Сканер массы" });

        var open = new Button { Text = "Открыть сканер" };
        open.OnPressed += _ => Open();
        box.AddChild(open);

        var overlay = new CheckBox { Text = "Оверлей: указатель на выбранную точку", Pressed = OverlayEnabled };
        overlay.OnToggled += args => SetOverlay(args.Pressed);
        box.AddChild(overlay);

        box.AddChild(new Label
        {
            Text = "Радар повторяет поворот камеры. Справа в окне сканера — свои " +
                   "координаты, точки и настройки оверлея.",
        });

        return box;
    }

    // ---------- открытие окна ----------

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
                _window.Title = "Сканер массы";
                _state = null;
                _radar = null;
                _input = null;
                _pan = Vector2.Zero;
                _layoutDone = false;
                _coordsLabel = null;
                _statusLabel = null;
                _rowsHost = null;
                _pointEdit = null;
                _overlayCheck = null;
                _metersCheck = null;
                _modeButton = null;
                Rows.Clear();
            }

            _radar ??= FindRadar(_window);
            if (_radar == null)
            {
                Console.Error.WriteLine("[SsMod] сканер массы: ShuttleNavControl не найден");
                return;
            }

            BuildLayout(_window, _radar);

            PushState(self.Value);

            if (_window.IsOpen)
                _window.Close();

            _window.OpenCentered();
            AttachInput();
            AttachRefresher();
            Console.WriteLine("[SsMod] сканер массы: открыт");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] сканер массы: {e}");
            _window = null;
            _state = null;
        }
    }

    // Перекомпоновка окна: слева карта, справа панель. Один раз на окно.
    private static void BuildLayout(RadarConsoleWindow window, ShuttleNavControl radar)
    {
        if (_layoutDone)
            return;

        var contents = window.ContentsContainer;
        contents.RemoveAllChildren();

        var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        contents.AddChild(row);

        radar.HorizontalExpand = true;
        radar.VerticalExpand = true;
        row.AddChild(radar);
        row.AddChild(BuildPanel());

        window.SetSize = new Vector2(1080f, 680f);
        window.MinSize = new Vector2(760f, 520f);

        _layoutDone = true;
    }

    // ---------- панель справа ----------

    private static Control BuildPanel()
    {
        var scroll = new ScrollContainer
        {
            HScrollEnabled = false,
            ReturnMeasure = true,
            MinWidth = 330,
            VerticalExpand = true,
        };

        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        scroll.AddChild(box);

        // --- свои координаты ---
        box.AddChild(new Label { Text = "Свои координаты" });
        _coordsLabel = new Label { Text = "(—, —)" };
        box.AddChild(_coordsLabel);

        var coordRow = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        coordRow.AddChild(Button("Копировать", CopyOwnCoords));
        coordRow.AddChild(Button("Сброс позиции", ResetPan));
        box.AddChild(coordRow);

        // --- точки ---
        box.AddChild(new Label { Text = "Точки (куда лететь)" });

        var inputRow = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        _pointEdit = new LineEdit { PlaceHolder = "x, y", HorizontalExpand = true };
        _pointEdit.OnTextEntered += _ => AddFromEdit();
        inputRow.AddChild(_pointEdit);
        inputRow.AddChild(Button("Добавить", AddFromEdit));
        inputRow.AddChild(Button("Из буфера", async () => await AddFromClipboard()));
        box.AddChild(inputRow);

        _statusLabel = new Label { Text = "" };
        box.AddChild(_statusLabel);

        _rowsHost = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        box.AddChild(_rowsHost);
        RebuildRows();

        // --- оверлей ---
        box.AddChild(new Label { Text = "Оверлей в игре (до выбранной точки)" });

        _overlayCheck = new CheckBox { Text = "Показывать указатель", Pressed = OverlayEnabled };
        _overlayCheck.OnToggled += args => SetOverlay(args.Pressed);
        box.AddChild(_overlayCheck);

        var modeRow = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        modeRow.AddChild(new Label { Text = "Вид: " });
        _modeButton = new OptionButton();
        _modeButton.AddItem("Палка");
        _modeButton.AddItem("Стрелка");
        _modeButton.SelectId(OverlayMode == MarkMode.Arrow ? 1 : 0);
        _modeButton.OnItemSelected += args =>
        {
            OverlayMode = args.Id == 1 ? MarkMode.Arrow : MarkMode.Stick;
        };
        modeRow.AddChild(_modeButton);
        box.AddChild(modeRow);

        _metersCheck = new CheckBox { Text = "Показывать метры", Pressed = OverlayMeters };
        _metersCheck.OnToggled += args => OverlayMeters = args.Pressed;
        box.AddChild(_metersCheck);

        box.AddChild(new Label
        {
            Text = "Палка/стрелка тянутся от персонажа в сторону точки. " +
                   "Длина и толщина — ключи scanner.length / scanner.width в конфиге.",
            FontColorOverride = new Color(0.7f, 0.7f, 0.7f),
        });

        box.AddChild(new Label
        {
            Text = "Радар: колесо — зум (8..256 тайлов), ЛКМ/ПКМ — двигать карту. " +
                   "Верх радара всегда совпадает с верхом экрана.",
            FontColorOverride = new Color(0.7f, 0.7f, 0.7f),
        });

        return scroll;
    }

    private static Button Button(string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.OnPressed += _ => onPressed();
        return button;
    }

    // ---------- свои координаты ----------

    private static void CopyOwnCoords()
    {
        if (LocalPlayer() is not { } self)
            return;

        var transform = IoCManager.Resolve<IEntityManager>().System<SharedTransformSystem>();
        var world = transform.GetWorldPosition(self) + _pan;
        var text = $"{F(world.X)}, {F(world.Y)}";

        IoCManager.Resolve<IClipboardManager>().SetText(text);
        SetStatus($"скопировано: {text}");
    }

    private static void ResetPan()
    {
        _pan = Vector2.Zero;
        ApplyCenter();
        SetStatus("позиция радара сброшена на персонажа");
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static void SetStatus(string text)
    {
        if (_statusLabel != null)
            _statusLabel.Text = text;
    }

    // ---------- разбор координат ----------

    // Понимает «1024, 512», «1024.5 512», «(1024.5, 512)», «1024,5 -512,5».
    public static bool TryParsePoint(string input, out Vector2 point)
    {
        point = Vector2.Zero;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var cleaned = input
            .Replace('(', ' ').Replace(')', ' ')
            .Replace('[', ' ').Replace(']', ' ')
            .Replace(';', ' ')
            .Trim();

        // Вариант 1: запятая — разделитель («x, y»).
        var parts = cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            point = new Vector2(x, y);
            return true;
        }

        // Вариант 2: пробел — разделитель, запятая — десятичный знак.
        parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            var a = parts[0].Replace(',', '.');
            var b = parts[1].Replace(',', '.');
            if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                point = new Vector2(x, y);
                return true;
            }
        }

        return false;
    }

    // ---------- точки ----------

    private static void AddFromEdit()
    {
        if (_pointEdit == null)
            return;

        var text = _pointEdit.Text;
        if (!TryParsePoint(text, out var point))
        {
            SetStatus("не понял координаты, нужен формат «x, y»");
            return;
        }

        _pointEdit.Text = "";
        AddWaypoint(point);
        SetStatus($"точка добавлена: ({F(point.X)}, {F(point.Y)})");
    }

    private static async Task AddFromClipboard()
    {
        var clip = await IoCManager.Resolve<IClipboardManager>().GetText();
        if (!TryParsePoint(clip, out var point))
        {
            SetStatus("в буфере нет координат формата «x, y»");
            return;
        }

        AddWaypoint(point);
        SetStatus($"точка из буфера: ({F(point.X)}, {F(point.Y)})");
    }

    private static void AddWaypoint(Vector2 point)
    {
        var self = LocalPlayer();
        var map = self.HasValue
            ? IoCManager.Resolve<IEntityManager>().System<SharedTransformSystem>().GetMapId(self.Value)
            : MapId.Nullspace;

        Waypoints.Add(new Waypoint { World = point, Map = map });
        _selected = Waypoints.Count - 1;
        RebuildRows();
    }

    private static void SelectWaypoint(int index)
    {
        if (index < 0 || index >= Waypoints.Count)
            return;

        _selected = _selected == index ? -1 : index;
        RebuildRows();
    }

    private static void DeleteWaypoint(int index)
    {
        if (index < 0 || index >= Waypoints.Count)
            return;

        Waypoints.RemoveAt(index);
        if (_selected >= Waypoints.Count)
            _selected = Waypoints.Count - 1;
        RebuildRows();
    }

    private static void RebuildRows()
    {
        if (_rowsHost == null)
            return;

        _rowsHost.RemoveAllChildren();
        Rows.Clear();

        for (var i = 0; i < Waypoints.Count; i++)
        {
            var index = i;
            var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };

            var label = new Label
            {
                Text = "…",
                HorizontalExpand = true,
                ClipText = true,
                FontColorOverride = i == _selected ? new Color(1f, 0.85f, 0.2f) : new Color(0.85f, 0.85f, 0.85f),
            };
            row.AddChild(label);

            var select = new Button { Text = "→", ToolTip = "Выбрать точку для оверлея" };
            select.OnPressed += _ => SelectWaypoint(index);
            row.AddChild(select);

            var delete = new Button { Text = "✕", ToolTip = "Удалить точку" };
            delete.OnPressed += _ => DeleteWaypoint(index);
            row.AddChild(delete);

            _rowsHost.AddChild(row);
            Rows.Add(new RowRefs { Text = label, Index = index });
        }
    }

    // Живое обновление текста строк: расстояние и направление к точке
    // относительно экрана (верх экрана = «вверх»).
    private static void UpdateRows(EntityUid self, SharedTransformSystem transform, Angle rot)
    {
        if (Rows.Count == 0)
            return;

        var selfWorld = transform.GetWorldPosition(self);

        foreach (var row in Rows)
        {
            if (row.Index >= Waypoints.Count)
                continue;

            var wp = Waypoints[row.Index];
            var delta = wp.World - selfWorld;
            var dist = delta.Length();
            var arrow = DirectionArrow(delta, rot);
            var mark = row.Index == _selected ? "  ◀" : "";
            row.Text.Text = $"{row.Index + 1}. ({F(wp.World.X)}, {F(wp.World.Y)}) — {dist:0} м {arrow}{mark}";
        }
    }

    // Куда на экране смотреть: восемь направлений относительно камеры.
    private static string DirectionArrow(Vector2 worldDelta, Angle rot)
    {
        var rel = new Angle(-rot.Theta).RotateVec(worldDelta);
        var screen = new Vector2(rel.X, -rel.Y);

        if (screen.LengthSquared() < 0.0001f)
            return "·";

        var deg = ((MathF.Atan2(screen.Y, screen.X) * 180f / MathF.PI) + 360f) % 360f;
        var index = ((int) MathF.Round(deg / 45f)) % 8;
        return index switch
        {
            0 => "→",
            1 => "↘",
            2 => "↓",
            3 => "↙",
            4 => "←",
            5 => "↖",
            6 => "↑",
            _ => "↗",
        };
    }

    // ---------- ввод на радаре ----------

    private static void AttachInput()
    {
        if (_window == null || _radar == null)
            return;

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

        var marks = new RadarMarks(_radar);
        LayoutContainer.SetAnchorPreset(marks, LayoutContainer.LayoutPreset.Wide);
        _radar.AddChild(marks);
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

    // ---------- цикл обновления ----------

    // Каждый кадр: поворот радара за камерой и тексты панели.
    internal static void OnFrame()
    {
        _lastRotation = EyeRotation();

        if (_window is not { IsOpen: true, Disposed: false } || _state == null)
            return;

        try
        {
            // Верх радара = верх экрана. Координаты на карте (поворот карты 0),
            // поэтому общий поворот вида равен повороту камеры.
            _state.Angle = _lastRotation;
            _window.UpdateState(_state);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] сканер массы: поворот: {e.Message}");
        }

        if (LocalPlayer() is not { } self)
            return;

        var ent = IoCManager.Resolve<IEntityManager>();
        var transform = ent.System<SharedTransformSystem>();

        if (_coordsLabel != null)
        {
            var world = transform.GetWorldPosition(self) + _pan;
            _coordsLabel.Text = $"({F(world.X)}, {F(world.Y)})";
        }

        UpdateRows(self, transform, _lastRotation);
    }

    // Раз в RefreshInterval: снимок отметок и состояния радара.
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

    private static Angle EyeRotation()
        => IoCManager.Resolve<IEyeManager>().CurrentEye?.Rotation ?? Angle.Zero;

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

    // Центр радара в мировых координатах — для отметок.
    internal static Vector2? CenterWorld()
    {
        if (LocalPlayer() is not { } self)
            return null;

        var transform = IoCManager.Resolve<IEntityManager>().System<SharedTransformSystem>();
        return transform.GetWorldPosition(self) + _pan;
    }

    // Сборка состояния радара. Конструктор NavInterfaceState ищется по первым
    // четырём параметрам — они одинаковые во всех версиях; хвостовые
    // заполняем значениями по умолчанию.
    private static void PushState(EntityUid self)
    {
        if (_window == null)
            return;

        var ent = IoCManager.Resolve<IEntityManager>();
        var transform = ent.System<SharedTransformSystem>();

        var stateType = typeof(NavInterfaceState);
        var docksType = typeof(Dictionary<NetEntity, List<DockingPortState>>);

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
        args[2] = (Angle?) EyeRotation();
        args[3] = Activator.CreateInstance(docksType);
        for (var i = 4; i < args.Length; i++)
            args[i] = DefaultValue(ctor.GetParameters()[i].ParameterType);

        var created = ctor.Invoke(args);
        if (created is not NavInterfaceState state)
            return;

        // Координаты привязаны к карте (поворот карты 0), поэтому общий поворот
        // вида равен Angle — радар крутится за камерой.
        stateType.GetField("RotateWithEntity")?.SetValue(state, true);

        // В 277 радар рисует ещё и лазеры из состояния: пустой список, но не null.
        EnsureEmptyList(stateType, state, "Lasers");

        ScanMarks(self, selfMap, transform, ent);

        _state = state;
        _window.UpdateState(state);
    }

    // Отметки: игроки и мобы вокруг. Сам радар чужие сущности не рисует
    // (в состоянии только гриды), поэтому точки и линии до персонажей рисует
    // RadarMarks поверх радара.
    private static void ScanMarks(EntityUid self, MapId selfMap,
        SharedTransformSystem transform, IEntityManager ent)
    {
        Marks.Clear();

        var selfPos = transform.GetWorldPosition(self);
        var rangeSq = MaxZoom * MaxZoom;

        var found = new List<(Vector2 World, float DistSq, bool IsPlayer)>();
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

            found.Add((world, distSq, isPlayer));
        }

        // Ближайшие первыми: при переполнении капа остаются те, кто рядом.
        found.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

        foreach (var item in found)
        {
            if (Marks.Count >= MaxMarks)
                break;

            Marks.Add(new Mark { World = item.World, Player = item.IsPlayer });
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

        // Списки должны быть пустыми, а не null.
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? Activator.CreateInstance(type)
            : null;
    }
}

// Пока окно радара открыто: каждый кадр — поворот за камерой и панель,
// раз в Interval — снимок отметок.
internal sealed class RadarRefresher : Control
{
    private double _since;

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        MassScanner.OnFrame();

        _since += args.DeltaSeconds;
        if (_since >= MassScanner.RefreshInterval)
        {
            _since = 0;
            MassScanner.Refresh();
        }
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

// Отметки поверх радара: персонажи (игроки — зелёные, мобы — красные),
// 1-пиксельные линии от центра до игроков и точки-вейпоинты.
// Геометрия повторяет отрисовку MapGridControl: середина (320+4)*UIScale,
// масштаб 320*UIScale/WorldRange, ось Y экрана перевёрнута.
internal sealed class RadarMarks : Control
{
    private readonly ShuttleNavControl _radar;
    private readonly Font _font;
    private static bool _loggedError;

    public RadarMarks(ShuttleNavControl radar)
    {
        _radar = radar;
        MouseFilter = MouseFilterMode.Ignore;
        _font = new VectorFont(
            IoCManager.Resolve<IResourceCache>().GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"),
            11);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        try
        {
            DrawAll(handle);
        }
        catch (Exception e)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                Console.WriteLine($"[SsMod] сканер: отметки: {e.Message}");
            }
        }
    }

    private void DrawAll(DrawingHandleScreen handle)
    {
        if (MassScanner.CenterWorld() is not { } center)
            return;

        var rot = EyeRotation();
        var mid = new Vector2(324f, 324f) * _radar.UIScale;
        var radius = 320f * _radar.UIScale;
        var scale = radius / MathF.Max(_radar.WorldRange, 1f);

        var players = new Color(0.3f, 1f, 0.4f, 0.6f);
        var playerDot = new Color(0.3f, 1f, 0.4f);
        var mobDot = new Color(1f, 0.35f, 0.35f, 0.9f);

        // Персонажи и мобы.
        foreach (var mark in MassScanner.SnapshotMarks())
        {
            var p = Project(mark.World - center, rot, mid, scale);
            var off = p - mid;
            var len = off.Length();
            var inside = len <= radius - 4f;
            var pDraw = inside ? p : mid + off / MathF.Max(len, 0.001f) * (radius - 4f);

            if (mark.Player)
            {
                // Линия до персонажа — ровно 1 пиксель.
                handle.DrawLine(mid, pDraw, players);

                if (inside)
                    handle.DrawCircle(p, 3.5f, playerDot);
            }
            else if (inside)
            {
                handle.DrawCircle(p, 2f, mobDot);
            }
        }

        // Точки (вейпоинты).
        for (var i = 0; i < MassScanner.WaypointCount; i++)
        {
            var wp = MassScanner.GetWaypoint(i);
            if (wp == null)
                continue;

            var selected = i == MassScanner.SelectedWaypointIndex;
            var lineColor = selected ? new Color(1f, 0.9f, 0.3f, 0.95f) : new Color(1f, 0.85f, 0.2f, 0.45f);
            var dotColor = selected ? new Color(1f, 0.9f, 0.3f) : new Color(1f, 0.85f, 0.2f, 0.85f);

            var p = Project(wp.World - center, rot, mid, scale);
            var off = p - mid;
            var len = off.Length();
            var inside = len <= radius - 4f;
            var pDraw = inside ? p : mid + off / MathF.Max(len, 0.001f) * (radius - 4f);

            // Линия до точки — тоже 1 пиксель.
            handle.DrawLine(mid, pDraw, lineColor);

            if (inside)
                DrawDiamond(handle, p, selected ? 6f : 4.5f, dotColor);

            var text = $"{(wp.World - center).Length():0} м";
            Compat.DrawText(handle, _font, pDraw + new Vector2(8f, -10f), text, 1f,
                selected ? dotColor : lineColor);
        }

        // Центр — свой персонаж: радар уже рисует зелёный клин, добавим точку.
        handle.DrawCircle(mid, 2f, Color.Lime);
    }

    private void DrawDiamond(DrawingHandleScreen handle, Vector2 p, float r, Color color)
    {
        Span<Vector2> verts = stackalloc Vector2[4]
        {
            p + new Vector2(0f, -r),
            p + new Vector2(r, 0f),
            p + new Vector2(0f, r),
            p + new Vector2(-r, 0f),
        };
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, verts, color);
    }

    private static Vector2 Project(Vector2 rel, Angle rot, Vector2 mid, float scale)
    {
        var v = new Angle(-rot.Theta).RotateVec(rel);
        return mid + new Vector2(v.X * scale, -v.Y * scale);
    }

    private Angle EyeRotation()
        => IoCManager.Resolve<IEyeManager>().CurrentEye?.Rotation ?? Angle.Zero;
}

// Оверлей поверх игры: палка или стрелка от персонажа в сторону выбранной
// точки, с дистанцией в метрах.
internal sealed class ScannerMarkOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private readonly Font _font;
    private static bool _loggedError;

    public ScannerMarkOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(
            IoCManager.Resolve<IResourceCache>().GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"),
            12);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            DrawMark(args);
        }
        catch (Exception e)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                Console.WriteLine($"[SsMod] сканер: оверлей: {e.Message}");
            }
        }
    }

    private void DrawMark(in OverlayDrawArgs args)
    {
        if (!MassScanner.OverlayEnabled)
            return;

        var wp = MassScanner.SelectedWaypoint;
        if (wp == null)
            return;

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        var transform = _ent.System<SharedTransformSystem>();
        if (transform.GetMapId(self) != wp.Map)
            return;

        var selfWorld = transform.GetWorldPosition(self);
        var selfS = CombatVisuals.Screen(args, selfWorld);
        var wpS = CombatVisuals.Screen(args, wp.World);

        var delta = wpS - selfS;
        var len = delta.Length();
        if (len < 2f)
            return;

        var dir = delta / len;
        var end = selfS + dir * MassScanner.OverlayLength;

        var handle = args.ScreenHandle;
        var color = new Color(1f, 0.85f, 0.2f, 0.9f);

        if (MassScanner.OverlayWidth <= 1.01f)
        {
            // Палка в 1 пиксель.
            handle.DrawLine(selfS, end, color);
        }
        else
        {
            CombatVisuals.ThickLine(handle, selfS, end, MassScanner.OverlayWidth, color);
        }

        if (MassScanner.OverlayMode == MassScanner.MarkMode.Arrow)
        {
            // Наконечник — треугольник на конце палки.
            var perp = new Vector2(-dir.Y, dir.X);
            var tip = end + dir * 9f;
            Span<Vector2> head = stackalloc Vector2[3]
            {
                tip,
                end + perp * 5f,
                end - perp * 5f,
            };
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, head, color);
        }

        if (MassScanner.OverlayMeters)
        {
            var dist = (wp.World - selfWorld).Length();
            var pos = end + dir * 10f;
            Compat.DrawText(handle, _font, pos + new Vector2(1f, 1f), $"{dist:0} м", 1f, new Color(0f, 0f, 0f, 0.7f));
            Compat.DrawText(handle, _font, pos, $"{dist:0} м", 1f, color);
        }
    }
}
