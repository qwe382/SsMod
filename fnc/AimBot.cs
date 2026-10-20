using System.Collections;
using System.Numerics;
using System.Reflection;
using Content.Shared.CombatMode;
using Content.Shared.Hands.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Wieldable.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Аимбот для оружия дальнего боя (fish_station, движок 277).
//
// Сделано по декомпиляции серверного Content.Server.Weapons.Ranged.Systems.GunSystem
// и клиентского GunSystem, а не «по интуиции». Вот что там на самом деле:
//
//  * Выстрел летит НЕ из ствола, а из трансформа стрелка: сервер берёт
//    fromCoordinates = Transform(user), toCoordinates = точка курсора, и считает
//    направление между ними. Значит наводка — это просто «куда смотрит курсор».
//  * Разброс накладывает сервер (GetRecoilAngle): на каждый выстрел
//    CurrentAngle = clamp(CurrentAngle + AngleIncrease - AngleDecay * (t - LastFire),
//    MinAngle, MaxAngle), и выстрел отклоняется на CurrentAngle * U(-0.5, 0.5).
//    То есть максимальное отклонение — половина CurrentAngle, и он РАСТЁТ от
//    выстрела к выстрелу и затухает по времени. CurrentAngle и все *Modified
//    поля приезжают на клиент (AutoNetworkedField), поэтому мы видим ту же
//    цифру, которой стреляет сервер.
//  * GunWieldBonus уже свёрнут в *Modified-поля: пока оружие не в двух руках,
//    MinAngle/MaxAngle там одни, после взятия — другие (у винтовок рыба
//    minAngle: 3..9, после взятия 0 — то есть ровный ноль разброса).
//  * GunRequiresWield + WieldableComponent: сервер отменяет выстрел
//    (ShotAttemptedEvent) и показывает «wieldable-component-requires», пока
//    Wielded == false. Взять в руки — это UseInHand (Z) / клик по слоту руки:
//    клиент шлёт RequestUseInHandEvent, сервер вызывает UseInHandEvent.
//  * Огонь: FireRateModified, NextFire, SelectedMode (SemiAuto/Burst/FullAuto),
//    BurstActivated/ShotsPerBurst/BurstCooldown/ShotCounter. Полуавтомат за один
//    зажим даёт ровно ОДИН выстрел (num = min(num, 1 - ShotCounter)), поэтому
//    для него кнопку надо отпускать между выстрелами — иначе бот выстрелит
//    один раз и встанет.
//  * Кнопка стрельбы зависит от GunComponent.UseKey: true — ЛКМ (Use),
//    false — ПКМ (UseSecondary). Плюс нужен CombatModeComponent.IsInCombatMode,
//    иначе клиентский Update вообще не дойдёт до отправки запроса.
//  * Снаряд наследует скорость стрелка (ShootProjectile:
//    v = скорость_стрелка + dir * ProjectileSpeedModified), поэтому упреждение
//    считаем по относительной скорости цели и себя.
//  * Хитскан (HitscanAmmoComponent) летит мгновенно — упреждение ему не нужно.
//    В прототипах рыба хитсканов у оружия нет (hitscan-сущности есть, но
//    HitscanAmmo в прототипах закомментирован), поэтому по умолчанию считаем
//    оружие снарядным; хитскан определяем по заряженному боеприпасу.
//  * TargetedProjectileComponent в fish есть, но её никто не читает
//    (ни в Content.Shared, ни в Content.Server) — самонаведения нет, упреждение
//    всё равно обязательно.
//  * Pump/BoltClosed: помповому оружию нужно передёрнуть помпу, болтовому —
//    закрыть затвор (TakeAmmoEvent возвращает "gun-chamber-bolt-ammo"), пока это
//    не сделано, выстрела не будет.
//
// Работаем не «своим» выстрелом, а штатным клиентским путём: подменяем позицию
// курсора на время GunSystem.Update и состояние кнопки стрельбы. Запрос уходит
// на сервер обычным порядком, со всей его валидацией (темп, разброс, патроны,
// две руки) — мод ничего не «дорисовывает» на сервере.
public static class AimBotSettings
{
    // Разрешить боту стрелять самому (иначе только наводка: кнопку держит игрок).
    public static bool AutoFire = true;

    // Не стрелять, пока конус разброса шире цели: ждать, пока CurrentAngle
    // затухнет (AngleDecay), вместо очереди в молоко.
    public static bool AccuracyGate = true;

    // Допуск точности: 1 — конус разброса ровно с радиус хитбокса цели,
    // меньше — стрелять только наверняка, больше — не ждать.
    public static float AccuracyTolerance = 1f;

    // Упреждение по скорости цели (для снарядов; хитскану не нужно).
    public static bool Lead = true;

