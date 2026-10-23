using System.Numerics;
using Content.Shared.Interaction;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Предикт взаимодействий: клик по предмету вне радиуса не пропадает, а ждёт.
//
// Как работает: если LMB/альт-клик по цели не прошёл из-за дальности, цель и
// параметры клика запоминаются; у цели рисуется заполняющийся круг-таймер.
// Пока круг активен, каждый кадр проверяем дальность — как только цель в
// радиусе, тот же клик повторяется (подхват, использование, альт-действие).
// Новый клик или истечение секунд отменяют ожидание.
internal static class InteractQueue
{
    public static bool Enabled = true;

    // Сколько секунд ждать, пока цель окажется в радиусе.
    public static float Seconds = 2f;

    // Радиус/толщина/цвет круга у цели (запасной вариант, если нет иконки).
    public const float RingRadius = 22f;
    public const float RingWidth = 4f;
    private static readonly Color RingBack = new(0f, 0f, 0f, 0.45f);
    private static readonly Color RingFill = new(0.3f, 0.9f, 1f, 0.9f);

    // Готовые кадры таймера из ресурсов игры (радиальный открут).
    private const string CooldownBase = "/Textures/Interface/Inventory/cooldown-";
    private const int CooldownFrames = 9;

    // Микрозадержка перед повтором: пара кадров, чтобы граничный кадр (дальность
    // дёргается на границе радиуса) не дал повтору уйти впустую. На ощущения
    // не влияет — срабатывает мгновенно.
    private const float InRangeDelay = 0.05f;

    private static SharedInteractionSystem? _system;
    private static EntityUid _user;
    private static EntityUid _target;

    // Альтернативный клик (alt+LMB) или обычный.
    private static bool _alt;

    // Повтор выпускается самим модом: в это время префикс не должен ни
    // перезаписывать, ни заново ставить ожидание.
    internal static bool Retrying;

    private static TimeSpan _deadline;
    private static float _total;

    // С какого момента цель непрерывно в радиусе (для микрозадержки).
    private static TimeSpan _inRangeSince;
    private static bool _wasInRange;

    public static bool Pending => _system != null;

    // Цель ожидания — для рисования круга.
    internal static EntityUid? Target => _system == null ? null : _target;

    // Вызов из префикса на UserInteraction: вне радиуса — запомнить клик.
    // Тот же клик при перипредикте гоняется по несколько раз — таймер при
    // этом не сбрасываем, иначе ожидание длится вечно.
    internal static void Enqueue(SharedInteractionSystem system, EntityUid user, EntityUid target, bool alt,
        IGameTiming timing)
    {
        if (_system != null && _target == target)
        {
            _system = system;
            _user = user;
            _alt = alt;
            return;
        }

        _system = system;
        _user = user;
        _target = target;
        _alt = alt;
        _total = MathF.Max(Seconds, 0.1f);
        _deadline = timing.CurTime + TimeSpan.FromSeconds(_total);
        _wasInRange = false;

        Console.WriteLine($"[SsMod] предикт: цель {target} вне радиуса, ждём {_total:0.0} с");
    }

    public static void Cancel()
    {
        _system = null;
        _wasInRange = false;
    }

    // Общая для версий проверка дальности — как у самой игры: перегрузка с
    // EntityUid-целью исключает цель из луча (закрытый шлюз не перекрывает сам
    // себя), плюс поблажки для предметов в стенах и настенных панелей.
    // Вариант по координатам этого не делает и для твёрдых целей всегда
    // отвечал «перекрыто». Перегрузка есть на 264/276/277/290.
    internal static bool InRange(SharedInteractionSystem interaction, EntityUid user, EntityUid target)
    {
        var ent = IoCManager.Resolve<IEntityManager>();
        if (!ent.TryGetComponent(target, out TransformComponent? xform))
            return false;

        var origin = ent.System<SharedTransformSystem>().GetMapCoordinates(user);
        return interaction.InRangeUnobstructed(origin, target);
    }

