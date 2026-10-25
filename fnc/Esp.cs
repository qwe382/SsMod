using System.Numerics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Настройки одной полоски (HP или стамина), они у каждого типа свои.
public sealed class EspBar
{
    public bool Enabled = true;

    public float Width = 40f;
    public float Height = 4f;

    // Смещение в пикселях от нижнего центра цели.
    public Vector2 Offset = new(0f, 3f);

    public bool ShowValue;
    public bool Gradient = true;
    public bool Border;

    public Color Color = new Color(0.2f, 1f, 0.2f);
    public Color Background = new Color(0f, 0f, 0f, 0.6f);
}

// Настройки одной категории целей ESP (друг/враг/нейтрал/труп).
public sealed class EspCategory
{
    public string Title = "";

    public bool Enabled = true;
    public Color Color = Color.White;

    public bool NameText = true;
    public bool Distance;
    public bool Arrow = true;

    // Профессия цели (с её ID-карты) — строкой над именем.
    public bool JobText;
    public float JobScale = 0.85f;
    public Vector2 JobOffset;

    // Множитель габаритов цели и смещение её центра в тайлах.
    public float Scale = 1f;
    public Vector2 Offset;

    // Масштаб текста и его смещение в пикселях.
    public float NameScale = 1f;
    public Vector2 NameOffset;

    // Размер стрелки (для целей вне экрана).
    public float ArrowScale = 1f;

    // Полоски — у каждого типа свои, независимо от рамки.
    public EspBar HealthBar = new();
    public EspBar StaminaBar = new()
    {
        Height = 3f,
        Offset = new Vector2(0f, 10f),
        Gradient = false,
        Color = new Color(0.3f, 0.7f, 1f),
    };
}

// Все настройки ESP. Оверлей читает их каждый кадр.
public static class EspSettings
{
    public static bool Enabled;

    // Общее.
    public static bool AliveOnly = true;
    public static bool PlayersOnly;
    public static float MaxDistance;

    public static readonly EspCategory Friend = new()
    {
        Title = "Друг",
        Color = new Color(0.2f, 1f, 0.35f),
    };

    public static readonly EspCategory Enemy = new()
    {
        Title = "Враг",
        Color = new Color(1f, 0.25f, 0.25f),
    };

    public static readonly EspCategory Neutral = new()
    {
        Title = "Нейтрал",
        Color = new Color(1f, 0.9f, 0.3f),
    };

    public static readonly EspCategory Dead = new()
    {
        Title = "Труп",
        Color = new Color(0.6f, 0.6f, 0.6f),
    };
}

