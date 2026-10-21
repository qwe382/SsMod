using System.Numerics;
using System.Text;
using Content.Shared.Hands.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Настройки визуалов мода. Живут в памяти, как и остальные настройки:
// вкладка «Визуалы» меняет поля, оверлеи читают их каждый кадр.
public static class VisualSettings
{
    // Дуга патронов у курсора.
    public static bool AmmoArc = true;
    public static bool AmmoText = true;
    public static float AmmoRadius = 42f;
    public static float AmmoThickness = 5f;
    public static Color AmmoColor = new(0.4f, 1f, 0.5f);
    public static Color AmmoEmptyColor = new(1f, 0.3f, 0.3f);
    public static float AmmoOffsetY = 0f;

    // Линия до цели и дуги вокруг неё (мелбот/gun-bot).
    public static bool TraceLine = true;
    public static bool TraceArcs = true;
    public static float TraceWidth = 2f;
    public static float TraceArcWidth = 12f;

    // Трейсы пуль: след снаряда. Цвет — по стрелявшему.
    public static bool Traces = true;
    public static float TracesWidth = 1.5f;
    public static float TracesLifetime = 0.35f; // сколько секунд висит след
    public static Color TracesFriend = new(0.3f, 1f, 0.4f, 0.7f);
    public static Color TracesNeutral = new(0.35f, 0.6f, 1f, 0.6f);
    public static Color TracesEnemy = new(1f, 0.3f, 0.3f, 0.75f);
    public static Color TracesUnknown = new(1f, 0.2f, 0.2f, 0.8f); // турели и прочее без владельца
}

// Дуга у курсора: сколько патронов осталось в стволе, и рядом текст
// «в стволе / ёмкость магазина / всего в магазинах инвентаря».
public sealed class AmmoOverlay : Overlay
{
    // Раствор дуги: 270° начиная с левого низу — как у индикаторов заряда.
    private const float StartAngle = MathF.PI * 0.75f;
    private const float Sweep = MathF.PI * 1.5f;
    private const int Segments = 40;

    private readonly Font _font;

    public AmmoOverlay()
    {
        // Шрифт берём тот же, что у ESP, — он уже работает во всех версиях.
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(
            cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!VisualSettings.AmmoArc)
            return;

        try
        {
            DrawAmmo(args);
        }
        catch (Exception e)
        {
            // Оверлей рисуется каждый кадр: исключение завалило бы рендер.
            Console.Error.WriteLine($"[SsMod] дуга патронов: {e.Message}");
        }
    }

    private void DrawAmmo(in OverlayDrawArgs args)
    {
        var ent = IoCManager.Resolve<IEntityManager>();
        var player = IoCManager.Resolve<IPlayerManager>();

        if (player.LocalEntity is not { Valid: true } self)
            return;

        // Только когда в руке ствол: у ближнего боя патронов нет.
        if (!AttackReach.TryGetGun(ent, self, out var gun))
            return;

        // Всё в том же формате, что показывает клиентский UI оружия.
        var (loaded, max) = GunApi.LoadedAmmo(ent, gun);
        if (loaded < 0 || max <= 0)
            return;

        var total = GunApi.InventoryAmmo(ent, self, gun);

        var handle = args.ScreenHandle;
        var center = CombatVisuals.CursorScreen(args, out _) + new Vector2(0f, VisualSettings.AmmoOffsetY);

        var radius = MathF.Max(VisualSettings.AmmoRadius, 6f);
        var width = MathF.Max(VisualSettings.AmmoThickness, 1f);

        // Фон: вся дуга еле видной подложкой, чтобы шкала читалась и на пустом.
        Arc(handle, center, radius, StartAngle, Sweep, width, new Color(0f, 0f, 0f, 0.45f));

        var ratio = Math.Clamp(loaded / (float) max, 0f, 1f);
        var color = Color.InterpolateBetween(VisualSettings.AmmoEmptyColor, VisualSettings.AmmoColor, ratio);
        Arc(handle, center, radius, StartAngle, Sweep * ratio, width, color);

        if (!VisualSettings.AmmoText)
            return;

        var text = $"{loaded}/{max}/{total}";
        const float scale = 1.1f;
        var size = TextWidth(text, scale);
        var pos = new Vector2(center.X - size / 2f, center.Y - radius - _font.GetLineHeight(scale) - 4f);

        // Тень, чтобы читалось и на светлом полу.
        Compat.DrawText(handle, _font, pos + new Vector2(1f, 1f), text, scale, new Color(0f, 0f, 0f, 0.7f));
        Compat.DrawText(handle, _font, pos, text, scale, Color.White.WithAlpha(0.95f));
    }

