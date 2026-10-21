using System.Numerics;
using System.Text;
using Content.Shared.Hands.Components;
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

    // Трейсы: линия от персонажа до цели и дуги вокруг цели (мелбот/gun-bot).
    public static bool TraceLine = true;
    public static bool TraceArcs = true;
    public static float TraceWidth = 2f;
    public static float TraceArcWidth = 12f;
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

// Вкладка «Визуалы»: настройки дуги патронов, трейсов и вынесенный сюда gun-bot.
public static class Visuals
{
    public static Control BuildTab()
    {
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        root.AddChild(AmmoSection());
        root.AddChild(TraceSection());

        // Gun-bot целиком переехал сюда: наводка, авто-стрельба, перезарядка,
        // передёргивание и снятие меток.
        root.AddChild(Section("Gun-bot", Body(GunBot.BuildTab()), expanded: true));

        return root;
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

    private static Control TraceSection()
    {
        var grid = Grid(
            ("Линия, px", Spin(VisualSettings.TraceWidth, 1f, 0, v => VisualSettings.TraceWidth = v)),
            ("Дуги, px", Spin(VisualSettings.TraceArcWidth, 1f, 0, v => VisualSettings.TraceArcWidth = v)));

        return Section("Трейсы", Body(
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