public sealed class EspOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // Систему берём лениво: в EntryPoint.PostInit IEntityManager.System<T>()
    // падает с NRE — системы движка ещё не инициализированы, а оверлеи
    // создаются именно там (стартовый конфиг).
    private SharedTransformSystem? _transformCache;
    private SpriteSystem? _spriteCache;
    private SharedTransformSystem Transform => _transformCache ??= _ent.System<SharedTransformSystem>();
    private SpriteSystem Sprite => _spriteCache ??= _ent.System<SpriteSystem>();
    private readonly Font _font;

    private readonly List<EntityUid> _mobs = new();

    public EspOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(
            _cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            DrawEsp(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] esp draw failed: {e.Message}");
        }
    }

    private void DrawEsp(in OverlayDrawArgs args)
    {
        if (args.Viewport.Eye is not { } eye)
            return;

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        var mapId = eye.Position.MapId;
        var selfPos = Transform.GetWorldPosition(self);
        var handle = args.ScreenHandle;

        // Рисуем в координатах DrawingHandle, поэтому видимая область — от нуля
        // до размера вьюпорта (см. CombatVisuals.Screen: он же переводит мировые
        // точки в эти координаты, учитывая масштаб и положение вьюпорта).
        var boundsSize = (Vector2) args.ViewportBounds.Size;
        var vp = new RectF(0f, 0f, boundsSize.X, boundsSize.Y);

        _mobs.Clear();
        var query = _ent.AllEntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out _))
        {
            if (uid != self)
                _mobs.Add(uid);
        }

        foreach (var uid in _mobs)
        {
            if (!_ent.TryGetComponent(uid, out MobStateComponent? mob))
                continue;

            if (!_ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.Deleted)
                continue;

            if (EspSettings.AliveOnly && Compat.GetMobState(mob) != MobState.Alive)
                continue;

            if (EspSettings.PlayersOnly && !_ent.HasComponent<ActorComponent>(uid))
                continue;

            if (Transform.GetMapCoordinates(uid).MapId != mapId)
                continue;

            var world = Transform.GetWorldPosition(uid);
            var distance = (world - selfPos).Length();
            if (EspSettings.MaxDistance > 0f && distance > EspSettings.MaxDistance)
                continue;

            var category = CategoryOf(uid, mob);
            if (!category.Enabled)
                continue;

            var screen = CombatVisuals.Screen(args, world);

            // Цель вне экрана — стрелка на краю в её сторону.
            if (!vp.Contains(screen))
            {
                if (category.Arrow)
                    DrawArrow(handle, vp, screen, category);
                continue;
            }

            DrawTarget(handle, args, uid, meta, category, world, distance);
        }
    }

    private static EspCategory CategoryOf(EntityUid uid, MobStateComponent mob)
    {
        if (Compat.GetMobState(mob) == MobState.Dead)
            return EspSettings.Dead;

        return Priorities.Get(uid) switch
        {
            Priority.Friend => EspSettings.Friend,
            Priority.Enemy => EspSettings.Enemy,
            _ => EspSettings.Neutral,
        };
    }

    private void DrawTarget(
        DrawingHandleScreen handle,
        in OverlayDrawArgs args,
        EntityUid uid,
        MetaDataComponent meta,
        EspCategory category,
        Vector2 world,
        float distance)
    {
        var size = new Vector2(0.5f, 1f);
        if (_ent.TryGetComponent(uid, out SpriteComponent? sprite) && sprite.Visible)
        {
            var bounds = Sprite.GetLocalBounds((uid, sprite));
            if (bounds.Width > 0f && bounds.Height > 0f)
                size = bounds.Size;
        }

        size *= MathF.Max(category.Scale, 0.01f);
        var worldCenter = world + category.Offset;

        // Габариты цели на экране — от них позиционируются полоски и имя.
        var box = Rect(
            CombatVisuals.Screen(args, worldCenter + new Vector2(-size.X / 2f, size.Y / 2f)),
            CombatVisuals.Screen(args, worldCenter + new Vector2(size.X / 2f, -size.Y / 2f)));

        // Полоски не зависят от рамки, позиция — от нижнего центра цели.
        var anchor = new Vector2((Compat.BoxLeft(box) + Compat.BoxRight(box)) / 2f, Compat.BoxBottom(box));

        if (TryGetBar(uid, health: true, out var hpRatio, out var hpValue))
            DrawBar(handle, anchor, category.HealthBar, hpRatio, hpValue);

        if (TryGetBar(uid, health: false, out var staminaRatio, out var staminaValue))
            DrawBar(handle, anchor, category.StaminaBar, staminaRatio, staminaValue);

        if (category.NameText)
        {
            var scale = MathF.Max(category.NameScale, 0.1f);
            var textPos = new Vector2(
                (Compat.BoxLeft(box) + Compat.BoxRight(box)) / 2f - meta.EntityName.Length * 5.5f * scale + category.NameOffset.X,
                Compat.BoxTop(box) - _font.GetLineHeight(scale) - 3f + category.NameOffset.Y);
            Compat.DrawText(handle, _font, textPos, meta.EntityName, scale, category.Color.WithAlpha(0.95f));
        }

        if (category.JobText)
        {
            var job = Jobs.Of(_ent, uid, _timing.CurTime, out var reason);
            var scale = MathF.Max(category.JobScale, 0.1f);

            // Профессия — строкой выше имени, чтобы её включение не сдвигало имя.
            var top = Compat.BoxTop(box) - _font.GetLineHeight(scale) - 3f;

            if (category.NameText)
                top -= _font.GetLineHeight(MathF.Max(category.NameScale, 0.1f)) + 2f;

            // Профессии нет — вместо неё серая причина, чтобы было видно, где
            // обрывается поиск (карточки нет, карточка пустая, прототип пуст).
            var text = job ?? $"проф: {reason}";
            var color = job != null ? category.Color.WithAlpha(0.8f) : new Color(0.6f, 0.6f, 0.6f, 0.85f);

            var textPos = new Vector2(
                (Compat.BoxLeft(box) + Compat.BoxRight(box)) / 2f - text.Length * 5.5f * scale + category.JobOffset.X,
                top + category.JobOffset.Y);

            Compat.DrawText(handle, _font, textPos, text, scale, color);
        }

        if (category.Distance)
        {
            var text = $"{distance:0.0} м";
            var textPos = new Vector2(
                (Compat.BoxLeft(box) + Compat.BoxRight(box)) / 2f - text.Length * 5.5f * category.NameScale + category.NameOffset.X,
                Compat.BoxBottom(box) + 20f + category.NameOffset.Y);
            Compat.DrawText(handle, _font, textPos, text, category.NameScale, category.Color.WithAlpha(0.95f));
        }
    }

    private void DrawBar(DrawingHandleScreen handle, Vector2 anchor, EspBar bar, float ratio, float value)
    {
        if (!bar.Enabled)
            return;

        ratio = Math.Clamp(ratio, 0f, 1f);

        var width = MathF.Max(bar.Width, 2f);
        var height = MathF.Max(bar.Height, 1f);
        var left = anchor.X - width / 2f + bar.Offset.X;
        var top = anchor.Y + bar.Offset.Y;

        var box = new UIBox2(left, top, left + width, top + height);

        handle.DrawRect(box, bar.Background);

        var color = bar.Gradient ? new Color(1f - ratio, ratio, 0.15f) : bar.Color;
        handle.DrawRect(new UIBox2(left, top, left + width * ratio, top + height), color.WithAlpha(0.95f));

        if (bar.Border)
            handle.DrawRect(box, Color.Black.WithAlpha(0.9f), false);

        if (bar.ShowValue)
        {
            var text = $"{(int) value}";
            const float scale = 0.8f;
            var textPos = new Vector2(left + width / 2f - text.Length * 5.5f * scale, top - height - 1f);
            Compat.DrawText(handle, _font, textPos, text, scale, Color.White.WithAlpha(0.95f));
        }
    }

    // Текущее значение и доля для HP или стамины. Считает Vitals — то же самое
    // показывает фрейм «Персонажи», второй копии расчёта нет.
    private bool TryGetBar(EntityUid uid, bool health, out float ratio, out float value)
    {
        ratio = 1f;
        value = 0f;

        if (health)
        {
            if (!Vitals.Health(_ent, uid, out var hp, out var hpMax))
                return false;

            value = hp;
            ratio = Math.Clamp(hp / hpMax, 0f, 1f);
            return true;
        }

        if (!Vitals.Stamina(_ent, uid, out var stamina, out var staminaMax))
            return false;

        value = stamina;
        ratio = Math.Clamp(stamina / staminaMax, 0f, 1f);
        return true;
    }

    // Стрелка на краю экрана в сторону цели, которая за кадром.
    private static void DrawArrow(DrawingHandleScreen handle, RectF view, Vector2 target, EspCategory category)
    {
        const float margin = 26f;

        var center = new Vector2((view.Left + view.Right) / 2f, (view.Top + view.Bottom) / 2f);
        var half = new Vector2(
            MathF.Max((view.Right - view.Left) / 2f - margin, 1f),
            MathF.Max((view.Bottom - view.Top) / 2f - margin, 1f));

        var dir = target - center;
        if (dir.LengthSquared() < 0.0001f)
            dir = new Vector2(0f, -1f);

        var t = float.MaxValue;
        if (MathF.Abs(dir.X) > 0.0001f)
            t = MathF.Min(t, half.X / MathF.Abs(dir.X));
        if (MathF.Abs(dir.Y) > 0.0001f)
            t = MathF.Min(t, half.Y / MathF.Abs(dir.Y));
        if (t == float.MaxValue)
            t = 0f;

        var tip = center + dir * t;
        var step = dir / dir.Length();

        var size = 14f * MathF.Max(category.ArrowScale, 0.1f);
        var perp = new Vector2(-step.Y, step.X) * (size * 0.55f);

        Span<Vector2> tri = stackalloc Vector2[3];
        tri[0] = tip + step * (size * 0.4f);
        tri[1] = tip - step * size + perp;
        tri[2] = tip - step * size - perp;

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, tri, category.Color.WithAlpha(0.85f));
    }

    private static UIBox2 Rect(Vector2 a, Vector2 b)
    {
        return new UIBox2(
            MathF.Min(a.X, b.X),
            MathF.Min(a.Y, b.Y),
            MathF.Max(a.X, b.X),
            MathF.Max(a.Y, b.Y));
    }

    private readonly struct RectF
    {
        public readonly float Left;
        public readonly float Top;
        public readonly float Right;
        public readonly float Bottom;

        public RectF(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public bool Contains(Vector2 p)
        {
            return p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
        }
    }
}

