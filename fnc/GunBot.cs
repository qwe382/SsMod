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
            Text = "Аимбот (наводка пока не сделана)",
            Pressed = Aim,
            ToolTip = "Разрешает gun-bot'у работать, когда в активной руке оружие дальнего боя: " +
                      "рисует синие дуги по цели. Наводка и стрельба пока не сделаны",
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
        box.AddChild(new Label { Text = "Визуалы: те же линия и дуги, что у мелбота, но синие." });
        box.AddChild(new Label { Text = "Цель — по приоритетам: Make Enemy, иначе тот, кто под курсором." });
        box.AddChild(new Label { Text = "Роутер боя работает всегда: ствол в активной руке выключает" });
        box.AddChild(new Label { Text = "мелбот целиком (вместе с визуалами) и отдаёт бой gun-bot'у." });
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

    private readonly SharedTransformSystem _transform;

    public GunBotOverlay()
    {
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
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
        // Оверлей висит, пока отмечен аимбот.
        if (!GunBot.Aim || args.Viewport.Eye is null)
            return;

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        // Точки наводки: красная — предикт, синяя — ПИД к ней. Рисуются всегда,
        // независимо от оружия в руке и боевого режима: это только геометрия.
        PredictPoint.Tick(_ent, self, CombatVisuals.CursorWorld(args, out _));
        DrawPoints(args);

        // Дальше — визуал роутера, и только когда бой реально ведёт gun-bot:
        // ствол в активной руке и боевой режим.
        if (!GunBot.Active)
            return;

        // Как и мелбот — только в боевом режиме.
        if (!_ent.TryGetComponent(self, out CombatModeComponent? combat) || !combat.IsInCombatMode)
            return;

        // Приоритеты те же, что у мелбота (см. Targeting): сначала ближайший
        // помеченный Make Enemy, иначе — тот, кто под курсором.
        var target = Targeting.NearestEnemy(_ent, self, MelBotOverlay.AutoRange)
                     ?? Targeting.UnderPoint(_ent, self,
                         CombatVisuals.CursorWorld(args, out _), MelBotOverlay.SnapTiles);

        if (target is not { } uid || !_ent.HasComponent<TransformComponent>(uid))
            return;

        var selfWorld = _transform.GetWorldPosition(self);
        var targetWorld = _transform.GetWorldPosition(uid);

        CombatVisuals.Draw(args.ScreenHandle,
            CombatVisuals.Screen(args, selfWorld),
            CombatVisuals.Screen(args, targetWorld),
            CombatVisuals.ArcRadius(args, targetWorld),
            CombatVisuals.Ranged,
            _timing.RealTime);
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
