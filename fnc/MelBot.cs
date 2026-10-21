using System.Numerics;
using Content.Client.Gameplay;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
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
// Атаку мелбот форсирует только пока цель в пределах дальности оружия
// (MeleeWeaponComponent.Range): вне дальности он не бьёт и не машет впустую.
//
// Роутер боя (fnc/GunBot.cs): если в активной руке ствол, мелбот выключен
// целиком — ни цели, ни линии, ни дуг, ни автоудара. Визуалы этого режима
// рисует gun-bot, своими, синими дугами.
//
// Поиск персонажей живёт в fnc/Targeting.cs (общий с gun-bot'ом), отрисовка —
// в fnc/CombatVisuals.cs (общая с gun-bot'ом, отличается только цветом).
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

    // Цель в пределах дальности оружия. Пока false — мелбот не форсирует атаку
    // (ни клик, ни подмену курсора, ни сброс Attacking).
    internal static bool CurrentTargetInReach { get; private set; } = true;

    // Публикация цели наружу: атакой управляет тот оверлей, который сейчас
    // активен — мелбот (по курсору) или крио-бот (по метке Make Enemy).
    internal static void SetTarget(EntityUid? target, MapCoordinates mapCoordinates,
        Vector2 screenPosition, WindowId window, bool inReach)
    {
        CurrentTarget = target;
        CurrentTargetMapCoordinates = mapCoordinates;
        CurrentTargetScreenPosition = screenPosition;
        CurrentWindow = window;
        CurrentTargetInReach = inReach;
    }

    // Пока активен крио-бот, цель публикует он: мелбот не должен её затирать.
    internal static bool YieldToCryoBot => CryoBotOverlay.Active;

    // Автоудар: сами бьём помеченных врагов (ПКМ -> Make Enemy), мышь держать не
    // нужно. Кнопка выбирается галочкой «Исп. ЛКМ»: по умолчанию КПМ (тяжёлый
    // удар ближнего боя; для стрельбы обычно нужна именно ЛКМ).
    public static bool AutoAttack;
    public static bool AutoAttackLmb;

    // Пока true — боевым системам отдаётся «кнопка атаки зажата» (см.
    // MelBotAutoAttackPatch). Пересчитывается каждый кадр в Draw.
    internal static bool HoldAuto;

    // Докуда искать помеченного врага для автоудара. Вне дальности оружия удар
    // всё равно не форсируется (AttackReach), это только радиус поиска.
    public const float AutoRange = 12f;

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // Систему берём лениво: в EntryPoint.PostInit IEntityManager.System<T>()
    // падает с NRE — системы движка ещё не инициализированы, а оверлеи
    // создаются именно там (стартовый конфиг).
    private SharedTransformSystem? _transformCache;
    private SharedTransformSystem Transform => _transformCache ??= _ent.System<SharedTransformSystem>();

    // Радиус захвата в тайлах (1 тайл = 1 единица мира). Gun-bot берёт его же
    // для поиска цели под курсором.
    public const float SnapTiles = 2f;

    // Захваченная цель: пока зажата кнопка атаки, держим именно её, даже если
    // курсор ушёл далеко. Сбрасывается при отпускании кнопки или потере цели.
    private EntityUid? _lockTarget;

    // Последняя напечатанная диагностика, чтобы не спамить каждый кадр.
    private string _lastDiag = "";
    private double _lastDiagTime;

    public MelBotOverlay()
    {
        IoCManager.InjectDependencies(this);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        // Автоудар пересчитывается каждый кадр: сбрасываем до всех выходов,
        // иначе после выключения мелбота кнопка осталась бы «зажатой».
        HoldAuto = false;

        // Атакой сейчас управляет крио-бот — не трогаем опубликованную им цель.
        if (YieldToCryoBot)
            return;

        CurrentTarget = null;
        CurrentTargetInReach = true;
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

            // Роутер боя: ствол в активной руке выключает мелбот целиком — ни
            // поиска цели, ни линии, ни дуг, ни автоудара. Бой ведёт gun-bot
            // (fnc/GunBot.cs), он и рисует — своими, синими дугами.
            if (GunBot.HoldingGun(_ent, self))
            {
                _lockTarget = null;
                CurrentTargetInReach = false;
                return;
            }

            // Координаты курсора приводим в мир через LocalToWorld самого вьюпорта
            // (почему не PixelToMap и не WorldToScreen — см. комментарий к оверлею).
            var mouseWorld = CombatVisuals.CursorWorld(args, out var screenMouse);
            CurrentWindow = screenMouse.Window;

            _zoomDiag = eye.Zoom.X;

            // Ближайший к курсору в пределах SnapTiles тайлов. Живых отбирает
            // Targeting: там же живёт снимок словаря и система приоритетов.
            var nearestUid = Targeting.UnderPoint(_ent, self, mouseWorld, SnapTiles);
            LogDiag(Targeting.Count, nearestUid is not null, mouseWorld);

            // Автоудар: ближайшего помеченного врага выбирает Targeting, то есть
            // цель берётся по приоритетам (Make Enemy). Курсор в этом режиме не
            // нужен; если помеченных рядом нет — всё работает как раньше, по
            // курсору и зажатой кнопке.
            var autoTarget = AutoAttack ? Targeting.NearestEnemy(_ent, self, AutoRange) : null;

            // Вне дальности оружия не машем по пустоте: автоудар — это удар,
            // а не размахивание. Достанем — цель появится сама.
            if (autoTarget is { } candidate && !AttackReach.InReach(_ent, self, candidate))
                autoTarget = null;

            // Выбор активной цели. Пока зажата любая кнопка атаки — держим
            // захваченную цель, даже если курсор уже далеко (SnapTiles не мешает).
            // Кнопку отпустили — захват снимается, цель снова ищется под курсором.
            var input = _ent.System<Robust.Client.GameObjects.InputSystem>();
            var held = input.CmdStates.GetState(EngineKeyFunctions.Use) == BoundKeyState.Down
                       || input.CmdStates.GetState(EngineKeyFunctions.UseSecondary) == BoundKeyState.Down;

            EntityUid? activeUid;
            if (autoTarget is { } autoUid)
            {
                // Автоудар важнее курсора: держим помеченного врага.
                _lockTarget = autoUid;
                activeUid = autoUid;
            }
            else if (held)
            {
                if (_lockTarget is { } locked && _ent.EntityExists(locked) && _ent.HasComponent<TransformComponent>(locked))
                {
                    activeUid = locked;
                }
                else if (nearestUid is { } nearUid)
                {
                    _lockTarget = nearUid;
                    activeUid = nearUid;
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

            // Кнопку жмём только когда цель реально есть: иначе персонаж бил бы
            // по тому, куда случайно смотрит настоящий курсор.
            HoldAuto = autoTarget is not null;

            var targetWorld = Transform.GetWorldPosition(target);
            CurrentTarget = target;

            // Дальность текущего оружия: пока цель вне досягаемости, мелбот не
            // форсирует атаку — иначе он бесконечно машет по пустоте.
            var selfWorld = Transform.GetWorldPosition(self);
            CurrentTargetInReach = AttackReach.InReach(_ent, self, target);

            // Рисуем в ScreenSpace, поэтому мировые точки переводим в экранные
            // WorldToLocal того же вьюпорта — он в тех же координатах, что и
            // LocalToWorld, из которого получена mouseWorld. Саму линию и дуги
            // рисует CombatVisuals: этот же визуал, но синим, рисует gun-bot.
            var targetScreen = CombatVisuals.Screen(args, targetWorld);
            CurrentTargetMapCoordinates = Transform.GetMapCoordinates(target);
            CurrentTargetScreenPosition = targetScreen;

            CombatVisuals.Draw(args.ScreenHandle,
                CombatVisuals.Screen(args, selfWorld),
                targetScreen,
                CombatVisuals.ArcRadius(args, targetWorld),
                CombatVisuals.Melee,
                _timing.RealTime);
        }
        catch (Exception e)
        {
            // Оверлей рисуется каждый кадр: исключение здесь завалило бы рендер.
            // Логируем не один раз, а при смене текста — иначе повторяющийся сбой
            // останется невидимым.
            Console.Error.WriteLine($"[SsMod] melbot draw failed: {e.Message}");
        }
    }

    // Диагностика: не чаще раза в секунду. Проверять «изменилось ли состояние»
    // тут бессмысленно — в тексте есть мышь и зум, они меняются каждый кадр,
    // и лог раздувался до десятков мегабайт за минуты (клиент пишет его синхронно).
    private void LogDiag(int alive, bool found, Vector2 mouseWorld)
    {
        var now = _timing.RealTime.TotalSeconds;
        if (now - _lastDiagTime < 1.0)
            return;

        _lastDiagTime = now;
        _lastDiag = $"живых={alive}, цель={found}, "
                    + $"зум={_zoomDiag:0.##}, мышь={mouseWorld.X:0.#},{mouseWorld.Y:0.#}";
        Console.WriteLine($"[SsMod] melbot: {_lastDiag}");
    }

    private float _zoomDiag;
}

