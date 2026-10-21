using System.Numerics;
using Content.Shared.CombatMode;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Роутер боя: ближний / дальний. Работает всегда, без галочки.
//
// Признак один — что лежит в активной руке:
//   * взял ствол (GunComponent) — мелбот выключается целиком (ни цели, ни
//     линии, ни дуг, ни автоудара). Место мелбота занимает gun-bot: то же
//     самое, но для оружия дальнего боя;
//   * убрал ствол — всё возвращается мелботу.
//
// Галочка «Аимбот» только разрешает gun-bot'у работать: она не отмечена —
// со стволом в руке не работает никто (мелбот выключен, gun-bot тоже), отмечена —
// gun-bot рисует свои визуалы (те же линия и дуги, но синие) и точки наводки.
// Наводка сейчас — только геометрия (см. fnc/PredictPoint.cs): красная точка
// предикта и синяя точка ПИД, которая к ней тянется. Ничего не нажимает и не
// стреляет.
//
// О смене режима пишем строкой в лог и всплывашкой у курсора — как «ХП при уроне»
// всплывает над тем, кто получил урон (fnc/DamagePopup.cs).
public static class GunBot
{
    // Галочка «Аимбот» (вкладка Gun-bot). Влияет на Active и на визуалы.
    public static bool Aim;

    // Ствол в руке и аимбот разрешён — бой должен вести gun-bot, а не мелбот.
    public static bool Active { get; private set; }

    // Галочка из окна и из конфига: вместе с признаком поднимаем/убираем оверлей
    // визуалов, иначе после загрузки конфига синие дуги не появились бы.
    public static void SetAim(bool on)
    {
        Aim = on;

        // Оверлей с точками висит всегда: галочка только разрешает стрельбу.
        // Иначе точки пропадали бы вместе с выключенным аимботом.
        ToggleOverlay(true);
    }

    // Отдельная галочка «Авто-стрельба»: удержание кнопки огня, автоклики на
    // неавтоматическом оружии, боевой режим и допуск разброса — всё в ней одной.
    // Наводка от неё не зависит: точки рисуются и с выключенной авто-стрельбой.
    public static void SetAutoFire(bool on)
    {
        AimBotSettings.AutoFire = on;
        AimBotSettings.AccuracyGate = on;
        AimBotSettings.AutoCombat = on;
    }

    // Оверлей визуалов gun-bot'а.
    public static void ToggleOverlay(bool on)
    {
        var overlays = IoCManager.Resolve<IOverlayManager>();

        if (on)
        {
            if (!overlays.HasOverlay<GunBotOverlay>())
                overlays.AddOverlay(new GunBotOverlay());
        }
        else
        {
            overlays.RemoveOverlay<GunBotOverlay>();
        }
    }

    // Последнее состояние, о котором объявляли, — чтобы не спамить каждый кадр.
    private static (bool Gun, bool Aim)? _lastState;

    // Что в активной руке: ствол? Это и есть переключатель роутера.
    // Во время перезарядки активная рука на миг меняется на руку с магазином,
    // поэтому ствол «пропадает» — мелбот в этот момент не должен встревать.
    public static bool HoldingGun(IEntityManager ent, EntityUid self)
        => GunApi.Reloading || AttackReach.TryGetGun(ent, self, out _);

    // Опрос режима: зовётся из оверлея всплывашек каждый кадр, независимо от боя.
    // Смена состояния — строка в лог и всплывашка у курсора.
    public static void Poll(IEntityManager ent, EntityUid? self)
    {
        var gunInHand = self is { Valid: true } player && HoldingGun(ent, player);
        Active = gunInHand && Aim;

        var state = (gunInHand, Aim);

        if (_lastState == state)
            return;

        _lastState = state;

        string text;
        Color color;

        if (gunInHand && Aim)
        {
            text = "роутер: дальний бой (gun-bot)";
            color = new Color(1f, 0.6f, 0.2f, 0.9f);
        }
        else if (gunInHand)
        {
            text = "роутер: мелбот выключен (аимбот не стоит)";
            color = new Color(0.75f, 0.75f, 0.75f, 0.9f);
        }
        else
        {
            text = "роутер: ближний бой (мелбот)";
            color = new Color(1f, 0.85f, 0.2f, 0.9f);
        }

        Console.WriteLine($"[SsMod] {text}");
        Popup(text, color);
    }