    private static void Arc(DrawingHandleScreen handle, Vector2 center, float radius, float start, float sweep,
        float width, Color color)
    {
        if (sweep <= 0.0001f)
            return;

        var steps = Math.Max(2, (int) (Segments * (sweep / MathF.Tau)));

        for (var i = 0; i < steps; i++)
        {
            var a0 = start + sweep * i / steps;
            var a1 = start + sweep * (i + 1) / steps;
            CombatVisuals.ThickLine(handle, Point(center, a0, radius), Point(center, a1, radius), width, color);
        }
    }

    private static Vector2 Point(Vector2 center, float angle, float radius)
        => center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

    private float TextWidth(string text, float scale)
    {
        var width = 0f;

        foreach (var rune in text.EnumerateRunes())
        {
            if (_font.TryGetCharMetrics(rune, scale, out var metrics))
                width += metrics.Advance;
        }

        return width;
    }
}

// Трейсы пуль: след снаряда от его прошлой позиции до текущей. Цвет — по
// стрелявшему: свои и друзья зелёные, нейтралы синие, враги красные.
public sealed class TracesOverlay : Overlay
{
    // Длиннее этого след не тянем: снаряд всё равно за кадр столько не летит,
    // а при телепорте/лаге получилась бы линия через полкарты.
    private const float MaxTrailTiles = 4f;

    // Больше этого числа отрезков не держим: очередь боя с дробовиками иначе
    // раздувала бы список до бесконечности.
    private const int MaxSegments = 4096;

    // Прошлая позиция снаряда. Записи без обновления дольше секунды выкидываем:
    // снаряд уже улетел или удалился.
    private readonly Dictionary<EntityUid, (Vector2 Pos, TimeSpan At)> _trail = new();

    // Отрезки следа со временем жизни: снаряд умирает сразу после попадания,
    // а след должен ещё повисеть столько, сколько задано в настройках.
    private readonly List<Segment> _segments = new();

    private readonly struct Segment
    {
        public readonly Vector2 From;
        public readonly Vector2 To;
        public readonly Color Color;
        public readonly TimeSpan Until;

        public Segment(Vector2 from, Vector2 to, Color color, TimeSpan until)
        {
            From = from;
            To = to;
            Color = color;
            Until = until;
        }
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!VisualSettings.Traces)
            return;

