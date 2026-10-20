using System.Numerics;
using Content.Shared.CombatMode;
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
// Галочка «Аимбот» разрешает gun-bot'у работать: она не отмечена —
// со стволом в руке не работает никто (мелбот выключен, gun-bot тоже), отмечена —
// gun-bot ведёт бой: наводит (см. fnc/AimBot.cs) и рисует синие дуги.
// Сама наводка и стрельба живут в AimBot и используют штатный клиентский путь
// GunSystem.Update — сервер видит обычный запрос выстрела.
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
        ToggleOverlay(on);
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
    public static bool HoldingGun(IEntityManager ent, EntityUid self)
        => AttackReach.TryGetGun(ent, self, out _);

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
            Text = "Аимбот",
            Pressed = Aim,
            ToolTip = "Наводит на цель (с упреждением) и стреляет сам, пока в активной руке " +
                      "оружие дальнего боя. Цель — по меткам Make Enemy; если врагов нет, " +
                      "берётся тот, кто под курсором. Учитывает разброс, темп, две руки и патроны",
        };

        aim.OnToggled += args =>
        {
            SetAim(args.Pressed);
            Console.WriteLine($"[SsMod] gun-bot: аимбот = {args.Pressed}");
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

        box.AddChild(aim);

        // Настройки стрельбы. Каждая — отдельный пункт конфига aimbot.*
        // (см. fnc/Config.cs), чтобы их можно было раздать вместе с остальными.
        box.AddChild(Toggle("Стрелять сам", () => AimBotSettings.AutoFire,
            v => AimBotSettings.AutoFire = v,
            "Держать кнопку стрельбы за игрока. Выключено — бот только наводит, " +
            "а стреляет игрок (классический silent aim)"));

        box.AddChild(Toggle("Ждать сброса разброса", () => AimBotSettings.AccuracyGate,
            v => AimBotSettings.AccuracyGate = v,
            "Не стрелять, пока конус текущего разброса шире хитбокса цели: CurrentAngle " +
            "затухает по AngleDecay, поэтому пауза между выстрелами точнее очереди"));

        box.AddChild(Toggle("Упреждение", () => AimBotSettings.Lead,
            v => AimBotSettings.Lead = v,
            "Стрелять в точку, где цель будет через время полёта снаряда. " +
            "Снаряд наследует скорость стрелка, поэтому считается относительная скорость"));

        box.AddChild(Toggle("Цель под курсором", () => AimBotSettings.CursorFallback,
            v => AimBotSettings.CursorFallback = v,
            "Если врагов с меткой Make Enemy рядом нет — брать того, над кем стоит курсор. " +
            "Осторожно: нейтральный под курсором тоже станет целью"));

        box.AddChild(Toggle("Не стрелять через стены", () => AimBotSettings.WallCheck,
            v => AimBotSettings.WallCheck = v,
            "Луч по маске пуль (Impassable | BulletImpassable): если на линии стена, " +
            "выстрела не будет"));

        box.AddChild(Toggle("Не стрелять по своим", () => AimBotSettings.FriendlyCheck,
            v => AimBotSettings.FriendlyCheck = v,
            "Не стрелять, если на линии огня стоит тот, кому поставлена метка Make Friend"));

        box.AddChild(Toggle("Брать в две руки", () => AimBotSettings.AutoWield,
            v => AimBotSettings.AutoWield = v,
            "GunRequiresWield не даст выстрелить, пока Wielded == false. Бот сам шлёт " +
            "UseInHand (то же, что Z или клик по слоту руки)"));

        box.AddChild(Toggle("Включать боевой режим", () => AimBotSettings.AutoCombat,
            v => AimBotSettings.AutoCombat = v,
            "Без CombatMode клиентский GunSystem.Update не отправляет запрос на выстрел. " +
            "Бот жмёт действие ActionCombatModeToggle"));

        box.AddChild(new Label { Text = "Визуалы: линия до точки прицела, дуги и круг разброса." });
        box.AddChild(new Label { Text = "Цель — по приоритетам: Make Enemy, иначе тот, кто под курсором." });
        box.AddChild(new Label { Text = "Роутер боя работает всегда: ствол в активной руке выключает" });
        box.AddChild(new Label { Text = "мелбот целиком (вместе с визуалами) и отдаёт бой gun-bot'у." });
        box.AddChild(status);
        box.AddChild(del);

        return box;
    }

    // Галочка настройки: читает текущее значение и пишет его в статическое поле.
    private static CheckBox Toggle(string text, Func<bool> get, Action<bool> set, string tooltip)
    {
        var check = new CheckBox { Text = text, Pressed = get(), ToolTip = tooltip };

        check.OnToggled += args =>
        {
            set(args.Pressed);
            Console.WriteLine($"[SsMod] аимбот: {text.ToLowerInvariant()} = {args.Pressed}");
        };

        return check;
    }
}

