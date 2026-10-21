using System.Numerics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Первый шаг наводки — две точки на экране:
//
//   красная — точка предикта: где цель будет через время полёта снаряда;
//   синяя   — точка наводки: она тянется к красной.
//
// Здесь пока только геометрия: ничего не нажимаем, не стреляем, боевой режим
// не трогаем.
//
// Точки не «летают по залупе»:
//   * цель — только тот, кто под курсором (это наводка, а не автовыбор врага);
//   * скорость цели сглаживается и ограничена, сетевые рывки не считаются;
//   * синяя при смене цели сразу встаёт на красную, а не летит через карту;
//   * скорость синей ограничена, а от красной она не может уйти дальше поводка.
internal static class PredictPoint
{
    // ПИД: ошибка в тайлах -> скорость в тайлах/с. При 60 кадрах в секунду
    // точка доходит до цели примерно за 0.2 с и не раскачивается.
    // Значения зашиты: слайдеров и ключей конфига у регулятора нет.
    public const float P = 16f;
    public const float I = 2f;
    public const float D = 0.12f;

    // Производная сглаживается. Без этого сетевой рывок позиции даёт всплеск
    // (ошибка - прошлая)/dt, и точка улетает в случайную сторону.
    private const float DerivativeSmooth = 0.08f;

    // Интеграл ограничен, иначе при долгом расхождении он разгоняет точку.
    public static float IntegralLimit = 4f;

    // Потолок скорости синей точки (тайлы/с). Раньше было 90 — точка успевала
    // улететь за полкадра на другой конец экрана.
    public const float MaxSpeed = 30f;

    // Поводок: синяя не может уйти от красной дальше этого (тайлы).
    public static float Leash = 8f;

    // Дальше этого упреждение не считаем: цель успеет уйти.
    private const float MaxFlight = 2.5f;

    // Максимальное упреждение в тайлах. Ограничивает только совсем дикие
    // случаи: у самой медленной пули (10 тайлов/с) на 12 тайлах упреждение
    // выходит около 5-6 тайлов, и прежний потолок в 4 резал его — бот стрелял
    // позади цели.
    private const float MaxLead = 10f;

    // Радиус захвата цели под курсором (тайлы).
    private const float Snap = 2.5f;

    // Запас к радиусу удержания: игрок держит курсор не на теле цели, а на
    // красной точке с упреждением, плюс мелкая дрожж мыши.
    private const float HoldSlack = 0.75f;

    // Дальше этого цель не удерживаем: стрелять по ней всё равно нечем.
    private const float RetainRange = 25f;

    // Сколько держим цель, если курсор всё-таки ушёл с неё: один-два кадра
    // дрожжи не должны срывать наводку.
    private const float HoldTime = 0.2f;

    // Длина упреждения в прошлом кадре — на неё расширяется радиус удержания.
    private static float _leadLength;

    // Когда курсор последний раз стоял на цели.
    private static TimeSpan _onTargetAt;

    // Сглаживание скорости цели и её потолок: у игрока ~6 тайлов/с, всё выше —
    // сетевой рывок или телепорт.
    private const float TrackSmooth = 0.2f;
    private const float MaxTargetSpeed = 12f;

    // Есть живая цель под курсором (по ней можно стрелять).
    public static bool HasTarget { get; private set; }

    // Точки есть всегда: без цели они стоят на своём персонаже.
    // Рисование смотрит сюда, стрельба — на HasTarget.
    public static bool HasPoint { get; private set; }

    public static EntityUid? Target { get; private set; }

    // Мировые координаты точек.
    public static Vector2 Red { get; private set; }
    public static Vector2 Blue { get; private set; }
    public static Vector2 SelfWorld { get; private set; }

    // Время полёта, по которому посчитано упреждение (для отладки).
    public static float Flight { get; private set; }

    private static Vector2 _integral;
    private static Vector2 _prevError;
    private static Vector2 _derivative;
    private static TimeSpan _last;

    // Трекер цели: скорость считаем конечной разностью её же позиций.
    private static EntityUid? _logged;
    private static EntityUid? _tracked;
    private static Vector2 _trackedPos;
    private static TimeSpan _trackedAt;
    private static Vector2 _trackedVel;