// Переключатель и настройки автоудара в окне мода (Ui/SsModWindow.cs, фрейм 1).
public static class MelBot
{
    public static Control BuildControls()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var check = new CheckBox
        {
            Text = "Mel-Bot",
            Pressed = IoCManager.Resolve<IOverlayManager>().HasOverlay<MelBotOverlay>(),
            ToolTip = "Линия от персонажа к тому, кто под курсором",
        };

        // Настройки автоудара не прячем и не сворачиваем: они всегда на виду,
        // а когда бот выключен — просто серые (Disabled). Отступ слева, чтобы
        // было видно, что это подпункты Mel-Bot'а.
        var settings = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(16f, 0f, 0f, 0f),
        };

        var auto = new CheckBox
        {
            Text = "Автоудар по врагам (Make Enemy)",
            Pressed = MelBotOverlay.AutoAttack,
            ToolTip = "Бьём помеченных врагов сами — удерживать мышь не нужно",
        };

        var lmb = new CheckBox
        {
            Text = "Исп. ЛКМ",
            Pressed = MelBotOverlay.AutoAttackLmb,
            ToolTip = "Выкл — КПМ (тяжёлый удар). Вкл — ЛКМ (обычный удар, стрельба)",
        };

        // Галочки не исчезают: недоступные просто гаснут.
        void Refresh()
        {
            auto.Disabled = !check.Pressed;
            lmb.Disabled = !check.Pressed || !auto.Pressed;
        }

        auto.OnToggled += args =>
        {
            MelBotOverlay.AutoAttack = args.Pressed;
            Refresh();
        };

        lmb.OnToggled += args => MelBotOverlay.AutoAttackLmb = args.Pressed;

        settings.AddChild(auto);
        settings.AddChild(lmb);

        check.OnToggled += args =>
        {
            Toggle(args.Pressed);
            Refresh();
        };

        box.AddChild(check);
        box.AddChild(settings);

        Refresh();
        return box;
    }

    public static void Toggle(bool on)
    {
        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (overlays.HasOverlay<MelBotOverlay>())
                return;

            overlays.AddOverlay(new MelBotOverlay());
            Console.WriteLine("[SsMod] melbot: включён");
        }
        else
        {
            overlays.RemoveOverlay<MelBotOverlay>();
            MelBotOverlay.HoldAuto = false;
            MelBotOverlay.SetTarget(null, default, default, default, true);
        }
    }
}