// Визуалы gun-bot'а: те же линия и дуги, что у мелбота (см. CombatVisuals),
// но синие. Показывают цель, по которой gun-bot наводится, — то есть того же,
// кого выбрал бы мелбот: сначала помеченного Make Enemy, иначе того, кто под
// курсором.
public sealed class GunBotOverlay : Overlay
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

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
            Console.Error.WriteLine($"[SsMod] gun-bot draw failed: {e.Message}");
        }
    }

    private void DrawTarget(in OverlayDrawArgs args)
    {
        // Вьюпорт нужен аимботу всегда: по нему он переводит мировую точку
        // прицела в экранные координаты для патча MouseScreenPosition.
        AimBot.SetViewport(args.Viewport, args.ViewportBounds);

        // Оверлей висит, пока отмечен аимбот; рисуем, только когда бой реально
        // ведёт gun-bot (ствол в руке) и цель уже выбрана.
        if (!GunBot.Aim || args.Viewport.Eye is null)
            return;

        if (_player.LocalEntity is not { Valid: true })
            return;

        if (!AimBot.Engaged || AimBot.Target is null)
            return;

        var selfScreen = CombatVisuals.Screen(args, AimBot.SelfWorld);
        var aimScreen = CombatVisuals.Screen(args, AimBot.AimWorld);

        // Линия до точки прицела (с упреждением) и дуги вокруг неё.
        CombatVisuals.Draw(args.ScreenHandle,
            selfScreen,
            aimScreen,
            CombatVisuals.ArcRadius(args, AimBot.AimWorld),
            CombatVisuals.Ranged,
            _timing.RealTime);

        // Конус разброса: круг радиусом tan(CurrentAngle / 2) * дистанция.
        // Именно в него сервер разбрасывает выстрел, поэтому по кругу сразу
        // видно, когда стрелять рано.
        if (AimBot.ConeTiles > 0.01f)
        {
            var radius = CombatVisuals.Radius(args, AimBot.AimWorld, AimBot.ConeTiles);

            if (radius is > 1f and < 4000f)
                CombatVisuals.Circle(args.ScreenHandle, aimScreen, radius, 1.5f,
                    new Color(1f, 0.45f, 0.35f, 0.45f));
        }

        // Крестик в точке прицела.
        var cross = 6f;
        CombatVisuals.ThickLine(args.ScreenHandle,
            aimScreen - new Vector2(cross, 0f), aimScreen + new Vector2(cross, 0f), 1.5f, Color.White);
        CombatVisuals.ThickLine(args.ScreenHandle,
            aimScreen - new Vector2(0f, cross), aimScreen + new Vector2(0f, cross), 1.5f, Color.White);

        DrawStatus(args, aimScreen);
    }

    // Короткая строка: что делает бот и почему молчит. Без неё непонятно, чем
    // он недоволен — то ли разброс, то ли патроны, то ли руки.
    private void DrawStatus(in OverlayDrawArgs args, Vector2 aimScreen)
    {
        var spread = AimBot.SpreadDeg > 0.05f ? $"{AimBot.SpreadDeg:0.0}°" : "0°";
        var ammo = AimBot.AmmoCount >= 0 ? $"{AimBot.AmmoCount}/{AimBot.AmmoMax}" : "?";
        var head = $"{(AimBot.WantsFire ? "огонь" : "жду")} · {AimBot.Distance:0.0} м · {spread} · {ammo}";
        var text = AimBot.Status.Length > 0 ? $"{head} · {AimBot.Status}" : head;

        var pos = aimScreen + new Vector2(14f, 10f);
        Compat.DrawText(args.ScreenHandle, _font, pos + new Vector2(1f, 1f), text, 1f,
            Color.Black.WithAlpha(0.7f));
        Compat.DrawText(args.ScreenHandle, _font, pos, text, 1f,
            new Color(0.75f, 0.85f, 1f, 0.9f));
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