    // Зовётся из GunBotOverlay.Draw раз в кадр и из AimBot.Step в кадре стрельбы.
    // hold — кнопка огня зажата: пока она держится, прежнюю цель не отпускаем,
    // даже если курсор с неё ушёл (иначе лок срывался на каждом движении цели).
    public static void Tick(IEntityManager ent, EntityUid self, Vector2 cursorWorld, bool hold = false)
    {
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        var dt = _last == TimeSpan.Zero ? 1f / 60f : (float) (now - _last).TotalSeconds;
        _last = now;

        // Кадр мог быть долгим (загрузка, сворачивание) — не разгоняем регулятор.
        if (dt <= 0f || dt > 0.25f)
            dt = 1f / 60f;

        var transform = ent.System<SharedTransformSystem>();
        SelfWorld = transform.GetWorldPosition(self);
        var selfMap = transform.GetMapId(self);

        // Цель — только под курсором. Ближайшего помеченного врага здесь не
        // берём: из-за него точки прыгали на другого персонажа в стороне.
        var target = Targeting.UnderPoint(ent, self, cursorWorld, Snap);

        // Курсор мог уйти с тела цели: игрок держит его на красной точке с
        // упреждением, а сама цель дёргается. Без удержания наводка срывалась —
        // бот отпускал цель, и выстрел уходил по обычному курсору.
        if (target is not null)
            _onTargetAt = now;

        if (target is null && TryKeep(ent, transform, selfMap, cursorWorld, now, out var kept))
            target = kept;

        // Лок: пока кнопка огня зажата, держим прежнюю цель, даже если курсор
        // от неё далеко. Цель под курсором всё равно важнее лока.
        if (target is null && hold && Target is { } locked
            && StillValid(ent, transform, selfMap, locked))
        {
            target = locked;
        }

        if (target is not { } uid
            || !ent.HasComponent<TransformComponent>(uid)
            || transform.GetMapId(uid) != selfMap)
        {
            // Цели нет — точки стоят на своём персонаже.
            NoTarget();
            return;
        }

        var pos = transform.GetWorldPosition(uid);
        UpdateVelocity(uid, pos, now);

        var speed = Speed(ent, self);
        Red = Predict(pos, speed, out var flight);
        Flight = flight;
        _leadLength = (Red - pos).Length();

        // Новая цель — ставим синюю сразу на красную, а не летим через карту.
        if (!HasTarget || Target != uid)
        {
            Blue = Red;
            _integral = Vector2.Zero;
            _prevError = Vector2.Zero;
            _derivative = Vector2.Zero;
        }
        else
        {
            Step(Red, dt);
        }

        HasTarget = true;
        HasPoint = true;
        Target = uid;

        if (_logged != uid)
        {
            _logged = uid;
            Console.WriteLine($"[SsMod] точки: цель {uid}, полёт {flight:0.00} с");
        }
    }

    // Один шаг ПИД: ошибка -> скорость синей точки.
    private static void Step(Vector2 red, float dt)
    {
        var error = red - Blue;

        _integral = Vector2.Clamp(_integral + error * dt,
            new Vector2(-IntegralLimit), new Vector2(IntegralLimit));

        // Производную сглаживаем: она считается по разности кадров и без
        // фильтра усиливает любой сетевой дрожж.
        var raw = (error - _prevError) / dt;
        _prevError = error;
        _derivative = Vector2.Lerp(_derivative, raw, 1f - MathF.Exp(-dt / DerivativeSmooth));

        var velocity = error * P + _integral * I + _derivative * D;

        var speed = velocity.Length();
        if (speed > MaxSpeed)
            velocity = velocity / speed * MaxSpeed;

        Blue += velocity * dt;

        // Поводок: даже при жёстких настройках точка не улетает от цели.
        var off = Blue - red;
        var dist = off.Length();
        if (dist > Leash)
            Blue = red + off / dist * Leash;
    }

    // Упреждение: снаряд летит со скоростью снаряда и наследует скорость
    // стрелка, поэтому целимся по относительной скорости. Для самой медленной
    // пули (10 тайлов/с) итераций нужно больше: время полёта заметно меняется
    // от самой точки упреждения.
    private static Vector2 Predict(Vector2 pos, float speed, out float flight)
    {
        var pred = pos;
        flight = 0f;

        // Хитскан летит мгновенно — упреждение ему не нужно.
        if (speed <= 0.01f)
            return pred;

        // Только скорость цели: своё движение на упреждение не влияет.
        var rel = _trackedVel;

        for (var i = 0; i < 4; i++)
        {
            flight = MathF.Min((pred - SelfWorld).Length() / speed, MaxFlight);
            var lead = rel * flight;
            var leadLength = lead.Length();
            if (leadLength > MaxLead)
                lead = lead / leadLength * MaxLead;

            pred = pos + lead;
        }

        return pred;
    }