    // Дальше этого (в тайлах) цель не ищем.
    public static float MaxRange = 20f;

    // Если врагов с меткой нет — брать того, кто под курсором. По умолчанию
    // выключено: иначе бот сам открывает огонь по нейтральному, стоит провести
    // мышью над ним.
    public static bool CursorFallback = false;

    // Автоматически брать оружие в две руки (используем UseInHand).
    public static bool AutoWield = true;

    // Автоматически включать боевой режим (действие ActionCombatModeToggle).
    public static bool AutoCombat = true;

    // Не стрелять, если линия огня упирается в стену.
    public static bool WallCheck = true;

    // Не стрелять, если на линии огня союзник (Make Friend).
    public static bool FriendlyCheck = true;
}

// Состояние на кадр. Считается в Prefix GunSystem.Update, рисуется оверлеем.
public static class AimBot
{
    // Радиус хитбокса моба: PhysShapeCircle radius: 0.35 в BaseControllable.
    public const float HitboxRadius = 0.35f;

    // Быстрее этого персонаж не бегает; всё, что выше, — телепорт/интерполяция.
    private const float MaxTrackSpeed = 15f;

    // Публичное состояние (читает оверлей и патчи).
    public static bool Engaged { get; private set; }
    public static bool WantsFire { get; private set; }
    public static bool Aiming { get; private set; }
    public static bool TriggerOwned { get; private set; }
    public static EntityUid? Target { get; private set; }
    public static Vector2 SelfWorld { get; private set; }
    public static Vector2 AimWorld { get; private set; }
    public static MapCoordinates AimMap { get; private set; }
    public static float Distance { get; private set; }
    public static float SpreadDeg { get; private set; }
    public static float ConeTiles { get; private set; }
    public static string Status { get; private set; } = "";
    public static int AmmoCount { get; private set; } = -1;
    public static int AmmoMax { get; private set; } = -1;
    public static bool Hitscan { get; private set; }
    public static bool Wielded { get; private set; }
    public static bool RequiresWield { get; private set; }
    public static bool InCombat { get; private set; }
    public static BoundKeyFunction FireKey { get; private set; } = EngineKeyFunctions.Use;
    public static WindowId CursorWindow { get; private set; }

    // Патчи читают это, чтобы не вмешиваться в обычную игру.
    internal static bool InGunUpdate;
    internal static bool Bypass;

    // Вьюпорт берём из оверлея: WorldToLocal + левый верхний угол вьюпорта —
    // та же согласованная пара, что у мелбота (см. CombatVisuals).
    private static IClydeViewport? _viewport;
    private static UIBox2 _bounds;

    private static readonly Dictionary<EntityUid, (Vector2 Pos, Vector2 Vel, TimeSpan At)> Tracked = new();

    private static string _lastWhy = "";
    private static TimeSpan _lastWhyAt;

    // Оверлей передаёт сюда вьюпорт и его прямоугольник (раз в кадр).
    public static void SetViewport(IClydeViewport viewport, UIBox2 bounds)
    {
        _viewport = viewport;
        _bounds = bounds;

        // Окно запоминаем здесь: в патче MouseScreenPosition нельзя спрашивать
        // MouseScreenPosition — это тот же геттер и рекурсия.
        try
        {
            CursorWindow = IoCManager.Resolve<IInputManager>().MouseScreenPosition.Window;
        }
        catch
        {
            // окно недоступно — оставляем прежнее
        }
    }

    // Экранная точка прицела (viewport-пиксели + левый верхний угол вьюпорта).
    // Её подставляет патч MouseScreenPosition, поэтому свойство публичное.
    public static Vector2 ScreenAim
    {
        get
        {
            if (_viewport is null)
                return default;

            return _viewport.WorldToLocal(AimWorld) + Compat.BoxTopLeft(_bounds);
        }
    }

    private static bool OnScreen(Vector2 screen)
    {
        return screen.X >= Compat.BoxLeft(_bounds) && screen.X <= Compat.BoxRight(_bounds)
            && screen.Y >= Compat.BoxTop(_bounds) && screen.Y <= Compat.BoxBottom(_bounds);
    }

    // ------------------------------------------------------------- основной шаг

    // Зовётся из Prefix GunSystem.Update: геометрия и решение «стрелять/нет»
    // должны быть свежими именно на том кадре, когда клиент отправляет запрос.
    public static void Tick()
    {
        try
        {
            Step();
        }
        catch (Exception e)
        {
            // Update клиентской пушки без try/catch — исключение сломало бы стрельбу.
            Reset();
            Console.Error.WriteLine($"[SsMod] аимбот: {e}");
        }
    }

