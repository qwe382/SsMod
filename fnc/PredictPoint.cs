using System.Numerics;
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
//   синяя   — точка ПИД-регулятора: куда бот «наводится» сейчас.
//
// Синяя тянется к красной через ПИД (ошибка -> скорость), поэтому не прыгает
// при смене цели и не дёргается от сетевых рывков позиции.
//
// Здесь пока только геометрия: ничего не нажимаем, не стреляем, боевой режим
// не трогаем.
internal static class PredictPoint
{
    // ПИД: ошибка в тайлах, выход — тайлы в секунду.
    private const float P = 14f;
    private const float I = 0.6f;
    private const float D = 4.5f;

    // Интеграл ограничиваем, иначе при долгом расхождении он «разгоняет» точку.
    private const float IntegralLimit = 8f;

    // Потолок скорости синей точки: при смене цели она не телепортируется.
    private const float MaxSpeed = 90f;

    // Скорость снаряда, если в руке нет ствола (тайлы в секунду).
    private const float DefaultSpeed = 20f;

    // Дальше этого упреждение не считаем: цель успеет уйти.
    private const float MaxFlight = 2.5f;

    // Радиус поиска цели с меткой Make Enemy.
    private const float SearchRange = 30f;

    public static bool HasTarget { get; private set; }
    public static EntityUid? Target { get; private set; }

    // Мировые координаты точек.
    public static Vector2 Red { get; private set; }
    public static Vector2 Blue { get; private set; }
    public static Vector2 SelfWorld { get; private set; }

    // Время полёта, по которому посчитано упреждение (для отладки).
    public static float Flight { get; private set; }

    private static Vector2 _integral;
    private static Vector2 _prevError;
    private static TimeSpan _last;

    // Трекер цели: скорость считаем конечной разностью её же позиций.
    private static EntityUid? _tracked;
    private static Vector2 _trackedPos;
    private static TimeSpan _trackedAt;
    private static Vector2 _trackedVel;
    private static EntityUid? _logged;

    // Зовётся из GunBotOverlay.Draw раз в кадр.
    public static void Tick(IEntityManager ent, EntityUid self, Vector2 cursorWorld)
    {
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        var dt = _last == TimeSpan.Zero ? 1f / 60f : (float) (now - _last).TotalSeconds;
        _last = now;

        // Кадр мог быть долгим (загрузка, сворачивание) — не разгоняем ПИД.
        if (dt <= 0f || dt > 0.25f)
            dt = 1f / 60f;

        var transform = ent.System<SharedTransformSystem>();
        SelfWorld = transform.GetWorldPosition(self);
        var selfMap = transform.GetMapId(self);

        // Те же правила, что у роутера: сначала ближайший помеченный Make Enemy,
        // иначе тот, кто под курсором.
        var target = Targeting.NearestEnemy(ent, self, SearchRange)
                     ?? Targeting.UnderPoint(ent, self, cursorWorld, MelBotOverlay.SnapTiles);

        if (target is not { } uid
            || !ent.HasComponent<TransformComponent>(uid)
            || transform.GetMapId(uid) != selfMap)
        {
            Lost();
            return;
        }

        var pos = transform.GetWorldPosition(uid);
        UpdateVelocity(uid, pos, now);

        var speed = Speed(ent, self);
        Red = Predict(pos, speed, out var flight);
        Flight = flight;

        if (!HasTarget)
        {
            // Новая цель — ставим синюю сразу на красную, а не летим через карту.
            Blue = Red;
            _integral = Vector2.Zero;
            _prevError = Vector2.Zero;
        }
        else
        {
            Step(Red - Blue, dt);
        }

        HasTarget = true;
        Target = uid;

        if (_logged != uid)
        {
            _logged = uid;
            Console.WriteLine($"[SsMod] предикт: цель {uid}, полёт {flight:0.00} с, скорость {speed:0} тайл/с");
        }
    }

    // Один шаг ПИД: ошибка -> ускорение/скорость синей точки.
    private static void Step(Vector2 error, float dt)
    {
        _integral = Vector2.Clamp(_integral + error * dt,
            new Vector2(-IntegralLimit), new Vector2(IntegralLimit));

        var derivative = (error - _prevError) / dt;
        _prevError = error;

        var velocity = error * P + _integral * I + derivative * D;

        var speed = velocity.Length();
        if (speed > MaxSpeed)
            velocity = velocity / speed * MaxSpeed;

        Blue += velocity * dt;
    }

    // Упреждение: снаряд летит со скоростью снаряда и наследует скорость
    // стрелка. Скорость стрелка для первого шага не учитываем — она в разы
    // меньше скорости пули.
    private static Vector2 Predict(Vector2 pos, float speed, out float flight)
    {
        var pred = pos;
        flight = 0f;

        for (var i = 0; i < 3; i++)
        {
            flight = MathF.Min((pred - SelfWorld).Length() / speed, MaxFlight);
            pred = pos + _trackedVel * flight;
        }

        return pred;
    }

    private static float Speed(IEntityManager ent, EntityUid self)
    {
        if (AttackReach.TryGetGun(ent, self, out var gunUid)
            && ent.TryGetComponent(gunUid, out GunComponent? gun)
            && gun.ProjectileSpeedModified > 1f)
        {
            return gun.ProjectileSpeedModified;
        }

        return DefaultSpeed;
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

        _trackedVel = (pos - _trackedPos) / span;
        _trackedPos = pos;
        _trackedAt = now;
    }

    private static void Lost()
    {
        HasTarget = false;
        Target = null;
        _tracked = null;
        _logged = null;
        _trackedVel = Vector2.Zero;
        _integral = Vector2.Zero;
        _prevError = Vector2.Zero;
    }
}