    // Раз в кадр из оверлея: проверяем ожидание и, если цель в радиусе,
    // повторяем клик. Рисование там же, в InteractQueueOverlay.
    public static void Tick(IEntityManager ent, IGameTiming timing)
    {
        if (_system is not { } system || _target is not { Valid: true } target)
            return;

        var player = IoCManager.Resolve<IPlayerManager>();
        if (player.LocalEntity is not { Valid: true } self || self != _user
            || !ent.HasComponent<TransformComponent>(target))
        {
            Cancel();
            return;
        }

        if (timing.CurTime >= _deadline)
        {
            Console.WriteLine($"[SsMod] предикт: не дождались цель {_target}");
            Cancel();
            return;
        }

        // Цель уже при нас (в руке или инвентаре — родитель сущности мы сами):
        // повторный клик по такому предмету означает «использовать в руке»
        // (выпить стакан, активировать мышь), а не подобрать. Ждать нечего.
        if (ent.TryGetComponent(_target, out TransformComponent? held)
            && held.ParentUid == _user)
        {
            Cancel();
            return;
        }

        if (!InRange(system, _user, target))
        {
            _wasInRange = false;
            return;
        }

        // Микрозадержка: ждём, пока цель непрерывно в радиусе.
        if (!_wasInRange)
        {
            _wasInRange = true;
            _inRangeSince = timing.CurTime;
            return;
        }

        if (timing.CurTime - _inRangeSince < TimeSpan.FromSeconds(InRangeDelay))
            return;

        Retry(ent, timing);
    }

    // Повтор клика как настоящего ввода. Прямой вызов UserInteraction здесь не
    // годится: он делает только локальный предикт, а сервер узнаёт о
    // взаимодействии из инпут-сообщения самого клика. Без него подбор
    // откатывается синхронизацией. HandleInputCommand с replay:true гонит и
    // локальные хендлеры (предикт), и отправку команды серверу, минуя
    // дедупликацию состояния кнопки.
    private static void Retry(IEntityManager ent, IGameTiming timing)
    {
        var player = IoCManager.Resolve<Robust.Client.Player.IPlayerManager>();
        var inputManager = IoCManager.Resolve<Robust.Client.Input.IInputManager>();
        var inputSystem = ent.System<Robust.Client.GameObjects.InputSystem>();

        var xform = ent.GetComponent<TransformComponent>(_target);
        var world = ent.System<SharedTransformSystem>().GetWorldPosition(_target);
        // WorldToScreen отдаёт пиксели; вьюпорт 0 — основной экран игры.
        var screen = new ScreenCoordinates(
            IoCManager.Resolve<Robust.Client.Graphics.IEyeManager>().WorldToScreen(world), default);

        var function = _alt
            ? Content.Shared.Input.ContentKeyFunctions.AltActivateItemInWorld
            : Robust.Shared.Input.EngineKeyFunctions.Use;

        // Down, не Up: PointerInputCmdHandler игнорирует Up (ignoreUp) и
        // молча выбрасывает такую команду.
        var msg = new Robust.Shared.Input.ClientFullInputCmdMessage(
            timing.CurTick,
            default,
            inputManager.NetworkBindMap.KeyFunctionID(function),
            xform.Coordinates,
            screen,
            Robust.Shared.Input.BoundKeyState.Down,
            _target);

        Cancel();
        Retrying = true;

        Console.WriteLine($"[SsMod] предикт: цель в радиусе, повторяю клик по {_target}");

        try
        {
            inputSystem.HandleInputCommand(player.LocalSession, function, msg, replay: true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] предикт: повтор клика не удался: {e.Message}");
        }
        finally
        {
            Retrying = false;
        }
    }

    // Таймер у цели: готовые кадры cooldown-0..8 из ресурсов игры — кадр по
    // остатку секунд. Если текстур нет (чужой форк) — рисованный круг.
    public static void Draw(DrawingHandleScreen handle, Vector2 center, IGameTiming timing)
    {
        if (_system == null)
            return;

        var left = (float) (_deadline - timing.CurTime).TotalSeconds;
        var fraction = Math.Clamp(left / _total, 0f, 1f);

        var frame = (int) MathF.Round((1f - fraction) * (CooldownFrames - 1));
        var icon = Icons.Get($"{CooldownBase}{frame}.png");

        if (icon is { } texture)
        {
            // Тёмная подложка, чтобы иконка читалась на любом фоне.
            const float size = 56f;
            handle.DrawCircle(center, size / 2f + 4f, new Color(0f, 0f, 0f, 0.6f));
            handle.DrawTextureRect(texture,
                new UIBox2(center - new Vector2(size / 2f), center + new Vector2(size / 2f)), Color.White);
            return;
        }

        // Нет текстур (чужой форк) — яркий рисованный круг.
        CombatVisuals.Circle(handle, center, RingRadius, RingWidth + 2f, RingBack);

        if (fraction > 0.01f)
            Arc(handle, center, RingRadius, RingWidth + 2f, fraction, RingFill);

        handle.DrawCircle(center, RingWidth, RingFill);
    }