    // ---------- всплывашка у курсора ----------

    private const float PopupLifetime = 1.2f;

    internal readonly struct ModePopup
    {
        public readonly string Text;
        public readonly Color Color;
        public readonly TimeSpan Start;
        public readonly TimeSpan Until;

        public ModePopup(string text, Color color, TimeSpan start, TimeSpan until)
        {
            Text = text;
            Color = color;
            Start = start;
            Until = until;
        }
    }

    internal static readonly List<ModePopup> Popups = new();

    private static void Popup(string text, Color color)
    {
        var now = IoCManager.Resolve<IGameTiming>().RealTime;
        Popups.Add(new ModePopup(text, color, now, now + TimeSpan.FromSeconds(PopupLifetime)));

        // Больше нескольких сразу не бывает, но подстрахуемся от утечки.
        if (Popups.Count > 8)
            Popups.RemoveAt(0);
    }

    // ---------- вкладка окна ----------

    public static Control BuildTab()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var aim = new CheckBox
        {
            Text = "Стрелять в красную точку (аимбот)",
            Pressed = Aim,
            ToolTip = "Одна кнопка на всё: наводка, удержание огня (у неавтоматического " +
                      "оружия — автоклики), боевой режим, допуск разброса 1 клетка, " +
                      "перезарядка и две руки",
        };

        aim.OnToggled += args =>
        {
            SetAim(args.Pressed);
            Console.WriteLine($"[SsMod] gun-bot: аимбот = {args.Pressed}");
        };

        // Авто-стрельба: удержание кнопки огня, автоклики на неавтоматическом
        // оружии, боевой режим и допуск разброса — одной галочкой. Наводка от
        // неё не зависит и работает отдельно.
        var autoFire = new CheckBox
        {
            Text = "Авто-стрельба (удержание / автоклик)",
            Pressed = AimBotSettings.AutoFire,
            ToolTip = "Бот сам держит огонь, пока цель в красной точке. Полуавтомат — " +
                      "автокликами. Плюс боевой режим и допуск разброса 1 клетка",
        };
        autoFire.OnToggled += args =>
        {
            SetAutoFire(args.Pressed);
            Console.WriteLine($"[SsMod] gun-bot: авто-стрельба = {args.Pressed}");
        };

        var status = new Label { Text = "" };

        // Снять все метки приоритетов. Нужна кнопка, а не только ПКМ-пункты: метки
        // живут в памяти мода и по одной снимаются долго.
        var del = new Button
        {
            Text = "Del prior",
            ToolTip = "Снять все метки приоритета (Make Friend / Make Enemy)",
        };

        del.OnPressed += _ =>
        {
            var count = Priorities.Clear();
            status.Text = $"снято меток: {count}";
            Console.WriteLine($"[SsMod] del prior: снято меток {count}");
        };

        // Авто-перезарядка и авто-передёргивание затвора: боту нечего стоять
        // с пустым стволом или открытым болтом.
        var reload = new CheckBox
        {
            Text = "Авто-перезарядка",
            Pressed = AimBotSettings.AutoReload,
            ToolTip = "Ствол пуст и магазин в свободной руке — вставить самим",
        };
        reload.OnToggled += args => AimBotSettings.AutoReload = args.Pressed;

        var rack = new CheckBox
        {
            Text = "Авто-передёргивание затвора",
            Pressed = AimBotSettings.AutoRack,
            ToolTip = "Затвор открыт или помпа не передёрнута — передёрнуть самим",
        };
        rack.OnToggled += args => AimBotSettings.AutoRack = args.Pressed;