    private static void Step()
    {
        Reset();

        var ent = IoCManager.Resolve<IEntityManager>();
        var player = IoCManager.Resolve<IPlayerManager>();
        var timing = IoCManager.Resolve<IGameTiming>();

        if (!GunBot.Aim || player.LocalEntity is not { Valid: true } self)
            return;

        // Ствол в активной руке — тот же признак, по которому роутер отдаёт бой
        // gun-bot'у (см. GunBot/AttackReach).
        if (!AttackReach.TryGetGun(ent, self, out var gunUid))
            return;

        if (!ent.TryGetComponent(gunUid, out GunComponent? gun))
            return;

        var transform = ent.System<SharedTransformSystem>();
        SelfWorld = transform.GetWorldPosition(self);
        var selfMap = transform.GetMapId(self);

        FireKey = gun.UseKey ? EngineKeyFunctions.Use : EngineKeyFunctions.UseSecondary;

        // ---- цель: сначала помеченный враг, иначе (по настройке) под курсором
        var target = Targeting.NearestEnemy(ent, self, Math.Clamp(AimBotSettings.MaxRange, 1f, 100f));

        if (target is null && AimBotSettings.CursorFallback)
        {
            var mouse = IoCManager.Resolve<IInputManager>().MouseScreenPosition;
            if (_viewport is not null)
            {
                var local = mouse.Position - Compat.BoxTopLeft(_bounds);
                var world = _viewport.LocalToWorld(local).Position;
                var under = Targeting.UnderPoint(ent, self, world, MelBotOverlay.SnapTiles);

                // Make Friend не трогаем даже курсором.
                if (under is { } u && Priorities.Get(u) != Priority.Friend)
                    target = u;
            }
        }

        if (target is not { } uid)
        {
            Status = "нет цели";
            return;
        }

        if (transform.GetMapId(uid) != selfMap)
        {
            Status = "цель на другой карте";
            return;
        }

        Target = uid;
        Engaged = true;

        var targetWorld = transform.GetWorldPosition(uid);
        Distance = (targetWorld - SelfWorld).Length();

        // ---- профиль оружия
        var now = timing.CurTime;

        // Чистим трекер скоростей, чтобы он не рос вместе с числом встреченных
        // персонажей: записи старше пяти секунд уже бесполезны.
        if (Tracked.Count > 256)
        {
            foreach (var (key, value) in Tracked.ToList())
            {
                if (now - value.At > TimeSpan.FromSeconds(5))
                    Tracked.Remove(key);
            }
        }

        var info = GunApi.Read(ent, gunUid, gun);
        Hitscan = info.Hitscan;
        Wielded = info.Wielded;
        RequiresWield = info.RequiresWield;
        AmmoCount = info.AmmoCount;
        AmmoMax = info.AmmoMax;

        bool combat = ent.TryGetComponent(self, out CombatModeComponent? combatComp) && combatComp.IsInCombatMode;
        InCombat = combat;

        // ---- боевой режим и две руки: без них сервер просто не выстрелит
        if (!combat)
        {
            if (AimBotSettings.AutoCombat)
                GunApi.TryCombatMode(ent, self);
        }

        if (RequiresWield && !Wielded)
        {
            if (AimBotSettings.AutoWield)
                GunApi.TryUseInHand(ent, now);
        }

        // ---- куда целимся: упреждение для снарядов
        var gunVel = Velocity(self, SelfWorld, now);
        var targetVel = Velocity(uid, targetWorld, now);
        var speed = gun.ProjectileSpeedModified > 0f
            ? gun.ProjectileSpeedModified
            : gun.ProjectileSpeed;

        AimWorld = AimBotSettings.Lead && !Hitscan
            ? Predict(SelfWorld, targetWorld, targetVel, gunVel, speed)
            : targetWorld;

        AimMap = transform.GetMapCoordinates(uid);

        // ---- разброс: та же формула, что у сервера в GetRecoilAngle
        var half = SpreadHalf(gun);
        SpreadDeg = (float) (half * 180.0 / Math.PI);
        ConeTiles = MathF.Tan(MathF.Min(half, 1.4f)) * Distance;

        // ---- гейты (порядок = приоритет текста в статусе)
        if (!combat)
        {
            Status = AimBotSettings.AutoCombat ? "боевой режим…" : "вне боевого режима";
            WantsFire = false;
        }
        else if (RequiresWield && !Wielded)
        {
            Status = AimBotSettings.AutoWield ? "беру в две руки…" : "нужны две руки";
            WantsFire = false;
        }
        else if (info.NeedsRack)
        {
            Status = info.RackReason;
            WantsFire = false;
        }
        else if (AmmoCount == 0)
        {
            Status = "пусто";
            WantsFire = false;
        }
        else if (AimBotSettings.AccuracyGate && ConeTiles > HitboxRadius * Math.Clamp(AimBotSettings.AccuracyTolerance, 0.1f, 4f))
        {
            Status = $"жду разброс ({SpreadDeg:0.0}°)";
            WantsFire = false;
        }
        else if (AimBotSettings.WallCheck && Blocked(ent, self, selfMap, SelfWorld, AimWorld, uid))
        {
            Status = "стена";
            WantsFire = false;
        }
        else if (AimBotSettings.FriendlyCheck && FriendlyOnLine(ent, self, uid, SelfWorld, AimWorld))
        {
            Status = "свой на линии";
            WantsFire = false;
        }
        else
        {
            Status = "";
            WantsFire = true;
        }

        // Полуавтомат: за один зажим — один выстрел (ShotCounter), поэтому между
        // выстрелами кнопку надо отпускать. Burst и FullAuto держим сами.
        if (gun.SelectedMode == SelectiveFire.SemiAuto && !gun.BurstActivated && gun.ShotCounter != 0)
            WantsFire = false;

        // ---- подменять ли курсор
        // Цель за краем вьюпорта: PixelToMap вернёт Nullspace, выстрела не будет.
        // Проверяем всегда — это не про стены, а про то, куда вообще можно навести.
        var screen = ScreenAim;
        var onScreen = OnScreen(screen);

        if (!onScreen)
        {
            Status = "цель за экраном";
            WantsFire = false;
        }

        TriggerOwned = AimBotSettings.AutoFire && _viewport is not null && onScreen;
        var held = KeyDown(FireKey);
        Aiming = _viewport is not null && onScreen && (TriggerOwned || held);

        Why(Status);
    }