    // Дуга-«пирог»: от вершины по часовой стрелке на долю круга.
    private static void Arc(DrawingHandleScreen handle, Vector2 center, float radius, float width,
        float fraction, Color color)
    {
        const int segments = 40;
        var sweep = MathF.Tau * fraction;
        var step = sweep / segments;

        var from = Point(center, 0f, radius);
        for (var i = 1; i <= segments; i++)
        {
            var to = Point(center, step * i, radius);
            CombatVisuals.ThickLine(handle, from, to, width, color);
            from = to;
        }
    }

    private static Vector2 Point(Vector2 center, float angle, float radius)
        => center + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * radius;

    // ---------- Настройки ----------

    public static Control BuildControls()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var check = new CheckBox
        {
            Text = "Предикт взаимодействий (клик вне радиуса)",
            Pressed = Enabled,
        };
        check.OnToggled += args => Enabled = args.Pressed;
        box.AddChild(check);

        var spin = new FloatSpinBox(0.1f, 1) { Value = Seconds, HorizontalExpand = true };
        spin.OnValueChanged += args => Seconds = Math.Clamp(args.Value, 0.2f, 10f);

        var grid = new GridContainer { Columns = 2 };
        grid.AddChild(new Label { Text = "Секунды ожидания", MinWidth = 110f });
        grid.AddChild(spin);
        box.AddChild(grid);

        return box;
    }
}

// Перехват кликов: SharedInteractionSystem.UserInteraction выполняется и на
// клиенте (предикт), поэтому здесь видно каждый LMB/альт-клик по цели.
// Патч по имени: если в чужой версии сигнатура ушла — Entry переживает сбой
// этого класса, остальной мод работает.
[HarmonyPatch(typeof(SharedInteractionSystem), nameof(SharedInteractionSystem.UserInteraction))]
internal static class InteractQueuePatch
{
    private static void Prefix(SharedInteractionSystem __instance, EntityUid user,
        EntityCoordinates coordinates, EntityUid? target, bool altInteract)
    {
        // Наш собственный повтор — не трогаем ожидание.
        if (InteractQueue.Retrying)
            return;

        if (!InteractQueue.Enabled || target is not { Valid: true } uid || uid == user)
        {
            InteractQueue.Cancel();
            return;
        }

        // Клик по цели в радиусе — действие выполняется обычным путём,
        // прежнее ожидание снимается.
        if (InteractQueue.InRange(__instance, user, uid))
        {
            InteractQueue.Cancel();
            return;
        }

        // Предмет уже при нас (в руке/инвентаре, но дальность «не та» из-за
        // перипредикта старых команд): ждать нечего, иначе повтор кликнул бы
        // по нему как «использовать в руке».
        var ent = IoCManager.Resolve<IEntityManager>();
        if (ent.TryGetComponent(uid, out TransformComponent? xform) && xform.ParentUid == user)
        {
            InteractQueue.Cancel();
            return;
        }

        // Не прошёл по дальности: запоминаем и ждём, пока цель подтянется.
        InteractQueue.Enqueue(__instance, user, uid, altInteract,
            IoCManager.Resolve<IGameTiming>());
    }
}

// Круг-таймер над целью. Висит всегда (как AmmoOverlay), внутри проверяет
// наличие ожидания.
internal sealed class InteractQueueOverlay : Overlay
{
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();

    public InteractQueueOverlay()
    {
        ZIndex = 100;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!InteractQueue.Pending)
            return;

        try
        {
            var ent = IoCManager.Resolve<IEntityManager>();
            InteractQueue.Tick(ent, _timing);

            if (!InteractQueue.Pending)
                return;

            var player = IoCManager.Resolve<IPlayerManager>();
            if (player.LocalEntity is not { Valid: true } self)
                return;

            if (InteractQueue.Target is not { Valid: true } uid
                || !ent.TryGetComponent(uid, out TransformComponent? xform))
                return;

            var transform = ent.System<SharedTransformSystem>();
            var world = transform.GetWorldPosition(uid);
            var center = CombatVisuals.Screen(args, world);

            InteractQueue.Draw(args.ScreenHandle, center, _timing);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] предикт: оверлей: {e.Message}");
        }
    }
}