    private static float Speed(IEntityManager ent, EntityUid self)
    {
        // Нет ствола — нет данных о скорости пули, упреждение не считаем.
        if (!AttackReach.TryGetGun(ent, self, out var gunUid)
            || !ent.TryGetComponent(gunUid, out GunComponent? gun))
        {
            return 0f;
        }

        // Лазеры и прочий хитскан не летят: любое упреждение — промах.
        // Скорость у таких пушек в компоненте всё равно лежит (53 по умолчанию),
        // поэтому смотрим именно провайдер боеприпасов.
        var hitscan = Compat.FindType(
            "Content.Shared.Weapons.Ranged.Components.HitscanBatteryAmmoProviderComponent");

        if (hitscan is not null && ent.HasComponent(gunUid, hitscan))
            return 0f;

        return gun.ProjectileSpeedModified > 0.1f
            ? gun.ProjectileSpeedModified
            : gun.ProjectileSpeed;
    }

    private static void UpdateVelocity(EntityUid uid, Vector2 pos, TimeSpan now)
    {
        if (_tracked != uid)
        {
            _tracked = uid;
            _trackedPos = pos;
            _trackedAt = now;
            _trackedVel = Vector2.Zero;
            return;
        }

        var span = (float) (now - _trackedAt).TotalSeconds;
        if (span < 0.02f)
            return;

        var raw = (pos - _trackedPos) / span;
        _trackedPos = pos;
        _trackedAt = now;

        // Телепорт или лаг: скорость не обновляем, иначе упреждение улетит.
        if (raw.Length() > 60f)
        {
            _trackedVel = Vector2.Zero;
            return;
        }

        _trackedVel = Vector2.Lerp(_trackedVel, raw, 1f - MathF.Exp(-span / TrackSmooth));

        var speed = _trackedVel.Length();
        if (speed > MaxTargetSpeed)
            _trackedVel = _trackedVel / speed * MaxTargetSpeed;
    }

    // Прежняя цель ещё существует: жива, на нашей карте, недалеко от нас.
    private static bool StillValid(IEntityManager ent, SharedTransformSystem transform, MapId map, EntityUid uid)
    {
        if (!Targeting.Usable(ent, uid))
            return false;

        if (!ent.TryGetComponent(uid, out MobStateComponent? mob) || mob.CurrentState != MobState.Alive)
            return false;

        if (transform.GetMapId(uid) != map)
            return false;

        return (transform.GetWorldPosition(uid) - SelfWorld).Length() <= RetainRange;
    }

    // Прежняя цель годится и курсор около неё (с поправкой на упреждение):
    // дрожжевые одиночные кадры наводку не срывают.
    private static bool TryKeep(IEntityManager ent, SharedTransformSystem transform, MapId map,
        Vector2 cursor, TimeSpan now, out EntityUid uid)
    {
        uid = default;

        if (!HasTarget || Target is not { } prev)
            return false;

        if (!StillValid(ent, transform, map, prev))
            return false;

        var pos = transform.GetWorldPosition(prev);

        if ((pos - cursor).Length() > Snap + _leadLength + HoldSlack
            && now - _onTargetAt > TimeSpan.FromSeconds(HoldTime))
        {
            return false;
        }

        uid = prev;
        return true;
    }

    // Живой цели нет: обе точки — на своём персонаже.
    private static void NoTarget()
    {
        HasTarget = false;
        HasPoint = true;
        Target = null;
        Red = SelfWorld;
        Blue = SelfWorld;
        Flight = 0f;
        _leadLength = 0f;
        _logged = null;
        _tracked = null;
        _trackedVel = Vector2.Zero;
        _integral = Vector2.Zero;
        _prevError = Vector2.Zero;
        _derivative = Vector2.Zero;
    }

    private static void Lost()
    {
        HasTarget = false;
        Target = null;
        _logged = null;
        _tracked = null;
        _trackedVel = Vector2.Zero;
        _integral = Vector2.Zero;
        _prevError = Vector2.Zero;
        _derivative = Vector2.Zero;
    }
}