    // Раз в секунду пишем в лог причину, по которой не стреляем: иначе лог
    // раздувается (клиент пишет его синхронно), а без лога непонятно, почему
    // бот стоит.
    private static void Why(string status)
    {
        if (status.Length == 0)
        {
            _lastWhy = "";
            return;
        }

        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        if (status == _lastWhy && now - _lastWhyAt < TimeSpan.FromSeconds(1))
            return;

        // Разные причины могут чередоваться кадр через кадр — общий тормоз.
        if (now - _lastWhyAt < TimeSpan.FromMilliseconds(500))
            return;

        _lastWhy = status;
        _lastWhyAt = now;
        Console.WriteLine($"[SsMod] аимбот: {status}");
    }

    private static void Reset()
    {
        Engaged = false;
        WantsFire = false;
        Aiming = false;
        TriggerOwned = false;
        Target = null;
        Distance = 0f;
        SpreadDeg = 0f;
        ConeTiles = 0f;
        Status = "";
        Hitscan = false;
        Wielded = false;
        RequiresWield = false;
        InCombat = false;
    }

    // ------------------------------------------------------------ математика

    // Серверный GetRecoilAngle: CurrentAngle = clamp(CurrentAngle + AngleIncrease
    // - AngleDecay * (t - LastFire), MinAngle, MaxAngle), а сам выстрел отклоняется
    // на CurrentAngle * U(-0.5, 0.5) — берём худший случай, половину угла.
    //
    // LastFire на клиент не приезжает (DataField без AutoNetworkedField), поэтому
    // время с прошлого выстрела неизвестно. Считаем без затухания — это может
    // заставить подождать лишний кадр, но никогда не разрешит выстрел с завышенной
    // точностью.
    private static float SpreadHalf(GunComponent gun)
    {
        var cur = gun.CurrentAngle.Theta + gun.AngleIncreaseModified.Theta;

        var min = gun.MinAngleModified.Theta;
        var max = gun.MaxAngleModified.Theta;
        cur = Math.Clamp(cur, Math.Min(min, max), Math.Max(min, max));

        return MathF.Abs((float) cur) * 0.5f;
    }

    // Упреждение: снаряд летит со скоростью speed + скорость стрелка, цель тоже
    // движется. Решаем итеративно: t = |цель(t) - мы| / speed.
    private static Vector2 Predict(Vector2 from, Vector2 target, Vector2 targetVel, Vector2 gunVel, float speed)
    {
        if (speed <= 0.01f)
            return target;

        var rel = targetVel - gunVel;
        var t = 0f;

        for (var i = 0; i < 3; i++)
        {
            var aim = target + rel * t;
            t = (aim - from).Length() / speed;
        }

        return target + rel * t;
    }