        box.AddChild(aim);
        box.AddChild(autoFire);
        box.AddChild(reload);
        box.AddChild(rack);
        box.AddChild(status);
        box.AddChild(del);

        return box;
    }

}

// Визуалы gun-bot'а: те же линия и дуги, что у мелбота (см. CombatVisuals),
// но синие. Показывают цель, по которой gun-bot наводится, — то есть того же,
// кого выбрал бы мелбот: сначала помеченного Make Enemy, иначе того, кто под
// курсором.
public sealed class GunBotOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

    // Систему берём лениво: в EntryPoint.PostInit IEntityManager.System<T>()
    // падает с NRE — оверлеи создаются именно там.
    private SharedTransformSystem? _transformCache;
    private SharedTransformSystem Transform => _transformCache ??= _ent.System<SharedTransformSystem>();

    // Шрифт для градусов разброса.
    private readonly Font _font;

    public GunBotOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(
            _cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            DrawTarget(args);
        }
        catch (Exception e)
        {
            // Оверлей рисуется каждый кадр: исключение здесь завалило бы рендер.
            // Пишем в stdout: stderr лаунчер не сохраняет, и ошибка была невидима.
            Console.WriteLine($"[SsMod] точки: ошибка — {e.Message}");
        }
    }

    private static bool _loggedDraw;

    private void DrawTarget(in OverlayDrawArgs args)
    {
        if (args.Viewport.Eye is null)
            return;

        if (!_loggedDraw)
        {
            _loggedDraw = true;
            Console.WriteLine("[SsMod] точки: оверлей рисуется");
        }

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        // Точки наводки — визуал только для оружия дальнего боя: без ствола в
        // руке их не рисуем и цель не выбираем.
        if (GunBot.Aim && AttackReach.TryGetGun(_ent, self, out _))
        {
            // Аимботу нужен вьюпорт, чтобы перевести красную точку в экранную и
            // подсунуть её вместо курсора на время GunSystem.Update.
            AimBot.SetViewport(args.Viewport, args.ViewportBounds);

            // Кнопку огня читаем по-настоящему: она держит лок цели.
            PredictPoint.Tick(_ent, self, CombatVisuals.CursorWorld(args, out _), AimBot.IsFireHeld());
            DrawPoints(args);
        }

        // Обслуживание ствола (перезарядка / передёргивание затвора) — до всех
        // гейтов Step: для этого не нужны ни цель, ни боевой режим.
        if (GunBot.Aim)
            AimBot.Maintain(_ent, self);

        // Дальше — визуал gun-bot'а: та же линия и дуги, что у мелбота (см.
        // CombatVisuals), но синие. Только когда бой реально ведёт gun-bot:
        // ствол в активной руке, аимбот отмечен и персонаж в боевом режиме.
        if (!GunBot.Active)
            return;

        // Цели нет — разброс всё равно показываем, у курсора: иначе понять,
        // какой сейчас конус, можно только наведясь на кого-то.
        if (PredictPoint.Target is not { } target || !_ent.HasComponent<TransformComponent>(target))
        {
            DrawSpreadAtCursor(args, self);
            return;
        }

        if (!_ent.TryGetComponent(self, out CombatModeComponent? combat) || !combat.IsInCombatMode)
        {
            DrawSpreadAtCursor(args, self);
            return;
        }

        var selfWorld = Transform.GetWorldPosition(self);
        var targetWorld = Transform.GetWorldPosition(target);

        var handle = args.ScreenHandle;
        var toScreen = CombatVisuals.Screen(args, targetWorld);

        if (VisualSettings.TraceLine)
        {
            CombatVisuals.ThickLine(handle,
                CombatVisuals.Screen(args, selfWorld),
                toScreen,
                VisualSettings.TraceWidth,
                CombatVisuals.Ranged.Line);
        }

        // Радиус дуг — конус разброса ровно на дистанции до цели: дальше цель,
        // шире круг; ближе — уже. Множитель и минимум из настроек.
        var radiusTiles = MathF.Max(AimBot.ConeTiles * VisualSettings.SpreadScale, VisualSettings.SpreadMinTiles);
        var radius = CombatVisuals.Radius(args, targetWorld, radiusTiles);

        if (VisualSettings.SpreadEnabled && VisualSettings.SpreadArcs)
            CombatVisuals.Arcs(handle, toScreen, radius, CombatVisuals.Ranged.Arc, _timing.RealTime);

        if (VisualSettings.SpreadEnabled && VisualSettings.SpreadCone)
            DrawCone(handle, args, selfWorld, targetWorld, AimBot.SpreadDeg * VisualSettings.SpreadScale);

        if (!VisualSettings.SpreadDegrees)
            return;

        // Градусы разброса рядом с дугой.
        var text = $"{AimBot.SpreadDeg:0.0}°";
        var pos = toScreen + new Vector2(radius + 6f, -6f);
        Compat.DrawText(handle, _font, pos + new Vector2(1f, 1f), text, 1f, new Color(0f, 0f, 0f, 0.7f));
        Compat.DrawText(handle, _font, pos, text, 1f, VisualSettings.SpreadTextColor);
    }

    // Разброс без цели: дуга вокруг курсора. Радиус — конус на дистанции до
    // курсора, поэтому меняется с расстоянием и масштабом (зумом).
    private void DrawSpreadAtCursor(in OverlayDrawArgs args, EntityUid self)
    {
        if (!VisualSettings.SpreadEnabled || !VisualSettings.SpreadNoTarget)
            return;

        if (!AttackReach.TryGetGun(_ent, self, out var gunUid))
            return;

        var world = CombatVisuals.CursorWorld(args, out _);
        var selfWorld = Transform.GetWorldPosition(self);

        // Дистанция — до курсора, а не фиксированная: отводишь курсор дальше,
        // круг разброса растёт, подводишь — сжимается.
        var distance = (world - selfWorld).Length();
        var (deg, tiles) = AimBot.SpreadAt(gunUid, distance);

        var radiusTiles = MathF.Max(tiles * VisualSettings.SpreadScale, VisualSettings.SpreadMinTiles);

        var center = CombatVisuals.CursorScreen(args, out _) + new Vector2(0f, VisualSettings.SpreadCursorOffsetY);
        var radius = CombatVisuals.Radius(args, world, radiusTiles);

        if (VisualSettings.SpreadArcs)
            CombatVisuals.Arcs(handle: args.ScreenHandle, center: center, radius: radius,
                color: CombatVisuals.Ranged.Arc, now: _timing.RealTime);

        if (VisualSettings.SpreadCone)
            DrawCone(args.ScreenHandle, args, selfWorld, world, deg * VisualSettings.SpreadScale);

        if (!VisualSettings.SpreadDegrees)
            return;

        var text = $"{deg:0.0}°";
        var pos = center + new Vector2(radius + 6f, -6f);
        Compat.DrawText(args.ScreenHandle, _font, pos + new Vector2(1f, 1f), text, 1f, new Color(0f, 0f, 0f, 0.7f));
        Compat.DrawText(args.ScreenHandle, _font, pos, text, 1f, VisualSettings.SpreadTextColor);
    }

    // Конус разброса: два луча от стрелка под ±halfDeg и дуга на конце —
    // видно, куда реально может уйти выстрел на этой дистанции.
    private static void DrawCone(DrawingHandleScreen handle, in OverlayDrawArgs args,
        Vector2 fromWorld, Vector2 toWorld, float halfDeg)
    {
        var delta = toWorld - fromWorld;
        var length = delta.Length();
        if (length < 0.05f)
            return;

        var dir = delta / length;
        var half = Math.Clamp(halfDeg, 0f, 45f) * (MathF.PI / 180f);

        var from = CombatVisuals.Screen(args, fromWorld);
        var left = CombatVisuals.Screen(args, fromWorld + Rotate(dir, half) * length);
        var right = CombatVisuals.Screen(args, fromWorld + Rotate(dir, -half) * length);

        var width = MathF.Max(VisualSettings.TraceWidth, 0.5f);
        var color = CombatVisuals.Ranged.Arc;

        CombatVisuals.ThickLine(handle, from, left, width, color);
        CombatVisuals.ThickLine(handle, from, right, width, color);

        // Дуга на конце конуса — по экранным углам, с учётом зума.
        var startAngle = MathF.Atan2(left.Y - from.Y, left.X - from.X);
        var endAngle = MathF.Atan2(right.Y - from.Y, right.X - from.X);
        var sweep = endAngle - startAngle;

        while (sweep > MathF.PI)
            sweep -= MathF.Tau;
        while (sweep < -MathF.PI)
            sweep += MathF.Tau;

        var radius = (left - from).Length();
        const int steps = 24;

        for (var i = 0; i < steps; i++)
        {
            var a0 = startAngle + sweep * i / steps;
            var a1 = startAngle + sweep * (i + 1) / steps;

            CombatVisuals.ThickLine(handle,
                from + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius,
                from + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius,
                width, color);
        }
    }

    private static Vector2 Rotate(Vector2 v, float angle)
    {
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    // Красная — точка предикта (куда надо стрелять), синяя — точка ПИД, которая
    // к ней тянется. Пока это единственное, что делает наводка.
    private static void DrawPoints(in OverlayDrawArgs args)
    {
        if (!PredictPoint.HasTarget)
            return;

        var handle = args.ScreenHandle;
        var red = CombatVisuals.Screen(args, PredictPoint.Red);
        var blue = CombatVisuals.Screen(args, PredictPoint.Blue);

        // Линия «синяя -> красная»: видно, куда и с какой ошибкой идёт регулятор.
        CombatVisuals.ThickLine(handle, blue, red, 1f, new Color(0.6f, 0.75f, 1f, 0.35f));

        handle.DrawCircle(red, 5f, new Color(1f, 0.15f, 0.15f, 0.95f), true);
        handle.DrawCircle(blue, 4f, new Color(0.25f, 0.55f, 1f, 0.95f), true);
    }
}