// Вкладка настроек ESP в окне мода.
public static class Esp
{
    public static Control BuildTab()
    {
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var enable = new CheckBox { Text = "Включить ESP", Pressed = EspSettings.Enabled };
        enable.OnToggled += args => Toggle(args.Pressed);
        root.AddChild(enable);

        var generalGrid = Grid(
            ("Макс. дистанция", DistanceControl()));

        root.AddChild(Section("Общее", Body(
            Check("Только живые", EspSettings.AliveOnly, v => EspSettings.AliveOnly = v),
            Check("Только игроки", EspSettings.PlayersOnly, v => EspSettings.PlayersOnly = v),
            generalGrid), expanded: true));

        root.AddChild(CategorySection(EspSettings.Friend));
        root.AddChild(CategorySection(EspSettings.Enemy));
        root.AddChild(CategorySection(EspSettings.Neutral));
        root.AddChild(CategorySection(EspSettings.Dead));

        return root;
    }

    public static void Toggle(bool on)
    {
        EspSettings.Enabled = on;

        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (!overlays.HasOverlay<EspOverlay>())
                overlays.AddOverlay(new EspOverlay());
        }
        else
        {
            overlays.RemoveOverlay<EspOverlay>();
        }

        Console.WriteLine($"[SsMod] esp = {on}");
    }

    private static Collapsible CategorySection(EspCategory category)
    {
        var grid = Grid(
            ("Масштаб ×", Spin(category.Scale, 0.05f, 2, v => category.Scale = v)),
            ("Офсет X", Spin(category.Offset.X, 0.05f, 2, v => category.Offset = new Vector2(v, category.Offset.Y))),
            ("Офсет Y", Spin(category.Offset.Y, 0.05f, 2, v => category.Offset = new Vector2(category.Offset.X, v))),
            ("Имя ×", Spin(category.NameScale, 0.05f, 2, v => category.NameScale = v)),
            ("Имя X", Spin(category.NameOffset.X, 1f, 0, v => category.NameOffset = new Vector2(v, category.NameOffset.Y))),
            ("Имя Y", Spin(category.NameOffset.Y, 1f, 0, v => category.NameOffset = new Vector2(category.NameOffset.X, v))),
            ("Проф. ×", Spin(category.JobScale, 0.05f, 2, v => category.JobScale = v)),
            ("Проф. X", Spin(category.JobOffset.X, 1f, 0, v => category.JobOffset = new Vector2(v, category.JobOffset.Y))),
            ("Проф. Y", Spin(category.JobOffset.Y, 1f, 0, v => category.JobOffset = new Vector2(category.JobOffset.X, v))),
            ("Стрелка ×", Spin(category.ArrowScale, 0.1f, 2, v => category.ArrowScale = v)));

        return Section(category.Title, Body(
            Check("Показывать", category.Enabled, v => category.Enabled = v),
            Check("Имя", category.NameText, v => category.NameText = v),
            Check("Профессия", category.JobText, v => category.JobText = v),
            Check("Дистанция", category.Distance, v => category.Distance = v),
            Check("Стрелка (вне экрана)", category.Arrow, v => category.Arrow = v),
            grid,
            new Label { Text = "Цвет имени" },
            ColorControl(category.Color, c => category.Color = c),
            BarSection("Полоска HP", category.HealthBar),
            BarSection("Полоска стамины", category.StaminaBar)));
    }

    private static Collapsible BarSection(string title, EspBar bar)
    {
        var grid = Grid(
            ("Ширина", Spin(bar.Width, 2f, 0, v => bar.Width = v)),
            ("Высота", Spin(bar.Height, 1f, 0, v => bar.Height = v)),
            ("Офсет X", Spin(bar.Offset.X, 2f, 0, v => bar.Offset = new Vector2(v, bar.Offset.Y))),
            ("Офсет Y", Spin(bar.Offset.Y, 2f, 0, v => bar.Offset = new Vector2(bar.Offset.X, v))));

        return Section(title, Body(
            Check("Показывать", bar.Enabled, v => bar.Enabled = v),
            grid,
            Check("Значение", bar.ShowValue, v => bar.ShowValue = v),
            Check("Градиент цвета", bar.Gradient, v => bar.Gradient = v),
            Check("Обводка", bar.Border, v => bar.Border = v),
            new Label { Text = "Цвет" },
            ColorControl(bar.Color, c => bar.Color = c),
            new Label { Text = "Фон" },
            ColorControl(bar.Background, c => bar.Background = c)));
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

    private static Control DistanceControl()
    {
        var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, HorizontalExpand = true };
        var label = new Label { Text = DistanceText(), MinWidth = 70f };
        var slider = new Slider
        {
            MinValue = 0f,
            MaxValue = 50f,
            Value = EspSettings.MaxDistance,
            MinWidth = 120f,
            HorizontalExpand = true,
        };
        slider.OnValueChanged += _ =>
        {
            EspSettings.MaxDistance = slider.Value;
            label.Text = DistanceText();
        };

        row.AddChild(slider);
        row.AddChild(label);
        return row;
    }

    private static string DistanceText()
    {
        return EspSettings.MaxDistance <= 0f ? "без лимита" : $"{EspSettings.MaxDistance:0} м";
    }
}