    // Скорость по разнице интерполированных позиций: клиент не симулирует
    // чужие тела, поэтому PhysicsComponent.LinearVelocity брать нельзя.
    private static Vector2 Velocity(EntityUid uid, Vector2 pos, TimeSpan now)
    {
        if (Tracked.TryGetValue(uid, out var prev) && now > prev.At)
        {
            var dt = (float) (now - prev.At).TotalSeconds;

            if (dt > 0.001f)
            {
                var v = (pos - prev.Pos) / dt;
                if (v.LengthSquared() > MaxTrackSpeed * MaxTrackSpeed)
                    v = Vector2.Normalize(v) * MaxTrackSpeed;

                // Сглаживание: интерполяция даёт рывки на смене тика.
                var smooth = Vector2.Lerp(prev.Vel, v, 0.5f);
                Tracked[uid] = (pos, smooth, now);
                return smooth;
            }
        }

        Tracked[uid] = (pos, Vector2.Zero, now);
        return Vector2.Zero;
    }

    // Луч по маске снарядов (Impassable | BulletImpassable — ровно то, во что
    // бьются пули). Живых пропускаем: дружеский огонь считается отдельно,
    // и цель сама по себе «стеной» не является.
    private static bool Blocked(IEntityManager ent, EntityUid self, MapId map, Vector2 from, Vector2 to, EntityUid target)
    {
        try
        {
            var dir = to - from;
            var len = dir.Length();
            if (len < 0.05f)
                return false;

            var physics = ent.System<SharedPhysicsSystem>();
            const int mask = (int) (CollisionGroup.Impassable | CollisionGroup.BulletImpassable);
            var ray = new CollisionRay(from, dir / len, mask);

            var nearest = float.MaxValue;

            foreach (var hit in physics.IntersectRay(map, ray, len, self, false))
            {
                if (hit.Distance <= 0f || hit.Distance >= len)
                    continue;

                if (hit.HitEntity == self || hit.HitEntity == target)
                    continue;

                if (ent.HasComponent<MobStateComponent>(hit.HitEntity))
                    continue;

                nearest = MathF.Min(nearest, hit.Distance);
            }

            return nearest < float.MaxValue;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] аимбот, стены: {e.Message}");
            return false;
        }
    }

    // Союзник (Make Friend) ровно на линии огня: пуля пройдёт через него.
    private static bool FriendlyOnLine(IEntityManager ent, EntityUid self, EntityUid target, Vector2 from, Vector2 to)
    {
        var transform = ent.System<SharedTransformSystem>();
        var map = transform.GetMapId(self);
        var seg = to - from;
        var segLen = seg.Length();
        if (segLen < 0.05f)
            return false;

        foreach (var uid in Targeting.Alive(ent))
        {
            if (uid == self || uid == target)
                continue;

            if (Priorities.Get(uid) != Priority.Friend)
                continue;

            if (transform.GetMapId(uid) != map)
                continue;

            var p = transform.GetWorldPosition(uid);
            var t = Vector2.Dot(p - from, seg) / (segLen * segLen);
            if (t <= 0f || t >= 1f)
                continue;

            var closest = from + seg * t;
            if ((p - closest).Length() < HitboxRadius)
                return true;
        }

        return false;
    }

    // Чтение кнопки в обход собственного патча (иначе увидим то, что сами же
    // и подставили).
    private static bool KeyDown(BoundKeyFunction function)
    {
        var input = IoCManager.Resolve<IEntityManager>().System<Robust.Client.GameObjects.InputSystem>();

        Bypass = true;
        try
        {
            return input.CmdStates.GetState(function) == BoundKeyState.Down;
        }
        finally
        {
            Bypass = false;
        }
    }
}

// ---------------------------------------------------------------------------
// Чтение механик оружия с защитой от расхождений версий.
//
// Мод собирается против 276, а играет в том числе на 277 (fish). Часть
// компонентов и полей есть только в 277 (Pump, BoltClosed, Enabled-поля
// провайдеров), часть переехала (BallisticAmmoProviderComponent.Entities —
// List<NetEntity> вместо List<EntityUid>). Прямая ссылка на такой член вшивается
// в IL и падает на JIT, поэтому всё «моложе 276» берём по имени.
// ---------------------------------------------------------------------------
internal static class GunApi
{
    private static bool _resolved;

    private static Type? _requiresWield;
    private static Type? _hitscanAmmo;
    private static Type? _hitscanCartridge;
    private static Type? _ballistic;
    private static Type? _battery;
    private static Type? _chamber;
    private static Type? _cartridge;