        try
        {
            DrawTraces(args);
        }
        catch (Exception e)
        {
            // Оверлей рисуется каждый кадр: исключение завалило бы рендер.
            Console.Error.WriteLine($"[SsMod] трейсы: {e.Message}");
        }
    }

    private void DrawTraces(in OverlayDrawArgs args)
    {
        var ent = IoCManager.Resolve<IEntityManager>();

        if (IoCManager.Resolve<IPlayerManager>().LocalEntity is not { Valid: true } self)
            return;

        if (args.Viewport.Eye is not { } eye)
            return;

        var transform = ent.System<SharedTransformSystem>();
        var map = eye.Position.MapId;
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        var handle = args.ScreenHandle;
        var width = MathF.Max(VisualSettings.TracesWidth, 0.5f);
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(VisualSettings.TracesLifetime, 0.05f, 5f));

        // Просроченные следы убираем до обхода: снаряд давно умер, а след уже
        // должен был погаснуть.
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            if (_segments[i].Until <= now)
                _segments.RemoveAt(i);
        }

        // Чистим хвосты до обхода, чтобы словарь не рос вместе с числом снарядов.
        if (_trail.Count > 64)
        {
            foreach (var (uid, value) in _trail.ToList())
            {
                if (now - value.At > TimeSpan.FromSeconds(1))
                    _trail.Remove(uid);
            }
        }

        var query = ent.AllEntityQueryEnumerator<ProjectileComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var projectile, out _))
        {
            if (transform.GetMapId(uid) != map)
                continue;

            var pos = transform.GetWorldPosition(uid);
            var had = _trail.TryGetValue(uid, out var last);
            _trail[uid] = (pos, now);

            // Первый кадр снаряда: следа ещё нет, но направление полёта уже
            // известно — добавляем короткий отрезок назад по углу.
            var delta = had ? pos - last.Pos : -projectile.Angle.ToVec() * 0.75f;
            var length = delta.Length();
            if (length < 0.05f)
                continue;

            if (length > MaxTrailTiles)
                delta = delta / length * MaxTrailTiles;

            _segments.Add(new Segment(pos - delta, pos, ColorOf(ent, projectile, self), now + lifetime));
        }

        // Переполнение: старые отрезки вперёд, их и выбрасываем.
        if (_segments.Count > MaxSegments)
            _segments.RemoveRange(0, _segments.Count - MaxSegments);

        foreach (var segment in _segments)
        {
            CombatVisuals.ThickLine(handle,
                CombatVisuals.Screen(args, segment.From),
                CombatVisuals.Screen(args, segment.To),
                width, segment.Color);
        }
    }

    private static Color ColorOf(IEntityManager ent, ProjectileComponent projectile, EntityUid self)
    {
        // Стрелявшего нет (ловушки, турели без владельца) — неизвестный, красный.
        if (projectile.Shooter is not { } shooter)
            return VisualSettings.TracesUnknown;

        if (shooter == self)
            return VisualSettings.TracesFriend;

        // Не персонаж (турель и прочая техника) — тоже неизвестный.
        if (!ent.HasComponent<MobStateComponent>(shooter))
            return VisualSettings.TracesUnknown;

        return Priorities.Get(shooter) switch
        {
            Priority.Friend => VisualSettings.TracesFriend,
            Priority.Enemy => VisualSettings.TracesEnemy,
            _ => VisualSettings.TracesNeutral,
        };
    }
}

// Фрейм «Визуалы»: свои вкладки — gun-bot, дуга патронов и трейсы пуль.
public static class Visuals
{
    public static Control BuildTab()
    {
        var tabs = new TabContainer();

        var bot = GunBot.BuildTab();
        tabs.AddChild(bot);
        TabContainer.SetTabTitle(bot, "Gun-bot");

        var visuals = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        visuals.AddChild(AmmoSection());
        visuals.AddChild(CombatLineSection());
        tabs.AddChild(visuals);
        TabContainer.SetTabTitle(visuals, "Дуга патронов");

        var traces = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        traces.AddChild(TracesSection());
        tabs.AddChild(traces);
        TabContainer.SetTabTitle(traces, "Трейсы");

        return tabs;
    }

