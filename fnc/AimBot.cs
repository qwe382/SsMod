using System.Collections;
using System.Numerics;
using System.Reflection;
using Content.Shared.CombatMode;
using Content.Shared.Hands.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Verbs;
using Content.Shared.Wieldable.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
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
    // Бот стреляет сам: держит кнопку огня, когда цель в красной точке.
    // Включается отдельной галочкой «Авто-стрельба», наводка от неё не зависит.
    public static bool AutoFire = false;

    // Не стрелять, пока конус разброса шире допуска: ждём, пока CurrentAngle
    // затухнет (AngleDecay), вместо очереди в молоко. Включается вместе с
    // авто-стрельбой — это её часть, отдельной галочки нет.
    public static bool AccuracyGate = false;

    // Допуск точности в тайлах (клетках): стреляем, если конус разброса на
    // дистанции до цели не шире клетки.
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

    // Автоматически досылать магазин из свободной руки, когда ствол пуст.
    public static bool AutoReload = true;

    // Автоматически передёргивать затвор/помпу — тем же UseInHand, что и
    // взятие в две руки.
    public static bool AutoRack = true;

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

    // Вьюпорт берём из оверлея: он нужен, если MainViewport ещё недоступен.
    // Основной путь — CombatVisuals.MainViewport (см. Absolute/Screen).
    private static IClydeViewport? _viewport;
    private static UIBox2 _bounds;

    private static readonly Dictionary<EntityUid, (Vector2 Pos, Vector2 Vel, TimeSpan At)> Tracked = new();

    // Линия до цели перекрыта стеной (цвет линии и гейт стрельбы).
    public static bool LineBlocked { get; private set; }

    private static EntityUid _blockedTarget;
    private static TimeSpan _blockedAt;

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

    // Экранная точка прицела в АБСОЛЮТНЫХ пикселях окна: её подставляет патч
    // MouseScreenPosition, поэтому масштаб и положение вьюпорта должны быть уже
    // учтены (см. CombatVisuals.Absolute). Свойство публичное — его читает патч.
    public static Vector2 ScreenAim
    {
        get
        {
            if (CombatVisuals.MainViewport is { } viewport)
                return viewport.WorldToScreen(AimWorld);

            if (_viewport is null)
                return default;

            return _viewport.WorldToLocal(AimWorld) + Compat.BoxTopLeft(_bounds);
        }
    }

    // Мир под настоящим курсором (InGunUpdate должен быть false).
    private static Vector2 CursorWorld()
    {
        var mouse = IoCManager.Resolve<IInputManager>().MouseScreenPosition;

        if (CombatVisuals.MainViewport is { } viewport)
            return viewport.PixelToMap(mouse.Position).Position;

        if (_viewport is null)
            return default;

        var local = mouse.Position - Compat.BoxTopLeft(_bounds);
        return _viewport.LocalToWorld(local).Position;
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

        // Без вьюпорта не знаем, где курсор и куда целиться. Он появляется
        // после первого кадра отрисовки оверлея.
        if (_viewport is null)
        {
            Status = "нет вьюпорта";
            return;
        }

        // ---- цель и точка прицела: ровно те же, что у точек наводки.
        // Считаем их здесь же, чтобы кадр стрельбы и кадр отрисовки видели одну
        // и ту же геометрию. Цель — только тот, кто под курсором.
        PredictPoint.Tick(ent, self, CursorWorld(), KeyDown(FireKey) || AimBotSettings.AutoFire);

        if (PredictPoint.Target is not { } uid || !PredictPoint.HasTarget)
        {
            Status = "нет цели";
            return;
        }

        if (transform.GetMapId(uid) != selfMap)
        {
            Status = "цель на другой карте";
            return;
        }

        // Приоритет — последняя инстанция: даже если цель как-то просочилась
        // в лок (например, друг был помечен уже после захвата), по своим не
        // стреляем и не целимся.
        if (Priorities.Get(uid) == Priority.Friend)
        {
            Status = "цель — союзник";
            return;
        }

        Target = uid;
        Engaged = true;

        var targetWorld = transform.GetWorldPosition(uid);
        Distance = (targetWorld - SelfWorld).Length();

        // Перекрыта ли линия до цели — для цвета линии и гейта «стена».
        // Луч пускаем не чаще 10 раз в секунду и ведём в саму цель, а не в
        // точку предикта (та уходит за угол и даёт мигание).
        var nowBlocked = timing.CurTime;
        if (_blockedTarget != uid || nowBlocked - _blockedAt > TimeSpan.FromMilliseconds(100))
        {
            LineBlocked = Blocked(ent, self, selfMap, SelfWorld, targetWorld, uid);
            _blockedTarget = uid;
            _blockedAt = nowBlocked;
        }

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
        // В боевой режим входим только когда собираемся стрелять: по авто-стрельбе
        // или по зажатой игроком кнопке огня. Иначе бот щёлкал бы режимом на
        // каждом наведении курсора.
        if (!combat && AimBotSettings.AutoCombat && (AimBotSettings.AutoFire || KeyDown(FireKey)))
            GunApi.TryCombatMode(ent, self);

        if (RequiresWield && !Wielded)
        {
            if (AimBotSettings.AutoWield)
                GunApi.TryUseInHand(ent, now);
        }

        // ---- куда целимся: ровно в красную точку, которая видна на экране.
        AimWorld = PredictPoint.Red;
        AimMap = transform.GetMapCoordinates(uid);

        // ---- разброс: та же формула, что у сервера в GetRecoilAngle
        var half = SpreadHalf(gun, now);
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
            if (AimBotSettings.AutoRack && GunApi.TryRack(now))
                GunApi.MarkRacked(gunUid, info.AmmoCount);

            Status = info.RackReason;
            WantsFire = false;
        }
        else if (AmmoCount == 0)
        {
            // Сама перезарядка живёт в Maintain (работает и без цели);
            // здесь только статус и запрет стрельбы.

            Status = "пусто";
            WantsFire = false;
        }
        else if (AimBotSettings.AccuracyGate && ConeTiles > MathF.Max(AimBotSettings.AccuracyTolerance, 0.05f))
        {
            Status = $"жду разброс ({SpreadDeg:0.0}°)";
            WantsFire = false;
        }
        else if (AimBotSettings.WallCheck && LineBlocked)
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
        SsLog.Write($"[SsMod] аимбот: {status}");
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
    private static Angle _spreadSeen;
    private static TimeSpan _spreadAt;

    // Текущий разброс ствола без привязки к цели: градусы (половина конуса, как
    // в SpreadDeg) и радиус конуса в тайлах на заданной дистанции. Нужен
    // индикатору разброса, когда цели нет.
    internal static (float Deg, float Tiles) SpreadAt(EntityUid gunUid, float distance)
    {
        var ent = IoCManager.Resolve<IEntityManager>();

        if (!ent.TryGetComponent(gunUid, out GunComponent? gun))
            return (0f, 0f);

        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        var half = SpreadHalf(gun, now);

        var deg = (float) (half * 180.0 / Math.PI);
        var tiles = MathF.Tan(MathF.Min(half, 1.4f)) * distance;
        return (deg, tiles);
    }

    // Оценка текущего разброса. CurrentAngle приезжает с сервера только в момент
    // выстрела, поэтому затухание (AngleDecay) считаем сами — от времени, когда
    // значение последний раз менялось, как это делает сервер в GetRecoilAngle.
    private static float SpreadHalf(GunComponent gun, TimeSpan now)
    {
        if (gun.CurrentAngle != _spreadSeen)
        {
            _spreadSeen = gun.CurrentAngle;
            _spreadAt = now;
        }

        var since = now > _spreadAt ? (float) (now - _spreadAt).TotalSeconds : 0f;
        var cur = gun.CurrentAngle.Theta + gun.AngleIncreaseModified.Theta - gun.AngleDecayModified.Theta * since;

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

    // Настоящее состояние кнопки огня: по нему держится лок цели, его же
    // читает оверлей.
    internal static bool IsFireHeld()
    {
        try
        {
            return KeyDown(FireKey);
        }
        catch
        {
            return false;
        }
    }    // -------------------------------------------------------- обслуживание ствола

    private static EntityUid _loggedGun;

    // Перезарядка и передёргивание не должны зависеть ни от цели, ни от боевого
    // режима: в Step эти гейты стоят раньше, и без цели ствол не обслуживался
    // вообще. Зовётся из оверлея каждый кадр.
    private static TimeSpan _lastGunErrorAt;

    public static void Maintain(IEntityManager ent, EntityUid self)
    {
        try
        {
            var now = IoCManager.Resolve<IGameTiming>().CurTime;

            // Перезарядка меняет активную руку на время — доводим её до конца,
            // пока ствол ещё не вернулся в активную руку.
            if (GunApi.Reloading)
            {
                GunApi.ServiceReload(ent, self, now);
                return;
            }

            if (!AttackReach.TryGetGun(ent, self, out var gunUid)
                || !ent.TryGetComponent(gunUid, out GunComponent? gun))
                return;

            var info = GunApi.Read(ent, gunUid, gun);

            var wantWield = !info.Wielded
                            && (info.RequiresWield || GunApi.ShouldRewield(gunUid));

            // Во время перезарядки не берём в две руки: перезарядка сама снимает
            // хват ради свободной руки, а этот вызов тут же ставил хват обратно —
            // рука не освобождалась никогда.
            if (wantWield && AimBotSettings.AutoWield && !GunApi.Reloading)
                GunApi.TryUseInHand(ent, now, gunUid);

            if (info.Wielded)
                GunApi.ClearRewield(gunUid);

            // Один раз на ствол — что за оружие и что мы про него видим.
            if (gunUid != _loggedGun)
            {
                _loggedGun = gunUid;
                SsLog.Write($"[SsMod] ствол: {GunApi.Dump(ent, gunUid, info)}");
            }

            if (info.NeedsRack)
            {
                if (AimBotSettings.AutoRack && GunApi.TryRack(now))
                {
                    // Отметку ставим только после реально отправленной команды:
                    // иначе кулдаун съел бы попытку, а «1 раз» уже был бы занят.
                    GunApi.MarkRacked(gunUid, info.AmmoCount);
                }

                return;
            }

            // -1 — патронов не нашли ни в одном провайдере (неизвестный тип
            // оружия); пробуем перезарядиться всё равно, лишнего не будет.
            if (info.AmmoCount <= 0 && AimBotSettings.AutoReload)
                GunApi.BeginReload(ent, self, gunUid, now);
        }
        catch (Exception e)
        {
            // Раньше это писалось каждый кадр (тысячи строк); ошибка не
            // меняется — хватит одной строки в 2 секунды.
            var now = IoCManager.Resolve<IGameTiming>().CurTime;
            if (now - _lastGunErrorAt < TimeSpan.FromSeconds(2))
                return;

            _lastGunErrorAt = now;

            // Первые кадры стека: без них причина «Culture is not supported»
            // в логе ничего не говорит.
            var trace = e.StackTrace?.Split('\n');

            SsLog.Write($"[SsMod] ствол: ошибка — {e.Message}");

            if (trace is not null)
            {
                for (var i = 0; i < Math.Min(3, trace.Length); i++)
                    SsLog.Write($"[SsMod]   {trace[i].Trim()}");
            }
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
    private static Type? _revolver;

    // Тип может найтись в сборке, но не быть зарегистрированным в контент-паке
    // конкретного сервера (на 290/маринах BatteryAmmoProviderComponent — как
    // раз такой случай). Тогда TryGetComponent/HasComponent бросают
    // KeyNotFoundException — считаем, что компонента просто нет.
    private static IComponent? Comp(IEntityManager ent, EntityUid uid, Type? type)
    {
        if (type is null)
            return null;

        try
        {
            return ent.TryGetComponent(uid, type, out var comp) ? comp : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasComp(IEntityManager ent, EntityUid uid, Type? type)
    {
        if (type is null)
            return false;

        try
        {
            return ent.HasComponent(uid, type);
        }
        catch
        {
            return false;
        }
    }

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
        _revolver = Compat.FindType("Content.Shared.Weapons.Ranged.Components.RevolverAmmoProviderComponent");
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

        // Сколько рук нужно освободить для двуручного хвата (0 — одноручное).
        public readonly int FreeHands;

        public Info(bool hitscan, bool wielded, bool requiresWield, bool needsRack, string rackReason,
            int ammoCount, int ammoMax, int freeHands)
        {
            Hitscan = hitscan;
            Wielded = wielded;
            RequiresWield = requiresWield;
            NeedsRack = needsRack;
            RackReason = rackReason;
            AmmoCount = ammoCount;
            AmmoMax = ammoMax;
            FreeHands = freeHands;
        }
    }

    public static Info Read(IEntityManager ent, EntityUid gun, GunComponent comp)
    {
        Resolve();

        var wielded = ent.TryGetComponent(gun, out WieldableComponent? wieldable) && wieldable.Wielded;
        var requiresWield = HasComp(ent, gun, _requiresWield);
        var freeHands = wieldable is null ? 0 : Get<int>(wieldable, "FreeHandsRequired");

        var (count, max) = CountAmmo(ent, gun);

        // Болтовка: BoltClosed == false — сервер не отдаст патрон
        // ("gun-chamber-bolt-ammo").
        var needsRack = false;
        var reason = "";

        if (Comp(ent, gun, _chamber) is { } chamber)
        {
            var bolt = Get<bool?>(chamber, "BoltClosed");
            if (bolt == false)
            {
                needsRack = true;
                reason = "затвор открыт";
            }
        }

        // Патронник пуст, а патроны в магазине есть — выстрела не будет, пока
        // не передёрнуть. Один раз на «заход»: отметка сбрасывается, как только
        // патронник снова заполнился (иначе второй раз за раунд не передёрнет).
        var chamberEmpty = ChamberEmpty(ent, gun);

        if (count > 0 && chamberEmpty)
        {
            if (_emptyChamberGun != gun || _emptyChamberCount != count)
            {
                needsRack = true;
                reason = "пустой патронник";
            }
        }
        else if (!chamberEmpty)
        {
            _emptyChamberGun = EntityUid.Invalid;
            _emptyChamberCount = -1;
        }

        // Помпа: верхний патрон в провайдере уже стреляный — пока не передёрнуть,
        // BallisticAmmoProvider берёт именно его и возвращает "gun-ballistic-cycle".
        if (!needsRack && Comp(ent, gun, _ballistic) is { } ballistic)
        {
            if (TopCartridgeSpent(ent, ballistic))
            {
                needsRack = true;
                reason = "передёрните помпу";
            }
        }

        return new Info(IsHitscan(ent, gun), wielded, requiresWield, needsRack, reason, count, max, freeHands);
    }

    // «Пустой патронник» дёргаем один раз на состояние (ствол + число
    // патронов): если затвор не подхватил патрон, каждый кадр его дёргать
    // бессмысленно.
    private static EntityUid _emptyChamberGun;
    private static int _emptyChamberCount = -1;

    internal static void MarkRacked(EntityUid gun, int count)
    {
        _emptyChamberGun = gun;
        _emptyChamberCount = count;
    }

    // Патронник пуст: слот с "chamber" в имени есть, и он пустой. Если слотов
    // нет (помпа/револьвер), проверка не срабатывает.
    private static bool ChamberEmpty(IEntityManager ent, EntityUid gun)
    {
        var slotsType = Compat.FindType("Content.Shared.Containers.ItemSlots.ItemSlotsComponent");
        if (Comp(ent, gun, slotsType) is not { } slotsComp)
            return false;

        if (GetMember(slotsComp, "Slots") is not IDictionary slots)
            return false;

        var found = false;

        foreach (DictionaryEntry entry in slots)
        {
            if (entry.Key?.ToString()?.Contains("chamber", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            found = true;

            if (ToUid(ent, GetMember(entry.Value, "Item")) is not null)
                return false;
        }

        return found;
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
        return HasComp(ent, uid, _hitscanAmmo) || HasComp(ent, uid, _hitscanCartridge);
    }

    // Заряжено / ёмкость ствола (вместе с вставленным магазином). Для оверлея
    // дуги патронов; те же числа читает и аимбот.
    internal static (int Loaded, int Max) LoadedAmmo(IEntityManager ent, EntityUid gun)
    {
        Resolve();
        return CountAmmo(ent, gun);
    }

    // Патроны во всех магазинах инвентаря: руки и надетые пояс/рюкзак/разгрузка.
    // Сам ствол и его магазин не считаем — это первая цифра в индикаторе.
    internal static int InventoryAmmo(IEntityManager ent, EntityUid self, EntityUid gun)
    {
        Resolve();

        var total = 0;

        if (ent.TryGetComponent(self, out HandsComponent? hands))
        {
            foreach (var (_, item) in HandItems(ent, self, hands))
            {
                if (item is { } held && held != gun)
                    total += MagazineAmmo(ent, held);
            }
        }

        var storageType = Compat.FindType("Content.Shared.Storage.StorageComponent");
        if (storageType is null || !ent.TryGetComponent(self, out ContainerManagerComponent? manager))
            return total;

        foreach (var (_, container) in manager.Containers)
        {
            foreach (var worn in container.ContainedEntities)
            {
                if (!HasComp(ent, worn, storageType))
                    continue;

                foreach (var item in Containers(ent, worn, 2))
                    total += MagazineAmmo(ent, item);
            }
        }

        return total;
    }

    // Патроны предмета, если это магазин. Патрон россыпью (Cartridge) не считаем:
    // в индикаторе просят именно магазины.
    private static int MagazineAmmo(IEntityManager ent, EntityUid uid)
    {
        if (Comp(ent, uid, _ballistic) is null && Comp(ent, uid, _battery) is null)
            return 0;

        var (count, _) = CountAmmo(ent, uid);
        return count > 0 ? count : 0;
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
            if (Comp(ent, uid, _ballistic) is { } ballistic)
            {
                count += Get<int>(ballistic, "UnspawnedCount") + ListCount(ballistic, "Entities");
                max += Get<int>(ballistic, "Capacity");
                found = true;
                continue;
            }

            if (Comp(ent, uid, _battery) is { } battery)
            {
                count += Get<int>(battery, "Shots");
                max += Get<int>(battery, "Capacity");
                found = true;
                continue;
            }

            // Револьвер: патроны лежат в барабане (Chambers), true — не стрелян.
            if (_revolver is not null && Comp(ent, uid, _revolver) is { } revolver)
            {
                max += Get<int>(revolver, "Capacity");

                if (GetField(revolver, "Chambers")?.GetValue(revolver) is Array chambers)
                {
                    foreach (var value in chambers)
                    {
                        if (value is true)
                            count++;
                    }
                }

                found = true;
                continue;
            }

            if (Comp(ent, uid, _cartridge) is { } cartridge)
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

        return Comp(ent, value, _cartridge) is { } comp && Get<bool>(comp, "Spent");
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

    public static void TryUseInHand(IEntityManager ent, TimeSpan now, EntityUid gun = default)
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

            // Знаем, какой именно ствол берём в руки: если он не в активной
            // руке, сначала переключаемся на неё.
            if (gun.Valid)
            {
                string? gunHand = null;

                foreach (var (name, item) in HandItems(ent, player, hands))
                {
                    if (item != gun)
                        continue;

                    gunHand = name;
                    break;
                }

                if (gunHand is null)
                    return;

                if (gunHand != hand)
                {
                    RaiseHandEvent(ent, SetHandEvent, gunHand);
                    return;
                }
            }

            // В активной руке должен быть сам двуручный предмет: иначе мы бы
            // «освобождали» руку, в которой лежит оружие, и выбрасывали его.
            if (HeldInHand(ent, player, hand) is not { } activeItem
                || !ent.HasComponent<WieldableComponent>(activeItem))
                return;

            // Вторую руку освобождать самим не нужно: CanWield считает руку
            // свободной, если из неё можно выронить (CountFreeableHands ->
            // CanDropHeld), а TryWield затем сам дропает помеху
            // (TrySpawnVirtualItemInHand(dropOthers: true)). Раньше мы дропали
            // её клавишей Drop и залипали, если та не срабатывала.

            // Только верба: клик по слоту руки перехватывается пушкой
            // (UseInHand -> OnChamberUse) и ничего не делает.
            TryWieldVerb(activeItem, player, true);
        }
        catch (Exception e)
        {
            _useInHandFailed = true;
            SsLog.Write($"[SsMod] аимбот: не смог взять в две руки ({e.Message})");
        }
    }

    // ---------------------------------------------- освобождение второй руки

    // Прежний автомат «переключиться на вторую руку — выкинуть — вернуться»
    // удалён: TryWield сам выкидывает помеху (TrySpawnVirtualItemInHand с
    // dropOthers), а стадии никогда не запускались (поля рук не заполнялись,
    // см. warning CS0649). TryUseInHand выше делает всё сам.

    // --------------------------------------------------------------- перезарядка

    private static TimeSpan _lastReload;
    private static EntityUid _storage;

    // Перезарядка руками в игре — это «использовать предмет из руки на
    // предмете активной руки»: сервер (SharedHandsSystem.TryInteractHandWithActiveHand)
    // берёт АКТИВНЫЙ предмет как used, а предмет из кликнутой руки — как target.
    // Значит магазин должен быть в активной руке, а ствол — в другой.
    // Поэтому на время перезарядки меняем активную руку, кликаем руку со
    // стволом и возвращаем руку обратно.
    internal enum ReloadStage
    {
        Idle,

        // Двуручный хват занимает вторую руку «виртуальным предметом» — её
        // не хватает для магазина из сумки. Сначала убираем ствол из рук.
        Unwielding,

        // Вторая рука занята посторонним предметом — выбрасываем его.
        FreeingHand,

        // Рюкзак/пояс закрыт: сервер не отдаёт из него предмет, пока не открыт
        // его интерфейс (SharedStorageSystem.ValidateInput -> UI.IsUiOpen).
        OpeningStorage,

        // Магазин ещё в сумке/поясе: попросили сервер вынуть его в руку.
        TakingMag,
        Switched,
        Interacted,

        // После обмена в руке остался пустой магазин — выбрасываем.
        Dropping,
    }

    private const string SetHandEvent = "Content.Shared.Hands.RequestSetHandEvent";
    private const string HandInteractEvent = "Content.Shared.Hands.RequestHandInteractUsingEvent";

    private const string StorageEvent = "Content.Shared.Storage.StorageInteractWithItemEvent";

    private static ReloadStage _stage;
    private static EntityUid _mag;
    private static EntityUid _reloadGun;
    private static EntityUid _rewieldGun;
    private static TimeSpan _unwieldClickAt;
    private static int _unwieldTries;
    private static TimeSpan? _unwieldStableSince;
    private static string? _freeHandName;
    private static int _openTried;
    private static bool _storageSent;

    // Ствол, который мы сами убрали из двух рук ради перезарядки: его надо
    // вернуть в руки, даже если он не требует хвата для стрельбы.
    internal static bool ShouldRewield(EntityUid gun) => gun.Valid && gun == _rewieldGun;

    internal static void ClearRewield(EntityUid gun)
    {
        if (gun == _rewieldGun)
            _rewieldGun = EntityUid.Invalid;
    }
    private static string? _magHand;
    private static string? _gunHand;
    private static TimeSpan _stageAt;

    public static bool Reloading => _stage != ReloadStage.Idle;

    // Начало: магазин ищем сначала в руках, потом в надетых сумках/поясе.
    public static void BeginReload(IEntityManager ent, EntityUid self, EntityUid gun, TimeSpan now)
    {
        if (now - _lastReload < TimeSpan.FromSeconds(0.8))
            return;

        _lastReload = now;

        try
        {
            if (!ent.TryGetComponent(self, out HandsComponent? hands))
                return;

            var active = ActiveHandName(hands);
            if (active is null)
            {
                Diag(ent, gun, hands, self, "нет активной руки");
                return;
            }

            var items = HandItems(ent, self, hands).ToList();

            // Перезаряжаем ту руку, в которой ствол, а не просто активную.
            string? gunHand = null;
            foreach (var (name, item) in items)
            {
                if (item != gun)
                    continue;

                gunHand = name;
                break;
            }

            if (gunHand is null)
            {
                Diag(ent, gun, hands, self, "ствол не в руках");
                return;
            }

            var whitelist = GunSlotWhitelist(ent, gun, out _);

            // Одиночное заряжание: револьвер (барабан) и дробовик
            // (BallisticAmmoProvider прямо на стволе) — магазинного слота нет.
            // Приоритет у револьвера: спидлоадер, затем одиночные патроны;
            // у дробовика — патроны из коробок и россыпью. Коробка патронов —
            // обычный storage, работает тот же путь, что и с магазинами.
            var feed = FeedWhitelist(ent, gun);
            if (whitelist is null && feed is null)
            {
                // Ни одного слота и не одиночное заряжание — перезаряжать
                // нечего. Без этой проверки в «магазин» попадала виртуальная
                // заглушка двуручного хвата.
                Diag(ent, gun, hands, self, "у ствола нет магазинного слота");
                return;
            }

            _singleFeed = feed is not null;
            if (_singleFeed)
            {
                _feedCountBefore = CountAmmo(ent, gun).Count;
                _feedSlow = false;

                // Барабан без свободных гнёзд, но не полный — всё занято
                // стреляными гильзами. Выбрасываем их вербой «Опустошить»,
                // следующая попытка перезарядки пойдёт в пустой барабан.
                if (_revolverComp is not null && Comp(ent, gun, _revolverComp) is { } cylinder)
                {
                    var (cnt, maxCnt) = CountAmmo(ent, gun);
                    var slots = GetMember(cylinder, "AmmoSlots") as IEnumerable;
                    var chambers = GetField(cylinder, "Chambers")?.GetValue(cylinder) as Array;
                    var freeSlots = 0;
                    var index = 0;

                    if (slots is not null)
                    {
                        foreach (var slot in slots)
                        {
                            if (slot is null
                                && chambers is not null
                                && index < chambers.Length
                                && chambers.GetValue(index) is null)
                                freeSlots++;

                            index++;
                        }
                    }

                    if (cnt < maxCnt && freeSlots == 0)
                    {
                        Note("[SsMod] перезарядка: выбрасываю гильзы из барабана");
                        TryEmptyRevolverVerb(gun, self);
                        _lastReload = now + TimeSpan.FromSeconds(1.2);
                        return;
                    }
                }
            }

            // 1) Патроны уже в другой руке — меняем активную руку и вставляем.
            var accept = feed ?? whitelist;
            foreach (var (hand, item) in items)
            {
                if (hand == gunHand || item is not { } mag)
                    continue;

                // Заглушку хвата за магазин не принимаем.
                if (IsVirtual(ent, mag))
                    continue;

                if (_singleFeed ? !IsFeedAmmo(ent, mag, feed) : !WhitelistMatch(accept, ent, mag))
                    continue;

                // Пустой магазин не подсовываем: ItemSlots поменяет его на
                // такой же пустой из ствола. Для одиночного заряжания то же
                // отсекает стреляные гильзы и пустые спидлоадеры.
                if (IsEmptyMag(ent, mag))
                    continue;

                _mag = mag;
                _magHand = hand;
                _gunHand = gunHand;
                _stage = ReloadStage.Switched;
                _stageAt = now;

                SsLog.Write($"[SsMod] перезарядка: беру {Proto(ent, mag)} из руки {hand}");
                RaiseHandEvent(ent, SetHandEvent, hand);
                return;
            }

            // 2) В руках патронов нет — тянем из надетой сумки/пояса/рюкзака.
            var foundFeed = _singleFeed
                ? FindFeedAmmo(ent, self, gun, feed, out var stored, out var storage)
                : FindStoredMag(ent, self, gun, whitelist, out stored, out storage);
            if (!foundFeed)
            {
                // Патроны кончились совсем: без долгой паузы бот долбит
                // перезарядку каждые 0.8 с и заливает лог одной строкой.
                _lastReload = now + TimeSpan.FromSeconds(30);
                Diag(ent, gun, hands, self, "патронов нет ни в руках, ни в сумках (пауза 30 с)");
                return;
            }

            // Коробка патронов — перекачка доафтером, результат ждём дольше.
            _feedSlow = _singleFeed
                        && Comp(ent, stored, _ballistic) is not null
                        && Comp(ent, stored, _cartridge) is null
                        && !HasComp(ent, stored, _speedLoader);

            // Без свободной руки StorageInteractWithItem ничего не вынет.
            string? free = null;
            foreach (var (name, item) in items)
            {
                if (name == gunHand || item is not null)
                    continue;

                free = name;
                break;
            }

            if (free is null)
            {
                // Двуручный хват держит вторую руку «виртуальным предметом»
                // (WieldableSystem.TryWield -> TrySpawnVirtualItemInHand), и
                // магазин туда не взять. Убираем ствол из двух рук — Maintain
                // возьмёт его обратно после перезарядки.
                if (ent.TryGetComponent(gun, out WieldableComponent? wieldable) && wieldable.Wielded)
                {
                    _reloadGun = gun;
                    _rewieldGun = gun;
                    _gunHand = gunHand;
                    _stage = ReloadStage.Unwielding;
                    _stageAt = now;
                    _unwieldTries = 0;
                    _unwieldStableSince = null;

                    Note("[SsMod] перезарядка: убираю ствол из двух рук, чтобы освободить руку");

                    if (active != gunHand)
                        RaiseHandEvent(ent, SetHandEvent, gunHand);

                    return;
                }

                // В другой руке лежит что-то ещё (стекло, бутылка, фонарь) —
                // без свободной руки магазин из сумки не достать. Выбрасываем.
                foreach (var (name, item) in items)
                {
                    if (name == gunHand || item is not { } junk)
                        continue;

                    // Виртуальный предмет хвата выбросить нельзя; если он ещё
                    // висит, сервер вот-вот снимет хват — просто ждём.
                    if (IsVirtual(ent, junk))
                        return;

                    _reloadGun = gun;
                    _freeHandName = name;
                    _gunHand = gunHand;
                    _stage = ReloadStage.FreeingHand;
                    _stageAt = now;

                    Note($"[SsMod] перезарядка: освобождаю {name} от {Proto(ent, junk)}");

                    if (active != name)
                        RaiseHandEvent(ent, SetHandEvent, name);

                    return;
                }

                Diag(ent, gun, hands, self, $"магазин в {Proto(ent, storage)}, но свободной руки нет");
                return;
            }
            // Сервер не отдаёт предмет из сумки, пока не открыт её интерфейс
            // (ValidateInput -> UI.IsUiOpen). Закрытый рюкзак сначала открываем.
            if (!StorageUiOpen(ent, storage))
            {
                _reloadGun = gun;
                _storage = storage;
                _gunHand = gunHand;
                _stage = ReloadStage.OpeningStorage;
                _stageAt = now;
                _openTried = 0;

                Note($"[SsMod] перезарядка: открываю {Proto(ent, storage)}");
                ToggleStorage("OpenBackpack", now);
                return;
            }

            // Важно: сначала переключаемся на свободную руку. Если активной
            // рукой держать ствол, сервер не вынет предмет, а «применит» его
            // к магазину в сумке (OnInteractWithItem).
            _mag = stored;
            _storage = storage;
            _magHand = free;
            _gunHand = gunHand;
            _stage = ReloadStage.TakingMag;
            _stageAt = now;
            _storageSent = false;

            Note($"[SsMod] перезарядка: достаю {Proto(ent, stored)} из {Proto(ent, storage)} (рука {free})");
            RaiseHandEvent(ent, SetHandEvent, free);
        }
        catch (Exception e)
        {
            _stage = ReloadStage.Idle;
            SsLog.Write($"[SsMod] перезарядка: {e.Message}");
        }
    }

    // Продолжение: ждём, пока рука реально переключится, кликаем ствол,
    // возвращаем активную руку.
    public static void ServiceReload(IEntityManager ent, EntityUid self, TimeSpan now)
    {
        try
        {
            var stageLimit = _stage switch
            {
                ReloadStage.Unwielding => 6,
                ReloadStage.Dropping => 2.5,
                // Коробка перекачивает патроны доафтером — стадии с ней живём дольше.
                _ => _feedSlow ? 4 : 2,
            };

            if (now - _stageAt > TimeSpan.FromSeconds(stageLimit))
            {
                if ((_stage != ReloadStage.TakingMag || _storageSent) && _gunHand is { } back)
                    RaiseHandEvent(ent, SetHandEvent, back);

                _stage = ReloadStage.Idle;
                // Пауза перед следующим заходом: без неё неудачная стадия
                // крутилась по кругу и дёргала ствол.
                _lastReload = now + TimeSpan.FromSeconds(10);

                Note("[SsMod] перезарядка: не успела, пауза 10 с");
                return;
            }

            if (!ent.TryGetComponent(self, out HandsComponent? hands))
            {
                _stage = ReloadStage.Idle;
                return;
            }

            var active = ActiveHandName(hands);

            switch (_stage)
            {
                // Открываем хранилище: сначала рюкзак, потом пояс. Если и так не
                // открылось — молча ждём, пока игрок откроет сам (без спама).
                case ReloadStage.OpeningStorage:
                    if (StorageUiOpen(ent, _storage))
                    {
                        _stage = ReloadStage.Idle;
                        _lastReload = TimeSpan.Zero;
                        BeginReload(ent, self, _reloadGun, now);
                        return;
                    }

                    if (now - _stageAt < TimeSpan.FromSeconds(0.7))
                        return;

                    if (_openTried == 0)
                    {
                        _openTried = 1;
                        _stageAt = now;
                        ToggleStorage("OpenBelt", now);
                        return;
                    }

                    // Не долбим закрытую сумку каждые 0.8 с: ждём 10 секунд.
                    _stage = ReloadStage.Idle;
                    _lastReload = now + TimeSpan.FromSeconds(10);
                    Note($"[SsMod] перезарядка: {Proto(ent, _storage)} закрыт, из него не достать");
                    return;

                // Освобождаем занятую посторонним предметом руку: переключились
                // на неё, выбрасываем, потом перезарядка начинается заново.
                case ReloadStage.FreeingHand:
                    if (_freeHandName is not { } freeHand)
                    {
                        _stage = ReloadStage.Idle;
                        return;
                    }

                    if (active != freeHand)
                        return;

                    if (HeldInHand(ent, self, freeHand) is not null)
                    {
                        if (now - _stageAt > TimeSpan.FromSeconds(0.3))
                            TryDrop(now);

                        return;
                    }

                    _stage = ReloadStage.Idle;
                    _lastReload = TimeSpan.Zero;
                    BeginReload(ent, self, _reloadGun, now);
                    return;

                // Убираем ствол из двух рук и сразу пробуем перезарядку заново:
                // виртуальный предмет из второй руки исчезает вместе с хватом.
                case ReloadStage.Unwielding:
                    if (ent.TryGetComponent(_reloadGun, out WieldableComponent? wield) && wield.Wielded)
                    {
                        // Предикт мог мигнуть «снято» и откатиться сервером —
                        // подтверждением считаем только устойчивое состояние.
                        _unwieldStableSince = null;

                        if (active != _gunHand)
                        {
                            if (_gunHand is { } toGun)
                                RaiseHandEvent(ent, SetHandEvent, toGun);

                            return;
                        }

                        // Кликаем не чаще раза в 0.7 с и не сбрасываем _stageAt:
                        // иначе общий таймаут стадии никогда не сработает.
                        if (now - _unwieldClickAt < TimeSpan.FromSeconds(0.7))
                            return;

                        _unwieldClickAt = now;
                        _unwieldTries++;

                        // Первым — штатный путь игрока: клик по слоту руки
                        // (RequestUseInHand -> UseInHand, WieldableSystem стоит
                        // в подписках раньше оружейных систем). Верба — запасной
                        // путь: сервер может не сматчить её по тексту, тогда
                        // клиентский предикт «хват снят» откатывается и ствол
                        // визуально остаётся в двух руках.
                        if (_unwieldTries % 2 == 1)
                        {
                            Note("[SsMod] перезарядка: снимаю хват кликом по слоту");
                            RaiseEvent(ent, "Content.Shared.Hands.RequestUseInHandEvent");
                        }
                        else
                        {
                            Note("[SsMod] перезарядка: снимаю хват вербой");
                            TryWieldVerb(_reloadGun, self, false);
                        }

                        return;
                    }

                    // Не вооружён: ждём устойчивого состояния — предикт мог
                    // мигнуть раньше, чем сервер отклонил команду.
                    _unwieldStableSince ??= now;

                    if (now - _unwieldStableSince < TimeSpan.FromSeconds(0.4))
                        return;

                    if (_unwieldTries > 0)
                        Note("[SsMod] перезарядка: хват снят");

                    _stage = ReloadStage.Idle;
                    _lastReload = TimeSpan.Zero;
                    _unwieldStableSince = null;
                    BeginReload(ent, self, _reloadGun, now);
                    return;

                // Ждём, пока запрошенный магазин доедет из сумки в руку.
                case ReloadStage.TakingMag:
                    if (!_storageSent)
                    {
                        // Активная рука должна быть пустой, иначе сервер не
                        // достаёт предмет, а взаимодействует им с целью.
                        if (active != _magHand)
                            return;

                        // Пока шли сюда, ствол могли уже зарядить (или в
                        // патронник попал патрон) — тогда магазин из сумки не
                        // трогаем, иначе он зря окажется в руке.
                        var (countNow, maxNow) = CountAmmo(ent, _reloadGun);

                        // Одиночное заряжание: достаём следующий патрон, пока
                        // барабан/магазин не полон; магазинному — как раньше,
                        // любого заряда хватит.
                        if (_singleFeed ? countNow >= maxNow : countNow > 0)
                        {
                            _stage = ReloadStage.Idle;
                            _lastReload = TimeSpan.Zero;
                            FinishReload(ent, hands, active);
                            return;
                        }

                        _storageSent = true;
                        _stageAt = now;

                        // Карман (storage == mag): предмет вынимается кликом
                        // по нему с пустой активной рукой, не StorageInteract.
                        if (_storage == _mag)
                            InteractQueue.ClickEntity(ent, _mag);
                        else
                            RaiseEvent(ent, StorageEvent, ent.GetNetEntity(_mag), ent.GetNetEntity(_storage));
                        return;
                    }

                    foreach (var (name, item) in HandItems(ent, self, hands))
                    {
                        if (item != _mag)
                            continue;

                        _stage = ReloadStage.Switched;
                        _stageAt = now;

                        if (active != name)
                        {
                            _magHand = name;
                            RaiseHandEvent(ent, SetHandEvent, name);
                        }

                        return;
                    }

                    return;

                case ReloadStage.Switched:
                    if (active != _magHand)
                        return;

                    if (_gunHand is { } gunHand)
                    {
                        SsLog.Write($"[SsMod] перезарядка: вставляю в ствол из руки {_magHand}");
                        RaiseHandEvent(ent, HandInteractEvent, gunHand);
                    }

                    _stage = ReloadStage.Interacted;
                    _stageAt = now;
                    break;

                case ReloadStage.Interacted:
                    if (now - _stageAt < TimeSpan.FromSeconds(_feedSlow ? 2.5 : 0.35))
                        return;

                    // Осталось что-то в руке: пустой магазин, коробка после
                    // перекачки, спидлоадер. Сначала пробуем убрать в рюкзак/
                    // разгрузку/сумку, выброс на землю — только если некуда.
                    if (_magHand is { } spentHand
                        && HeldInHand(ent, self, spentHand) is { } leftover
                        && !IsVirtual(ent, leftover)
                        && (IsEmptyMag(ent, leftover) || _singleFeed))
                    {
                        _stage = ReloadStage.Dropping;
                        _stageAt = now;
                        _stashTried.Clear();
                        Note($"[SsMod] перезарядка: убираю {Proto(ent, leftover)} в инвентарь");
                        return;
                    }

                    // Одиночное заряжание: вставили патрон — если барабан ещё
                    // не полон и заряд подействовал, берём следующий. Заряд не
                    // подействовал (счёт не вырос) — заканчиваем, не молотим
                    // один и тот же патрон бесконечно.
                    if (_singleFeed)
                    {
                        var (countNow, maxNow) = CountAmmo(ent, _reloadGun);

                        if (countNow > _feedCountBefore && countNow < maxNow)
                        {
                            _stage = ReloadStage.Idle;
                            _lastReload = TimeSpan.Zero;
                            BeginReload(ent, self, _reloadGun, now);
                            return;
                        }
                    }

                    FinishReload(ent, hands, active);
                    return;

                case ReloadStage.Dropping:
                    if (_magHand is not { } dropHand
                        || HeldInHand(ent, self, dropHand) is null)
                    {
                        FinishReload(ent, hands, active);
                        return;
                    }

                    if (now - _stageAt > TimeSpan.FromSeconds(6))
                    {
                        Note("[SsMod] перезарядка: предмет так и остался в руке");
                        FinishReload(ent, hands, active);
                        return;
                    }

                    if (active != dropHand)
                    {
                        RaiseHandEvent(ent, SetHandEvent, dropHand);
                        return;
                    }

                    // Раз в 0.8 с: сперва убираем предмет в сумку/рюкзак/
                    // разгрузку (клик по сумке с предметом в активной руке —
                    // штатная вставка в storage), и только когда хранилища
                    // кончились — выбрасываем на землю.
                    if (now - _dropClickAt < TimeSpan.FromSeconds(0.8))
                        return;

                    _dropClickAt = now;

                    if (NextStash(ent, self) is { } stash)
                    {
                        InteractQueue.ClickEntity(ent, stash);
                        return;
                    }

                    TryDrop(now);
                    return;
            }
        }
        catch (Exception e)
        {
            _stage = ReloadStage.Idle;
            SsLog.Write($"[SsMod] перезарядка: {e.Message}");
        }
    }

    // Перезарядка закончена: возвращаем активную руку на ствол.
    private static void FinishReload(IEntityManager ent, HandsComponent hands, string? active)
    {
        if (_gunHand is { } gunHand && active != gunHand)
            RaiseHandEvent(ent, SetHandEvent, gunHand);

        _stage = ReloadStage.Idle;
        _singleFeed = false;
        _feedCountBefore = -1;
        _feedSlow = false;
    }

    // Клиентское предсказуемое событие (RequestSetHandEvent /
    // RequestHandInteractUsingEvent): тип берём по имени, чтобы не зависеть от
    // сборки, и шлём на сервер через RaisePredictiveEvent<T>.
    private static void RaiseHandEvent(IEntityManager ent, string typeName, string handName)
    {
        RaiseEvent(ent, typeName, handName);
    }

    private static void RaiseEvent(IEntityManager ent, string typeName, params object?[] args)
    {
        var type = Compat.FindType(typeName);

        if (type is null || Activator.CreateInstance(type, args) is not EntityEventArgs evt)
        {
            SsLog.Write($"[SsMod] перезарядка: нет события {typeName}");
            return;
        }

        var method = typeof(IEntityManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "RaisePredictiveEvent" && m.IsGenericMethodDefinition);

        if (method is null)
            return;

        method.MakeGenericMethod(type).Invoke(ent, new object?[] { evt });
    }

    // Магазин в надетой вещи с хранилищем (пояс, рюкзак, разгрузка):
    // перебираем контейнеры игрока, у каждой вещи с StorageComponent — её
    // содержимое. Прятать туда ничего не надо, только читаем.
    private static bool FindStoredMag(IEntityManager ent, EntityUid player, EntityUid gun, object? whitelist,
        out EntityUid mag, out EntityUid storage)
    {
        mag = EntityUid.Invalid;
        storage = EntityUid.Invalid;

        var storageType = Compat.FindType("Content.Shared.Storage.StorageComponent");
        if (storageType is null || !ent.TryGetComponent(player, out ContainerManagerComponent? manager))
            return false;

        foreach (var (_, container) in manager.Containers)
        {
            foreach (var worn in container.ContainedEntities)
            {
                // Карман — не storage: магазин в кармане сам является worn.
                // Помечаем этот случай storage == mag: TakingMag вынет его
                // кликом по предмету, а не StorageInteractWithItem.
                if (worn != gun && !IsVirtual(ent, worn)
                    && WhitelistMatch(whitelist, ent, worn) && !IsEmptyMag(ent, worn))
                {
                    mag = worn;
                    storage = worn;
                    return true;
                }

                if (!HasComp(ent, worn, storageType))
                    continue;

                foreach (var item in Containers(ent, worn, 2))
                {
                    if (item == gun || IsVirtual(ent, item)
                        || !WhitelistMatch(whitelist, ent, item) || IsEmptyMag(ent, item))
                        continue;

                    mag = item;
                    storage = worn;
                    return true;
                }
            }
        }

        return false;
    }

    // ------------------------------------------------ одиночное заряжание

    private static bool _singleFeed;
    private static int _feedCountBefore = -1;

    // Коробка перекачивает боезапас доафтером (~секунда) — результат ждём
    // дольше, чем одиночную вставку патрона.
    private static bool _feedSlow;
    private static Type? _speedLoader;
    private static Type? _revolverComp;

    // Белый список одиночного заряжания: у револьвера — барабан
    // (RevolverAmmoProvider), у дробовика — сам ствол (BallisticAmmoProvider).
    // Возвращает EntityWhitelist провайдера или null, если ствол не такой.
    private static object? FeedWhitelist(IEntityManager ent, EntityUid gun)
    {
        _revolverComp ??= Compat.FindType("Content.Shared.Weapons.Ranged.Components.RevolverAmmoProviderComponent");

        if (_revolverComp is not null && Comp(ent, gun, _revolverComp) is { } revolver)
            return GetMember(revolver, "Whitelist");

        return Comp(ent, gun, _ballistic) is { } ballistic ? GetMember(ballistic, "Whitelist") : null;
    }

    // Годится ли предмет как заряд для одиночного заряжания: спидлоадер
    // (только револьвер, с живыми патронами) или нестреляный патрон.
    private static bool IsFeedAmmo(IEntityManager ent, EntityUid uid, object? feedWhitelist)
    {
        _speedLoader ??= Compat.FindType("Content.Shared.Weapons.Ranged.Components.SpeedLoaderComponent");

        if (_speedLoader is not null
            && HasComp(ent, uid, _speedLoader)
            && WhitelistMatch(feedWhitelist, ent, uid))
            return LiveRounds(ent, uid) > 0;

        if (Comp(ent, uid, _cartridge) is { } cartridge)
            return !Get<bool>(cartridge, "Spent") && WhitelistMatch(feedWhitelist, ent, uid);

        return false;
    }

    // Живые патроны в провайдере (магазин/спидлоадер/коробка): нерасходованные
    // плюс реальные патроны, которые ещё не стреляны. Считать все сущности
    // нельзя — стреляные гильзы тоже лежат в списке, и пустой на деле
    // спидлоадер выглядел заряженным.
    private static int LiveRounds(IEntityManager ent, EntityUid uid)
    {
        if (Comp(ent, uid, _ballistic) is not { } ballistic)
            return 0;

        var live = Get<int>(ballistic, "UnspawnedCount");

        if (GetMember(ballistic, "Entities") is IEnumerable list)
        {
            foreach (var one in list)
            {
                if (ToUid(ent, one) is not { } round)
                    continue;

                if (Comp(ent, round, _cartridge) is { } cartridge && !Get<bool>(cartridge, "Spent"))
                    live++;
            }
        }

        return live;
    }

    // Пересекаются ли белые списки (по тегам) — так коробка патронов
    // матчится к стволу: у обоих тег ShellShotgun.
    //
    // ЗАГЛУШКА, доделать: у части стволов (двухстволка) белый список
    // провайдера пуст, и коробка к ним не матчится — из-за этого дробовик
    // не перезаряжается из коробки. Нужно придумать запасной матчинг
    // (например, по прототипу патрона в коробке против whitelist ствола
    // или по тегу калибра у самого ствола). Пока без списка — не совпадает,
    // чтобы не совать чужой калибр.
    private static bool TagsOverlap(object? a, object? b)
    {
        if (a is null || b is null)
            return false;

        var tagsA = GetMember(a, "Tags") as IEnumerable;
        var tagsB = GetMember(b, "Tags") as IEnumerable;
        if (tagsA is null || tagsB is null)
            return false;

        var set = new HashSet<string>();
        foreach (var tag in tagsA)
        {
            var id = tag?.ToString();
            if (!string.IsNullOrEmpty(id))
                set.Add(id!);
        }

        foreach (var tag in tagsB)
        {
            var id = tag?.ToString();
            if (!string.IsNullOrEmpty(id) && set.Contains(id!))
                return true;
        }

        return false;
    }

    // Поиск заряда в сумках. Приоритет: револьверу — спидлоадер, дробовику —
    // коробку патронов (BallisticAmmoProvider с совпадающим тегом: клик
    // коробкой по стволу переносит весь боезапас), и только потом одиночные
    // патроны. Коробка — не storage: она сама провайдер, «достать» из неё
    // нельзя, можно только перекачать кликом по стволу.
    private static bool FindFeedAmmo(IEntityManager ent, EntityUid player, EntityUid gun, object? feedWhitelist,
        out EntityUid mag, out EntityUid storage)
    {
        mag = EntityUid.Invalid;
        storage = EntityUid.Invalid;

        var storageType = Compat.FindType("Content.Shared.Storage.StorageComponent");
        if (storageType is null || !ent.TryGetComponent(player, out ContainerManagerComponent? manager))
            return false;

        _speedLoader ??= Compat.FindType("Content.Shared.Weapons.Ranged.Components.SpeedLoaderComponent");
        var isRevolver = _revolverComp is not null && HasComp(ent, gun, _revolverComp);

        EntityUid loose = default;
        EntityUid looseBox = default;
        EntityUid box = default;
        EntityUid boxHome = default;

        foreach (var (_, container) in manager.Containers)
        {
            foreach (var worn in container.ContainedEntities)
            {
                // Спидлоадер или патрон прямо в кармане — сам worn
                // (storage == mag, карманный забор кликом по предмету).
                if (isRevolver && worn != gun && !IsVirtual(ent, worn)
                    && _speedLoader is not null && HasComp(ent, worn, _speedLoader)
                    && WhitelistMatch(feedWhitelist, ent, worn)
                    && LiveRounds(ent, worn) > 0)
                {
                    mag = worn;
                    storage = worn;
                    return true;
                }

                if (loose == default && worn != gun && !IsVirtual(ent, worn)
                    && Comp(ent, worn, _cartridge) is { } wornCart
                    && !Get<bool>(wornCart, "Spent")
                    && WhitelistMatch(feedWhitelist, ent, worn))
                {
                    loose = worn;
                    looseBox = worn;
                }

                foreach (var item in Containers(ent, worn, 2))
                {
                    if (item == gun || IsVirtual(ent, item))
                        continue;

                    // Спидлоадер — только револьверу, высший приоритет.
                    if (isRevolver
                        && _speedLoader is not null
                        && HasComp(ent, item, _speedLoader)
                        && WhitelistMatch(feedWhitelist, ent, item)
                        && LiveRounds(ent, item) > 0)
                    {
                        mag = item;
                        storage = worn;
                        return true;
                    }

                    // Коробка в подсумке/рюкзаке — это item.
                    TryBox(item, worn, ref box, ref boxHome);

                    // Одиночный патрон запоминаем и берём, если приоритетнее
                    // ничего не нашлось.
                    if (loose == default
                        && Comp(ent, item, _cartridge) is { } cartridge
                        && !Get<bool>(cartridge, "Spent")
                        && WhitelistMatch(feedWhitelist, ent, item))
                    {
                        loose = item;
                        looseBox = worn;
                    }
                }
            }
        }

        if (isRevolver && loose != default)
        {
            mag = loose;
            storage = looseBox;
            return true;
        }

        if (!isRevolver && box != default)
        {
            mag = box;
            storage = boxHome;
            return true;
        }

        if (loose != default)
        {
            mag = loose;
            storage = looseBox;
            return true;
        }

        return false;

        // Коробка патронов: сама BallisticAmmoProvider с живыми патронами и
        // совпадающими со стволом тегами. Лежит в подсумке/рюкзаке (storage) —
        // оттуда её достаёт тот же механизм, что и магазины. Оружие и
        // спидлоадеры коробками не считаем.
        void TryBox(EntityUid uid, EntityUid home, ref EntityUid found, ref EntityUid foundHome)
        {
            if (found != default || isRevolver || uid == gun
                || !HasComp(ent, home, storageType)
                || Comp(ent, uid, _ballistic) is not { } ballistic
                || HasComp(ent, uid, _speedLoader)
                || ent.HasComponent<GunComponent>(uid)
                || LiveRounds(ent, uid) <= 0
                || !TagsOverlap(GetMember(ballistic, "Whitelist"), feedWhitelist))
                return;

            found = uid;
            foundHome = home;
        }
    }

    // Первый слот ствола с белым списком (не патронник): его список решает,
    // чем можно заряжаться; занятость слота — вставлять нечего.
    private static object? GunSlotWhitelist(IEntityManager ent, EntityUid gun, out bool occupied)
    {
        occupied = false;

        var slotsType = Compat.FindType("Content.Shared.Containers.ItemSlots.ItemSlotsComponent");
        if (Comp(ent, gun, slotsType) is not { } slotsComp)
            return null;

        if (GetMember(slotsComp, "Slots") is not IDictionary slots)
            return null;

        object? best = null;

        foreach (DictionaryEntry entry in slots)
        {
            var key = entry.Key?.ToString() ?? "";
            if (key.Contains("chamber", StringComparison.OrdinalIgnoreCase))
                continue;

            var list = GetMember(entry.Value, "Whitelist");
            if (list is null)
                continue;

            best ??= list;

            if (ToUid(ent, GetMember(entry.Value, "Item")) is not null)
            {
                occupied = true;
                break;
            }
        }

        return best;
    }

    // Пары «имя руки -> предмет в ней». Предмет лежит в контейнере с именем
    // руки: SharedHandsSystem.AddHand делает EnsureContainer(handName), а
    // DoPickup кладёт предмет туда же. Поле Hand в HandsComponent — описание
    // руки (расположение, метки), предмета в нём нет ни в одной версии.
    private static IEnumerable<(string Name, EntityUid? Item)> HandItems(IEntityManager ent, EntityUid player,
        HandsComponent hands)
    {
        foreach (var name in HandNames(hands))
            yield return (name, HeldInHand(ent, player, name));
    }

    // Имена рук: в 276/277 это ключи Hands/SortedHands, в 290 — список Hand с
    // полем Name.
    private static IEnumerable<string> HandNames(HandsComponent hands)
    {
        if (GetMember(hands, "SortedHands") is IEnumerable<string> sorted)
        {
            foreach (var name in sorted)
                yield return name;

            yield break;
        }

        var raw = GetMember(hands, "Hands");

        if (raw is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key?.ToString() is { } key)
                    yield return key;
            }

            yield break;
        }

        if (raw is IEnumerable list)
        {
            foreach (var hand in list)
            {
                if (GetMember(hand, "Name")?.ToString() is { } name)
                    yield return name;
            }
        }
    }

    private static EntityUid? HeldInHand(IEntityManager ent, EntityUid player, string handName)
    {
        if (!ent.TryGetComponent(player, out ContainerManagerComponent? manager))
            return null;

        if (!manager.Containers.TryGetValue(handName, out var container))
            return null;

        foreach (var child in container.ContainedEntities)
            return child;

        return null;
    }

    // ------------------------------------------------------------ передёргивание

    private static TimeSpan _lastRack;
    private static bool _cockGunFailed;

    // Передёрнуть затвор/помпу. Основной путь — верба «Rack» (та же, что в
    // контекстном меню): клиент шлёт ExecuteVerbEvent, сервер вызывает
    // UseChambered. Резерв — клавиша CockGun через incmd (она есть только в 277,
    // и то не во всех контент-паках). UseInHand не годится: WieldableComponent
    // перехватывает его раньше пушки (UnwieldOnUse = true).
    // Возвращает true, если команда действительно отправлена.
    public static bool TryRack(TimeSpan now)
    {
        if (_cockGunFailed || now - _lastRack < TimeSpan.FromSeconds(0.7))
            return false;

        _lastRack = now;

        try
        {
            var ent = IoCManager.Resolve<IEntityManager>();
            var player = IoCManager.Resolve<IPlayerManager>().LocalEntity;

            if (player is { Valid: true } user
                && AttackReach.TryGetGun(ent, user, out var gun)
                && TryRackVerb(gun, user))
            {
                SsLog.Write("[SsMod] передёргивание: Rack");
                return true;
            }

            // Резерв: отдельная клавиша CockGun (появилась в 277).
            var console = IoCManager.Resolve<IConsoleHost>();

            if (!console.AvailableCommands.ContainsKey("incmd"))
            {
                _cockGunFailed = true;
                SsLog.Write("[SsMod] передёргивание: нет вербы Rack и нет команды incmd");
                return false;
            }

            // Состояние клавиши в incmd — это BoundKeyState: Up/Down.
            // ("Pressed" там не парсится и команда молча падала.)
            console.ExecuteCommand("incmd CockGun Down");
            console.ExecuteCommand("incmd CockGun Up");
            SsLog.Write("[SsMod] передёргивание: CockGun");
            return true;
        }
        catch (Exception e)
        {
            _cockGunFailed = true;
            SsLog.Write($"[SsMod] передёргивание: {e.Message}");
            return false;
        }
    }

    // Верба «Rack»/«Передёрнуть» на стволе (та же, что в контекстном меню).
    private static bool TryRackVerb(EntityUid gun, EntityUid user)
    {
        var verbs = LocalVerbs(gun, user, typeof(ActivationVerb));
        if (verbs is null)
            return false;

        var rackText = Loc.GetString("gun-chamber-rack");

        foreach (var verb in verbs)
        {
            // Текст — основной признак; имя сгенерированной лямбды из
            // OnChamberActivationVerb — запасной (локаль сервера может отличаться).
            var isRack = verb.Text == rackText
                         || (verb.Act?.Method.Name.Contains("OnChamberActivationVerb") ?? false);

            if (!isRack)
                continue;

            return ExecuteVerb(gun, verb);
        }

        return false;
    }

    // Список локальных верб цели. GetLocalVerbs объявлен в SharedVerbSystem и
    // наследуется клиентской Content.Client.Verbs.VerbSystem; в 276 у него есть
    // необязательный force, в 290 может не быть — подставляем дефолты.
    private static SortedSet<Verb>? LocalVerbs(EntityUid target, EntityUid user, Type type)
    {
        try
        {
            var systemType = Compat.FindType("Content.Client.Verbs.VerbSystem");
            if (systemType is null)
                return null;

            var systems = IoCManager.Resolve<IEntitySystemManager>();
            if (!systems.TryGetEntitySystem(systemType, out var system) || system is null)
                return null;

            var getVerbs = systemType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetLocalVerbs"
                                     && m.GetParameters().Length >= 3
                                     && m.GetParameters()[2].ParameterType == typeof(Type));

            if (getVerbs is null)
                return null;

            var parameters = getVerbs.GetParameters();
            var args = new object?[parameters.Length];
            args[0] = target;
            args[1] = user;
            args[2] = type;

            for (var i = 3; i < parameters.Length; i++)
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;

            return getVerbs.Invoke(system, args) as SortedSet<Verb>;
        }
        catch (Exception e)
        {
            Note($"[SsMod] вербы: {e.Message}");
            return null;
        }
    }

    // Выполнить вербу: клиент шлёт ExecuteVerbEvent, сервер ищет её у себя по
    // тексту/категории и вызывает Act.
    private static bool ExecuteVerb(EntityUid target, Verb verb)
    {
        var systemType = Compat.FindType("Content.Client.Verbs.VerbSystem");
        if (systemType is null)
            return false;

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        if (!systems.TryGetEntitySystem(systemType, out var system) || system is null)
            return false;

        var execute = systemType.GetMethod("ExecuteVerb",
            BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(EntityUid), typeof(Verb) }, null);

        if (execute is null)
            return false;

        execute.Invoke(system, new object?[] { target, verb });
        return true;
    }

    private static Type? _virtualItem;

    // «Виртуальный предмет» — заглушка двуручного хвата (WieldableSystem кладёт
    // её во вторую руку). Выбросить её нельзя, поэтому в циклах освобождения
    // руки её надо пропускать, а не пытаться дропнуть.
    private static bool IsVirtual(IEntityManager ent, EntityUid uid)
    {
        _virtualItem ??= Compat.FindType("Content.Shared.Inventory.VirtualItem.VirtualItemComponent");
        return HasComp(ent, uid, _virtualItem);
    }

    // Взять ствол в две руки / убрать хват. Клик по слоту руки (UseInHand)
    // ненадёжен: тот же UseInHand ловит OnChamberUse у магазинного оружия и
    // может выиграть гонку. Верба WieldableSystem делает ровно одно действие.
    private static bool TryWieldVerb(EntityUid gun, EntityUid user, bool wield)
    {
        var verbs = LocalVerbs(gun, user, typeof(InteractionVerb));
        if (verbs is null)
        {
            Note("[SsMod] две руки: вербы недоступны");
            return false;
        }

        var text = Loc.GetString(wield ? "wieldable-verb-text-wield" : "wieldable-verb-text-unwield");

        var seen = new List<string>();
        Verb? fallback = null;

        foreach (var verb in verbs)
        {
            var act = verb.Act?.Method;
            seen.Add($"{verb.Text}@{(act?.DeclaringType?.Name ?? "-")}");

            // Текст совпал — это точно верба хвата.
            if (verb.Text == text)
                return ExecuteVerb(gun, verb);

            // Запасной путь на случай другой локали: лямбда объявлена внутри
            // SharedWieldableSystem.AddToggleWieldVerb в классе-замыкании
            // (<>c__DisplayClass), чей родитель — сама система. Имя ищем по
            // всей цепочке declaring-типов: один уровень пропускал вербу.
            var declaring = act?.DeclaringType;
            var fromWieldable = false;

            while (declaring is not null)
            {
                if (declaring.Name.Contains("Wieldable"))
                {
                    fromWieldable = true;
                    break;
                }

                declaring = declaring.DeclaringType;
            }

            var isWieldable = act is not null
                              && (act.Name.Contains("AddToggleWieldVerb") || fromWieldable);

            if (isWieldable)
                fallback ??= verb;
        }

        if (fallback is not null)
            return ExecuteVerb(gun, fallback);

        Note($"[SsMod] две руки: ждал «{text}», есть [{string.Join(" | ", seen)}]");
        return false;
    }

    // Верба «Опустошить» у револьвера: выбрасывает всё из барабана, включая
    // стреляные гильзы. Матчим по имени метода вербы (EmptyRevolver), запасной
    // путь — текст локали gun-revolver-empty.
    private static bool TryEmptyRevolverVerb(EntityUid gun, EntityUid user)
    {
        var verbs = LocalVerbs(gun, user, typeof(Content.Shared.Verbs.AlternativeVerb));
        if (verbs is null)
            return false;

        foreach (var verb in verbs)
        {
            if (verb.Act?.Method?.Name.Contains("EmptyRevolver") ?? false)
                return ExecuteVerb(gun, verb);
        }

        var text = Loc.GetString("gun-revolver-empty");

        foreach (var verb in verbs)
        {
            if (verb.Text == text)
                return ExecuteVerb(gun, verb);
        }

        Note("[SsMod] перезарядка: вербу «опустошить» не нашла");
        return false;
    }

    private static TimeSpan _lastDrop;
    private static bool _dropFailed;
    private static TimeSpan _dropClickAt;

    // Хранилища, куда уже пробовали убрать предмет в этой стадии Dropping.
    private static readonly HashSet<EntityUid> _stashTried = new();

    // Следующее хранилище для складирования: надетые сумки/рюкзак/разгрузка,
    // куда клик по сумке с предметом в руке кладёт его внутрь. Выброс на
    // землю — только после того, как все перепробованы.
    private static EntityUid? NextStash(IEntityManager ent, EntityUid self)
    {
        if (!ent.TryGetComponent(self, out ContainerManagerComponent? manager))
            return null;

        var storageType = Compat.FindType("Content.Shared.Storage.StorageComponent");

        foreach (var (_, container) in manager.Containers)
        {
            foreach (var worn in container.ContainedEntities)
            {
                if (_stashTried.Contains(worn))
                    continue;

                if (storageType is not null && HasComp(ent, worn, storageType))
                {
                    _stashTried.Add(worn);
                    return worn;
                }
            }
        }

        return null;
    }

    // Выбросить предмет из активной руки: штатная клавиша Drop. Другого
    // клиентского пути «выкинуть одну вещь» нет — TryDrop живёт на сервере.
    private static void TryDrop(TimeSpan now)
    {
        if (_dropFailed || now - _lastDrop < TimeSpan.FromSeconds(0.5))
            return;

        _lastDrop = now;

        try
        {
            var console = IoCManager.Resolve<IConsoleHost>();

            if (!console.AvailableCommands.ContainsKey("incmd"))
            {
                _dropFailed = true;
                return;
            }

            console.ExecuteCommand("incmd Drop Down");
            console.ExecuteCommand("incmd Drop Up");
        }
        catch (Exception e)
        {
            _dropFailed = true;
            SsLog.Write($"[SsMod] перезарядка: не смогла выбросить — {e.Message}");
        }
    }

    // Открыт ли интерфейс хранилища. Сервер отдаёт предмет из сумки только при
    // открытом UI (SharedStorageSystem.ValidateInput -> UI.IsUiOpen), поэтому
    // закрытый рюкзак сначала надо открыть, иначе попытки бесполезны.
    private static bool StorageUiOpen(IEntityManager ent, EntityUid storage)
    {
        if (!ent.TryGetComponent(storage, out Robust.Shared.GameObjects.UserInterfaceComponent? ui))
            return false;

        return GetMember(ui, "ClientOpenInterfaces") is ICollection open && open.Count > 0;
    }

    // Штатная клавиша «открыть рюкзак/пояс»: SharedStorageSystem вешает на
    // OpenBackpack/OpenBelt переключение UI слота.
    private static void ToggleStorage(string function, TimeSpan now)
    {
        try
        {
            var console = IoCManager.Resolve<IConsoleHost>();

            if (!console.AvailableCommands.ContainsKey("incmd"))
                return;

            console.ExecuteCommand($"incmd {function} Down");
            console.ExecuteCommand($"incmd {function} Up");
        }
        catch (Exception e)
        {
            SsLog.Write($"[SsMod] перезарядка: не смогла открыть сумку — {e.Message}");
        }
    }

    // ------------------------------------------------------------- диагностика

    // Пустой магазин/патрон: у провайдера ноль патронов и ноль нерасходованных.
    // Такой предмет вставлять бессмысленно — ItemSlots просто поменяет его на
    // такой же пустой из ствола.
    public static bool IsEmptyMag(IEntityManager ent, EntityUid uid)
    {
        if (Comp(ent, uid, _ballistic) is { } ballistic)
            return Get<int>(ballistic, "UnspawnedCount") <= 0 && ListCount(ballistic, "Entities") <= 0;

        return false;
    }

    // Что видно про ствол и его слоты: по этой строке понятно, какой это
    // провайдер патронов и почему перезарядка не идёт.
    public static string Dump(IEntityManager ent, EntityUid gun, Info info)
    {
        var parts = new List<string> { Proto(ent, gun) };

        if (Comp(ent, gun, _chamber) is { } chamber)
            parts.Add($"затвор={Get<bool?>(chamber, "BoltClosed")}");

        if (HasComp(ent, gun, _ballistic))
            parts.Add("помпа");

        if (HasComp(ent, gun, _battery))
            parts.Add("батарея");

        var revolver = Compat.FindType("Content.Shared.Weapons.Ranged.Components.RevolverAmmoProviderComponent");
        if (HasComp(ent, gun, revolver))
            parts.Add("револьвер");

        parts.Add($"патроны {info.AmmoCount}/{info.AmmoMax}");
        parts.Add($"слоты: {SlotsText(ent, gun)}");

        return string.Join(", ", parts);
    }

    // Одна строка про ствол и руки — чтобы по логу было видно, почему
    // перезарядка не пошла. Печатается только когда перезарядка реально
    // нужна (ствол пуст), и не чаще раза в 1.2 с.
    private static string _lastDiagKey = "";
    private static TimeSpan _lastDiagAt;

    private static void Diag(IEntityManager ent, EntityUid gun, HandsComponent hands, EntityUid player, string reason)
    {
        // Ключ без содержимого рук: иначе строка меняется от каждого подобранного
        // предмета и дедупликация не работает.
        var key = $"{reason}|{Proto(ent, gun)}";
        var now = IoCManager.Resolve<IGameTiming>().CurTime;

        if (key == _lastDiagKey && now - _lastDiagAt < TimeSpan.FromSeconds(3))
            return;

        _lastDiagKey = key;
        _lastDiagAt = now;

        var handsText = string.Join("; ", HandItems(ent, player, hands).Select(pair =>
            pair.Item is { } item
                ? $"{pair.Name}={Proto(ent, item)}[{string.Join("/", TagsOf(ent, item))}]"
                : $"{pair.Name}=—"));

        SsLog.Write($"[SsMod] перезарядка: {reason}; {Proto(ent, gun)}; слоты: {SlotsText(ent, gun)}; руки: {handsText}");
    }

    private static string _lastNote = "";
    private static TimeSpan _lastNoteAt;

    // Одинаковые строки про неудачи — не чаще раза в 20 секунд: причина не
    // меняется, а лог от повторов превращается в спам.
    private static void Note(string text)
    {
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        if (text == _lastNote && now - _lastNoteAt < TimeSpan.FromSeconds(3))
            return;

        _lastNote = text;
        _lastNoteAt = now;
        SsLog.Write(text);
    }

    private static string SlotsText(IEntityManager ent, EntityUid gun)
    {
        var slotsType = Compat.FindType("Content.Shared.Containers.ItemSlots.ItemSlotsComponent");
        if (Comp(ent, gun, slotsType) is not { } slotsComp)
            return "нет";

        if (GetMember(slotsComp, "Slots") is not IDictionary slots)
            return "нет";

        var parts = new List<string>();

        foreach (DictionaryEntry entry in slots)
        {
            var item = ToUid(ent, GetMember(entry.Value, "Item"));
            var tags = string.Join("/", TagsIn(entry.Value is null ? null : GetMember(entry.Value, "Whitelist")));

            parts.Add($"{entry.Key}={(item is { } uid ? Proto(ent, uid) : "пусто")}" + (tags.Length > 0 ? $"<{tags}>" : ""));
        }

        return parts.Count == 0 ? "нет" : string.Join(", ", parts);
    }

    private static IEnumerable<string> TagsIn(object? whitelist)
    {
        if (whitelist is null || GetMember(whitelist, "Tags") is not IEnumerable tags)
            yield break;

        foreach (var tag in tags)
        {
            if (tag?.ToString() is { Length: > 0 } id)
                yield return id;
        }
    }

    private static string Proto(IEntityManager ent, EntityUid uid)
    {
        if (ent.TryGetComponent(uid, out MetaDataComponent? meta) && meta.EntityPrototype is { } proto)
            return proto.ID;

        return uid.ToString();
    }

    // Своя проверка белого списка (рефлексией): теги и компоненты. Прямой
    // вызов EntityWhitelist.Check тянет за собой версии-зависимые перегрузки.
    private static bool WhitelistMatch(object? whitelist, IEntityManager ent, EntityUid uid)
    {
        if (whitelist is null)
            return true;

        var tags = GetMember(whitelist, "Tags") as IEnumerable;
        var comps = GetMember(whitelist, "Components") as IEnumerable;
        if (tags is null && comps is null)
            return true;

        var requireAll = GetMember(whitelist, "RequireAll") is true;
        var mine = TagsOf(ent, uid);
        var matched = 0;
        var rules = 0;

        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                var id = tag?.ToString();
                if (string.IsNullOrEmpty(id))
                    continue;

                rules++;
                if (mine.Contains(id!))
                    matched++;
            }
        }

        if (comps is not null)
        {
            foreach (var comp in comps)
            {
                var name = comp?.ToString();
                if (string.IsNullOrEmpty(name))
                    continue;

                rules++;
                if (HasComp(ent, uid, Compat.FindType(name!)))
                    matched++;
            }
        }

        if (rules == 0)
            return true;

        return requireAll ? matched == rules : matched > 0;
    }

    // Теги сущности строками.
    private static HashSet<string> TagsOf(IEntityManager ent, EntityUid uid)
    {
        var result = new HashSet<string>();

        var tagType = Compat.FindType("Content.Shared.Tag.TagComponent");
        if (Comp(ent, uid, tagType) is not { } tags)
            return result;

        if (GetMember(tags, "Tags") is not IEnumerable raw)
            return result;

        foreach (var tag in raw)
        {
            var id = tag?.ToString();
            if (!string.IsNullOrEmpty(id))
                result.Add(id!);
        }

        return result;
    }

    // Поле или свойство по имени — для членов, чей тип меняется между версиями.
    private static object? GetMember(object? obj, string name)
    {
        if (obj is null)
            return null;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var type = obj.GetType();

        return type.GetProperty(name, flags)?.GetValue(obj)
               ?? type.GetField(name, flags)?.GetValue(obj);
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
            SsLog.Write($"[SsMod] аимбот: боевой режим не включился ({e.Message})");
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
        // Tick обязан прочитать НАСТОЯЩИЙ курсор: если сначала поставить
        // InGunUpdate, патч MouseScreenPosition вернёт прошлую точку прицела,
        // и выбор цели начнёт зацикливаться сам на себя.
        AimBot.Tick();
        AimBot.InGunUpdate = true;
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

        if (AimBot.WantsFire)
        {
            __result = BoundKeyState.Down;
            return;
        }

        // Бот не хочет стрелять (пусто, стена, разброс) — но если игрок сам
        // держит кнопку, не отбираем у него выстрел: иначе ЛКМ «не нажимается».
        if (AimBot.IsFireHeld())
            return;

        __result = BoundKeyState.Up;
    }
}