    private static void Resolve()
    {
        if (_resolved)
            return;

        _resolved = true;

        _requiresWield = Compat.FindType("Content.Shared.Weapons.Ranged.Components.GunRequiresWieldComponent");
        _hitscanAmmo = Compat.FindType("Content.Shared.Weapons.Hitscan.Components.HitscanAmmoComponent");
        _hitscanCartridge = Compat.FindType("Content.Shared._Starlight.Weapon.Components.HitScanCartridgeAmmoComponent")
                            ?? Compat.FindType("Content.Shared._Starlight.Weapon.Components.HitscanCartridgeAmmoComponent");
        _ballistic = Compat.FindType("Content.Shared.Weapons.Ranged.Components.BallisticAmmoProviderComponent");
        _battery = Compat.FindType("Content.Shared.Weapons.Ranged.Components.BatteryAmmoProviderComponent");
        _chamber = Compat.FindType("Content.Shared.Weapons.Ranged.Components.ChamberMagazineAmmoProviderComponent");
        _cartridge = Compat.FindType("Content.Shared.Weapons.Ranged.Components.CartridgeAmmoComponent");
    }

    internal readonly struct Info
    {
        public readonly bool Hitscan;
        public readonly bool Wielded;
        public readonly bool RequiresWield;
        public readonly bool NeedsRack;
        public readonly string RackReason;
        public readonly int AmmoCount;
        public readonly int AmmoMax;

        public Info(bool hitscan, bool wielded, bool requiresWield, bool needsRack, string rackReason,
            int ammoCount, int ammoMax)
        {
            Hitscan = hitscan;
            Wielded = wielded;
            RequiresWield = requiresWield;
            NeedsRack = needsRack;
            RackReason = rackReason;
            AmmoCount = ammoCount;
            AmmoMax = ammoMax;
        }
    }

    public static Info Read(IEntityManager ent, EntityUid gun, GunComponent comp)
    {
        Resolve();

        var wielded = ent.TryGetComponent(gun, out WieldableComponent? wieldable) && wieldable.Wielded;
        var requiresWield = _requiresWield != null && ent.HasComponent(gun, _requiresWield);

        var (count, max) = CountAmmo(ent, gun);

        // Болтовка: BoltClosed == false — сервер не отдаст патрон
        // ("gun-chamber-bolt-ammo").
        var needsRack = false;
        var reason = "";

        if (_chamber != null && ent.TryGetComponent(gun, _chamber, out var chamber))
        {
            var bolt = Get<bool?>(chamber, "BoltClosed");
            if (bolt == false)
            {
                needsRack = true;
                reason = "затвор открыт";
            }
        }

        // Помпа: верхний патрон в провайдере уже стреляный — пока не передёрнуть,
        // BallisticAmmoProvider берёт именно его и возвращает "gun-ballistic-cycle".
        if (!needsRack && _ballistic != null && ent.TryGetComponent(gun, _ballistic, out var ballistic))
        {
            if (TopCartridgeSpent(ent, ballistic))
            {
                needsRack = true;
                reason = "передёрните помпу";
            }
        }

        return new Info(IsHitscan(ent, gun), wielded, requiresWield, needsRack, reason, count, max);
    }

    private static bool IsHitscan(IEntityManager ent, EntityUid gun)
    {
        if (_hitscanAmmo is null && _hitscanCartridge is null)
            return false;

        if (HasAny(ent, gun))
            return true;

        // Патронник/магазин: ищем сам боеприпас во вложенных контейнерах.
        return Containers(ent, gun).Any(uid => HasAny(ent, uid));
    }

    private static bool HasAny(IEntityManager ent, EntityUid uid)
    {
        return (_hitscanAmmo != null && ent.HasComponent(uid, _hitscanAmmo))
               || (_hitscanCartridge != null && ent.HasComponent(uid, _hitscanCartridge));
    }

    // Сумма по всем провайдерам, до которых дотягиваемся: сам ствол, магазин,
    // патронник. Значения берём из тех же полей, что читает клиентский UI.
    private static (int Count, int Max) CountAmmo(IEntityManager ent, EntityUid gun)
    {
        var count = 0;
        var max = 0;
        var found = false;

        foreach (var uid in Containers(ent, gun).Prepend(gun))
        {
            if (_ballistic != null && ent.TryGetComponent(uid, _ballistic, out var ballistic))
            {
                count += Get<int>(ballistic, "UnspawnedCount") + ListCount(ballistic, "Entities");
                max += Get<int>(ballistic, "Capacity");
                found = true;
                continue;
            }

            if (_battery != null && ent.TryGetComponent(uid, _battery, out var battery))
            {
                count += Get<int>(battery, "Shots");
                max += Get<int>(battery, "Capacity");
                found = true;
                continue;
            }

            if (_cartridge != null && ent.TryGetComponent(uid, _cartridge, out var cartridge))
            {
                max += 1;
                if (!Get<bool>(cartridge, "Spent"))
                    count += 1;
                found = true;
            }
        }

        return found ? (count, max) : (-1, -1);
    }