// Всплывашки роутера у курсора. Своя, а не в DamagePopup: тот живёт только пока
// включена галочка «ХП при уроне», а роутер работает всегда.
public sealed class RouterPopupOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly Font _font;

    public RouterPopupOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(
            _cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            DrawPopups(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] router popup draw failed: {e.Message}");
        }
    }

    private void DrawPopups(in OverlayDrawArgs args)
    {
        GunBot.Poll(_ent, _player.LocalEntity);

        var now = _timing.RealTime;
        var popups = GunBot.Popups;

        for (var i = popups.Count - 1; i >= 0; i--)
        {
            if (popups[i].Until <= now)
                popups.RemoveAt(i);
        }

        if (popups.Count == 0)
            return;

        var handle = args.ScreenHandle;
        var mouse = _input.MouseScreenPosition.Position;

        foreach (var popup in popups)
        {
            var left = (float) (popup.Until - popup.Start).TotalSeconds;
            var fade = Math.Clamp(left / 0.4f, 0f, 1f);
            var screen = mouse + new Vector2(18f, 14f);

            // Тень, чтобы читалось на любом фоне.
            Compat.DrawText(handle, _font, screen + new Vector2(1f, 1f), popup.Text, 1f,
                Color.Black.WithAlpha(0.7f * fade));
            Compat.DrawText(handle, _font, screen, popup.Text, 1f,
                popup.Color.WithAlpha(popup.Color.A * fade));
        }
    }
}