// Автоудар: пока мод сам ведёт бой (мелбот нашёл помеченного врага в пределах
// дальности), отдаём всем боевым системам «кнопка атаки зажата». Они читают
// состояние кнопок из PlayerCommandStates (MeleeWeaponSystem.Update,
// GunSystem.Update), поэтому удар уходит на сервер обычным порядком — как будто
// игрок сам держит кнопку.
//
// Кнопка выбирается галочкой «Исп. ЛКМ»: по умолчанию КПМ (тяжёлая атака
// ближнего боя), с галочкой — ЛКМ (обычная атака; из оружия дальнего боя обычно
// стреляет именно она).
[HarmonyLib.HarmonyPatch(typeof(Robust.Shared.Input.PlayerCommandStates),
    nameof(Robust.Shared.Input.PlayerCommandStates.GetState))]
internal static class MelBotAutoAttackPatch
{
    private static void Postfix(BoundKeyFunction function, ref BoundKeyState __result)
    {
        if (__result == BoundKeyState.Down || !MelBotOverlay.HoldAuto)
            return;

        var button = MelBotOverlay.AutoAttackLmb
            ? EngineKeyFunctions.Use
            : EngineKeyFunctions.UseSecondary;

        if (function == button)
            __result = BoundKeyState.Down;
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
            MelBotOverlay.CurrentTarget is null ||
            !MelBotOverlay.CurrentTargetInReach)
        {
            return;
        }

        var ent = IoCManager.Resolve<IEntityManager>();
        var transform = ent.System<SharedTransformSystem>();
        var targetMap = MelBotOverlay.CurrentTargetMapCoordinates;
        var coords = transform.ToCoordinates(targetMap);
        var screen = IoCManager.Resolve<Robust.Client.State.IStateManager>().CurrentState as GameplayStateBase;

        // Клик по предмету (подобрать, открыть, поставить) не подменяем:
        // иначе мелбот превращает его в удар по цели и вещи «не берутся».
        if (screen is not null)
        {
            var underCursor = screen.GetClickedEntity(transform.ToMapCoordinates(input.Coordinates));
            if (underCursor is { } orig && orig.Valid && !ent.HasComponent<MobStateComponent>(orig))
                return;
        }

        var clicked = screen?.GetClickedEntity(targetMap) ?? EntityUid.Invalid;

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
        if (MelBotOverlay.CurrentTarget is null || !MelBotOverlay.CurrentTargetInReach)
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
//
// Но только пока цель в пределах дальности оружия: иначе мелбот махал бы
// бесконечно по пустоте, даже когда враг ушёл.
[HarmonyLib.HarmonyPatch(typeof(Content.Client.Weapons.Melee.MeleeWeaponSystem),
    nameof(Content.Client.Weapons.Melee.MeleeWeaponSystem.Update))]
internal static class MelBotMeleePatch
{
    private static void Prefix(Content.Client.Weapons.Melee.MeleeWeaponSystem __instance)
    {
        if (MelBotOverlay.CurrentTarget is null || !MelBotOverlay.CurrentTargetInReach)
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