    private static bool TopCartridgeSpent(IEntityManager ent, object provider)
    {
        if (_cartridge is null)
            return false;

        if (GetField(provider, "Entities") is not IList list || list.Count == 0)
            return false;

        var last = list[list.Count - 1];
        var uid = ToUid(ent, last);
        if (uid is not { } value)
            return false;

        return ent.TryGetComponent(value, _cartridge, out var comp) && Get<bool>(comp, "Spent");
    }

    // Контейнеры на клиенте — EntityUid'ы, а Entities провайдера в 277 лежат
    // NetEntity'ами, в 276 — EntityUid'ами.
    private static EntityUid? ToUid(IEntityManager ent, object? value)
    {
        if (value is EntityUid uid)
            return uid;

        if (value is NetEntity net)
            return ent.GetEntity(net);

        return null;
    }

    // Все сущности внутри контейнеров (на пару уровней: магазин в стволе,
    // патрон в магазине).
    private static IEnumerable<EntityUid> Containers(IEntityManager ent, EntityUid uid, int depth = 3)
    {
        if (depth <= 0)
            yield break;

        if (!ent.TryGetComponent(uid, out ContainerManagerComponent? manager))
            yield break;

        foreach (var (_, container) in manager.Containers)
        {
            foreach (var child in container.ContainedEntities)
            {
                yield return child;

                foreach (var nested in Containers(ent, child, depth - 1))
                    yield return nested;
            }
        }
    }

    // -------------------------------------------------- действия игрока (рефлексия)

    // Взять оружие в две руки — тот же путь, что клик по слоту руки:
    // HandsSystem.UIHandClick шлёт RequestUseInHandEvent, сервер вызывает
    // UseInHandEvent, WieldableComponent переключается.
    //
    // Подписи различаются: 276 — UIHandClick(Entity<HandsComponent>, string, bool),
    // 277 — (Entity<HandsComponent>, string), 290 — (HandsComponent, string).
    // Плюс в 290 нет ActiveHandId: активная рука — это Hand с полем Name.
    private static TimeSpan _lastUseInHand;
    private static bool _useInHandFailed;

    public static void TryUseInHand(IEntityManager ent, TimeSpan now)
    {
        if (_useInHandFailed || now - _lastUseInHand < TimeSpan.FromSeconds(1))
            return;

        _lastUseInHand = now;

        try
        {
            var self = IoCManager.Resolve<IPlayerManager>().LocalEntity;
            if (self is not { Valid: true } player)
                return;

            if (!ent.TryGetComponent(player, out HandsComponent? hands))
                return;

            var hand = ActiveHandName(hands);
            if (hand is null)
                return;

            var systems = IoCManager.Resolve<IEntitySystemManager>();
            var handsType = Compat.FindType("Content.Client.Hands.Systems.HandsSystem");

            if (handsType is null || !systems.TryGetEntitySystem(handsType, out var system) || system is null)
                return;

            var method = handsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "UIHandClick" && m.GetParameters().Length is 2 or 3);

            if (method is null)
            {
                _useInHandFailed = true;
                Console.WriteLine("[SsMod] аимбот: нет HandsSystem.UIHandClick, две руки берём вручную");
                return;
            }

            var parameters = method.GetParameters();

            // Первый аргумент — либо Entity<HandsComponent>, либо сам компонент.
            var first = parameters[0].ParameterType.IsAssignableFrom(typeof(HandsComponent))
                ? (object) hands
                : Activator.CreateInstance(parameters[0].ParameterType, player, hands);

            var args = parameters.Length == 2
                ? new[] { first, hand }
                : new object?[] { first, hand, true };

            method.Invoke(system, args);
        }
        catch (Exception e)
        {
            _useInHandFailed = true;
            Console.WriteLine($"[SsMod] аимбот: не смог взять в две руки ({e.Message})");
        }
    }

    // Имя активной руки без прямой ссылки на поле: в 276/277 это ActiveHandId,
    // в 290 — ActiveHand.Name.
    private static string? ActiveHandName(object hands)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var type = hands.GetType();

        if (type.GetProperty("ActiveHandId", flags)?.GetValue(hands) is string id)
            return id;

        if (type.GetField("ActiveHandId", flags)?.GetValue(hands) is string fieldId)
            return fieldId;

        var active = type.GetProperty("ActiveHand", flags)?.GetValue(hands)
                     ?? type.GetField("ActiveHand", flags)?.GetValue(hands);

        if (active is null)
            return null;

        var handType = active.GetType();

        return handType.GetProperty("Name", flags)?.GetValue(active) as string
               ?? handType.GetField("Name", flags)?.GetValue(active) as string;
    }

    // Включить боевой режим: это действие ActionCombatModeToggle, а не поле
    // компонента (SetInCombatMode только Dirty — сервер о нём не узнает).
    private static TimeSpan _lastCombat;
    private static bool _combatFailed;

    public static void TryCombatMode(IEntityManager ent, EntityUid self)
    {
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        if (_combatFailed || now - _lastCombat < TimeSpan.FromSeconds(1))
            return;

        _lastCombat = now;

        try
        {
            if (!ent.TryGetComponent(self, out CombatModeComponent? combat) || combat.CombatToggleActionEntity is not { } actionUid)
                return;

            // Тип компонента действия берём из самого метода TriggerAction:
            // в 277 это Content.Shared.Actions.Components.ActionComponent, в 290 —
            // BaseActionComponent, и зашивать имя нельзя.
            var actionsType = Compat.FindType("Content.Client.Actions.ActionsSystem");
            if (actionsType is null)
                return;

            var systems = IoCManager.Resolve<IEntitySystemManager>();
            if (!systems.TryGetEntitySystem(actionsType, out var system) || system is null)
                return;

            var method = actionsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "TriggerAction" && m.GetParameters().Length == 1);

            if (method is null)
            {
                _combatFailed = true;
                return;
            }

            var parameter = method.GetParameters()[0].ParameterType;
            if (!parameter.IsGenericType || parameter.GetGenericTypeDefinition() != typeof(Entity<>))
            {
                _combatFailed = true;
                return;
            }

            var actionType = parameter.GetGenericArguments()[0];
            if (!ent.TryGetComponent(actionUid, actionType, out var action) || action is null)
                return;

            var entity = Activator.CreateInstance(parameter, actionUid, action);
            method.Invoke(system, new[] { entity });
        }
        catch (Exception e)
        {
            _combatFailed = true;
            Console.WriteLine($"[SsMod] аимбот: боевой режим не включился ({e.Message})");
        }
    }

    // ------------------------------------------------------------------ рефлексия

    private static FieldInfo? GetField(object? obj, string name)
    {
        return obj?.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
    }

    private static T Get<T>(object? obj, string name)
    {
        var field = GetField(obj, name);
        if (field?.GetValue(obj) is T value)
            return value;

        return default!;
    }

    private static int ListCount(object? obj, string name)
    {
        return GetField(obj, name)?.GetValue(obj) is IList list ? list.Count : 0;
    }
}