    private static Control TracesSection()
    {
        var grid = Grid(
            ("Толщина, px", Spin(VisualSettings.TracesWidth, 0.5f, 1, v => VisualSettings.TracesWidth = v)),
            ("Длительность, с", Spin(VisualSettings.TracesLifetime, 0.05f, 2, v => VisualSettings.TracesLifetime = v)));

        return Section("Трейсы пуль", Body(
            Check("Показывать трейсы", VisualSettings.Traces, v => VisualSettings.Traces = v),
            grid,
            new Label { Text = "Свои и друзья" },
            ColorControl(VisualSettings.TracesFriend, c => VisualSettings.TracesFriend = c),
            new Label { Text = "Нейтралы" },
            ColorControl(VisualSettings.TracesNeutral, c => VisualSettings.TracesNeutral = c),
            new Label { Text = "Враги" },
            ColorControl(VisualSettings.TracesEnemy, c => VisualSettings.TracesEnemy = c),
            new Label { Text = "Неизвестные (турели)" },
            ColorControl(VisualSettings.TracesUnknown, c => VisualSettings.TracesUnknown = c)),
            expanded: true);
    }

    private static Control AmmoSection()
    {
        var grid = Grid(
            ("Радиус, px", Spin(VisualSettings.AmmoRadius, 2f, 0, v => VisualSettings.AmmoRadius = v)),
            ("Толщина, px", Spin(VisualSettings.AmmoThickness, 1f, 0, v => VisualSettings.AmmoThickness = v)),
            ("Сдвиг по Y", Spin(VisualSettings.AmmoOffsetY, 2f, 0, v => VisualSettings.AmmoOffsetY = v)));

        return Section("Дуга патронов у курсора", Body(
            Check("Показывать дугу", VisualSettings.AmmoArc, v => VisualSettings.AmmoArc = v),
            Check("Текст (ствол/магазин/инвентарь)", VisualSettings.AmmoText, v => VisualSettings.AmmoText = v),
            grid,
            new Label { Text = "Цвет заполнения" },
            ColorControl(VisualSettings.AmmoColor, c => VisualSettings.AmmoColor = c),
            new Label { Text = "Цвет на нуле" },
            ColorControl(VisualSettings.AmmoEmptyColor, c => VisualSettings.AmmoEmptyColor = c)),
            expanded: true);
    }

    private static Control CombatLineSection()
    {
        var grid = Grid(
            ("Линия, px", Spin(VisualSettings.TraceWidth, 1f, 0, v => VisualSettings.TraceWidth = v)),
            ("Дуги, px", Spin(VisualSettings.TraceArcWidth, 1f, 0, v => VisualSettings.TraceArcWidth = v)));

        return Section("Линия и дуги боя", Body(
            Check("Линия до цели", VisualSettings.TraceLine, v => VisualSettings.TraceLine = v),
            Check("Дуги вокруг цели", VisualSettings.TraceArcs, v => VisualSettings.TraceArcs = v),
            grid));
    }

    private static Collapsible Section(string title, CollapsibleBody body, bool expanded = false)
    {
        return new Collapsible(title, body) { BodyVisible = expanded };
    }

    private static CollapsibleBody Body(params Control[] children)
    {
        var content = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        foreach (var child in children)
            content.AddChild(child);

        var body = new CollapsibleBody();
        body.AddChild(content);
        return body;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var check = new CheckBox { Text = text, Pressed = value };
        check.OnToggled += args => set(args.Pressed);
        return check;
    }

    private static FloatSpinBox Spin(float value, float step, byte precision, Action<float> set)
    {
        var spin = new FloatSpinBox(step, precision) { Value = value, HorizontalExpand = true };
        spin.OnValueChanged += args => set(args.Value);
        return spin;
    }

    private static GridContainer Grid(params (string Label, Control Control)[] rows)
    {
        var grid = new GridContainer { Columns = 2 };
        foreach (var (label, control) in rows)
        {
            grid.AddChild(new Label { Text = label, MinWidth = 110f });
            grid.AddChild(control);
        }

        return grid;
    }

    private static Control ColorControl(Color color, Action<Color> set)
    {
        var sliders = new ColorSelectorSliders
        {
            Color = color,
            IsAlphaVisible = true,
            HorizontalExpand = true,
        };
        sliders.OnColorChanged = set;
        return sliders;
    }
}