// ---------------------------------------------------------------------------
// Патчи клиента: подмена курсора и кнопки стрельбы на время GunSystem.Update.
// ---------------------------------------------------------------------------

// На время Update пушки помечаем, что мы внутри: подменять MouseScreenPosition
// глобально нельзя — от него зависит UI (наведение, тултипы).
[HarmonyLib.HarmonyPatch(typeof(Content.Client.Weapons.Ranged.Systems.GunSystem),
    nameof(Content.Client.Weapons.Ranged.Systems.GunSystem.Update))]
internal static class AimBotGunUpdatePatch
{
    private static void Prefix()
    {
        AimBot.InGunUpdate = true;
        AimBot.Tick();
    }

    private static void Postfix()
    {
        AimBot.InGunUpdate = false;
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        AimBot.InGunUpdate = false;
        return __exception;
    }
}

// Клиент берёт точку выстрела через PixelToMap(MouseScreenPosition) — значит,
// достаточно вернуть экранную точку прицела на время Update пушки.
// Мелбот патчит тот же геттер, но при стволе в руке он выключен (см. GunBot).
[HarmonyLib.HarmonyPatch]
internal static class AimBotCursorPatch
{
    private static MethodBase TargetMethod()
    {
        var type = HarmonyLib.AccessTools.TypeByName("Robust.Client.Input.ClydeInputManager")!;
        return HarmonyLib.AccessTools.PropertyGetter(type, "MouseScreenPosition")!;
    }

    private static bool Prefix(ref ScreenCoordinates __result)
    {
        if (!AimBot.InGunUpdate || !AimBot.Aiming || !AimBot.Engaged)
            return true;

        __result = new ScreenCoordinates(AimBot.ScreenAim, AimBot.CursorWindow);
        return false;
    }
}

// Кнопка стрельбы: UseKey у оружия решает, ЛКМ это или ПКМ. Полуавтомату между
// выстрелами кнопку отпускаем (иначе ShotCounter не даст второй выстрел).
[HarmonyLib.HarmonyPatch(typeof(PlayerCommandStates), nameof(PlayerCommandStates.GetState))]
internal static class AimBotFirePatch
{
    private static void Postfix(BoundKeyFunction function, ref BoundKeyState __result)
    {
        if (AimBot.Bypass || !AimBot.InGunUpdate || !AimBot.TriggerOwned || !AimBot.Engaged)
            return;

        if (function != AimBot.FireKey)
            return;

        __result = AimBot.WantsFire ? BoundKeyState.Down : BoundKeyState.Up;
    }
}
