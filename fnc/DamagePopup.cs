using System.Globalization;
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
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Твик (включён всегда, галки в окне нет): когда кому-то наносится урон, над ним
// на секунду всплывает «текущее/максимальное ХП».
//
// Урон в клиент приезжает только как сетевое состояние DamageableComponent, поэтому
// «что-то получило урон» определяем опросом: раз в кадр смотрим всех, кто рядом с
// персонажем, и сравниваем суммарный урон с прошлым кадром. Прямых ссылок на
// DamageableComponent тут нет — тип называется по-разному в версиях контента,
// читаем через Compat.GetTotalDamage.
public static class DamagePopup
{
    internal static bool Enabled { get; private set; }

    // Всплывашки живут отдельно от оверлея, чтобы гаснуть по своему таймеру.
    internal static readonly List<Popup> Popups = new();

    // Твик всегда включён — галку в «Твиках» убрали, поэтому включаем один раз
    // на старте игры (см. StartupPatch).
    public static void Toggle(bool on)
    {
        Enabled = on;

        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (!overlays.HasOverlay<DamagePopupOverlay>())
                overlays.AddOverlay(new DamagePopupOverlay());
        }
        else
        {
            overlays.RemoveOverlay<DamagePopupOverlay>();
            Popups.Clear();
        }

        Console.WriteLine($"[SsMod] damage popup = {on}");
    }

    internal readonly struct Popup
    {
        public readonly EntityUid Target;
        public readonly Vector2 World;
        public readonly string Text;
        public readonly Color Color;
        public readonly TimeSpan Start;
        public readonly TimeSpan Until;

        public Popup(EntityUid target, Vector2 world, string text, Color color, TimeSpan start, TimeSpan until)
        {
            Target = target;
            World = world;
            Text = text;
            Color = color;
            Start = start;
            Until = until;
        }
    }
}

public sealed class DamagePopupOverlay : Overlay
{
    // Дольше секунды держим только чтобы всплывашка успела погаснуть.
    private const float Lifetime = 1f;

    // Дальше этого от персонажа не смотрим: там и цели-то не видно.
    private const float ScanRange = 15f;

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // Систему берём лениво: в EntryPoint.PostInit IEntityManager.System<T>()
    // падает с NRE — системы движка ещё не инициализированы, а оверлеи
    // создаются именно там (стартовый конфиг).
    private SharedTransformSystem? _transformCache;
    private SharedTransformSystem Transform => _transformCache ??= _ent.System<SharedTransformSystem>();
    private readonly Font _font;

    // Суммарный урон каждой цели в прошлом кадре.
    private readonly Dictionary<EntityUid, float> _lastDamage = new();

    // Максимум ХП, вытащенный из прототипа: считаем один раз на прототип.
    private readonly Dictionary<string, float> _protoMax = new();

    private TimeSpan _lastPrune;

    public DamagePopupOverlay()
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
            Console.Error.WriteLine($"[SsMod] damage popup draw failed: {e.Message}");
        }
    }

    private void DrawPopups(in OverlayDrawArgs args)
    {
        var now = _timing.RealTime;

        var popups = DamagePopup.Popups;
        for (var i = popups.Count - 1; i >= 0; i--)
        {
            if (popups[i].Until <= now)
                popups.RemoveAt(i);
        }

        if (_player.LocalEntity is { Valid: true } self)
            Scan(self, now);

        if (popups.Count == 0 || args.Viewport.Eye is null)
            return;

        var handle = args.ScreenHandle;

        foreach (var popup in popups)
        {
            var left = (popup.Until - popup.Start).TotalSeconds;
            var done = MathF.Max(Lifetime - (float) left, 0f);
            var fade = Math.Clamp((float) left / 0.35f, 0f, 1f);

            var screen = CombatVisuals.Screen(args, popup.World);
            // Всплываем вверх и чуть подрастаем по мере затухания.
            screen.Y -= 26f + done * 10f;
            screen.X -= popup.Text.Length * 5.5f * 0.5f;

            var color = popup.Color.WithAlpha(popup.Color.A * fade);

            // Тень, чтобы читалось на любом фоне.
            Compat.DrawText(handle, _font, screen + new Vector2(1f, 1f), popup.Text, 1f, Color.Black.WithAlpha(0.7f * fade));
            Compat.DrawText(handle, _font, screen, popup.Text, 1f, color);
        }
    }

    // Ищем тех, кому только что добавили урона.
    private void Scan(EntityUid self, TimeSpan now)
    {
        var mapId = Transform.GetMapCoordinates(self).MapId;
        var selfPos = Transform.GetWorldPosition(self);

        var query = _ent.AllEntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (uid == self)
                continue;

            var coords = Transform.GetMapCoordinates(uid);
            if (coords.MapId != mapId)
                continue;

            var world = coords.Position;
            if ((world - selfPos).LengthSquared() > ScanRange * ScanRange)
                continue;

            // Нет DamageableComponent — цель не считается.
            if (Compat.GetTotalDamage(_ent, uid) is not { } total)
                continue;

            if (_lastDamage.TryGetValue(uid, out var previous) && total > previous)
                Record(uid, world, total, total - previous, now);

            _lastDamage[uid] = total;
        }

        Prune(now);
    }

    private void Record(EntityUid uid, Vector2 world, float total, float delta, TimeSpan now)
    {
        string text;
        Color color;

        if (MaxHealth(uid) is { } max && max > 0f)
        {
            var value = Math.Clamp(max - total, 0f, max);
            var ratio = value / max;

            text = $"{(int) MathF.Ceiling(value)}/{(int) max}";
            color = ratio switch
            {
                > 0.5f => Color.White,
                > 0.2f => new Color(1f, 0.85f, 0.25f),
                _ => new Color(1f, 0.35f, 0.35f),
            };
        }
        else
        {
            // Ни порогов моба, ни порога разрушения в прототипе — показываем сам удар.
            text = $"-{(int) MathF.Ceiling(delta)}";
            color = new Color(1f, 0.6f, 0.6f);
        }

        var until = now + TimeSpan.FromSeconds(Lifetime);

        // Несколько ударов подряд — не плодим надписи, обновляем ту же.
        var popups = DamagePopup.Popups;
        for (var i = 0; i < popups.Count; i++)
        {
            if (popups[i].Target != uid)
                continue;

            popups[i] = new DamagePopup.Popup(uid, world, text, color, now, until);
            return;
        }

        popups.Add(new DamagePopup.Popup(uid, world, text, color, now, until));
    }

    // Максимум ХП, как в игровой полоске здоровья: порог критического состояния,
    // иначе смерти, иначе первый (минимальный) порог. Если порогов моба нет — объект,
    // и тогда максимум берём из прототипа (порог разрушения).
    private float MaxHealth(EntityUid uid)
    {
        if (!_ent.TryGetComponent(uid, out MobThresholdsComponent? thresholds) || thresholds.Thresholds.Count == 0)
            return PrototypeMax(uid);

        var crit = 0f;
        var dead = 0f;
        var first = 0f;
        var isFirst = true;

        foreach (var (key, state) in thresholds.Thresholds)
        {
            var value = (float) key;

            if (isFirst)
            {
                first = value;
                isFirst = false;
            }

            if (state == MobState.Critical && crit <= 0f)
                crit = value;
            else if (state == MobState.Dead && dead <= 0f)
                dead = value;
        }

        if (crit > 0f)
            return crit;

        if (dead > 0f)
            return dead;

        return first > 0f ? first : PrototypeMax(uid);
    }

    // У объектов максимум ХП задан серверным DestructibleComponent, на клиент он не приезжает.
    // Зато клиент держит сырые YAML-узлы прототипов (IPrototypeManager.TryGetMapping), а там
    // у каждого объекта прописаны пороги разрушения — берём наибольший порог по урону.
    private float PrototypeMax(EntityUid uid)
    {
        if (!_ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.EntityPrototype is not { } proto)
            return 0f;

        if (_protoMax.TryGetValue(proto.ID, out var cached))
            return cached;

        var max = 0f;

        try
        {
            if (_proto.TryGetMapping(typeof(EntityPrototype), proto.ID, out var mapping)
                && mapping.TryGet("components", out var comps)
                && comps is SequenceDataNode list)
            {
                foreach (var entry in list.Sequence)
                {
                    if (entry is not MappingDataNode comp)
                        continue;

                    if (!comp.TryGet("type", out var typeNode) || typeNode is not ValueDataNode type
                        || type.Value != "Destructible")
                        continue;

                    max = MathF.Max(max, DestructionThreshold(comp));
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] damage popup: прототип {proto.ID}: {e.Message}");
        }

        _protoMax[proto.ID] = max;
        return max;
    }

    // Пороги разрушения: у каждого порога свой триггер, а триггеры умеют вкладываться
    // (And/Or) — поэтому ищем все значения «damage» рекурсивно и берём максимум.
    private static float DestructionThreshold(MappingDataNode destructible)
    {
        if (!destructible.TryGet("thresholds", out var thresholds) || thresholds is not SequenceDataNode list)
            return 0f;

        var max = 0f;

        foreach (var entry in list.Sequence)
        {
            if (entry is MappingDataNode threshold
                && threshold.TryGet("trigger", out var trigger)
                && trigger is MappingDataNode triggerMap)
            {
                max = MathF.Max(max, TriggerDamage(triggerMap));
            }
        }

        return max;
    }

    private static float TriggerDamage(MappingDataNode trigger)
    {
        var max = 0f;

        if (trigger.TryGet("damage", out var damage) && damage is ValueDataNode value
            && float.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            max = parsed;
        }

        if (trigger.TryGet("triggers", out var nested) && nested is SequenceDataNode list)
        {
            foreach (var entry in list.Sequence)
            {
                if (entry is MappingDataNode inner)
                    max = MathF.Max(max, TriggerDamage(inner));
            }
        }

        return max;
    }

    // Чистим кеш урона от удалённых сущностей, чтобы он не рос бесконечно.
    private void Prune(TimeSpan now)
    {
        if (now - _lastPrune < TimeSpan.FromSeconds(5))
            return;

        _lastPrune = now;

        List<EntityUid>? dead = null;
        foreach (var uid in _lastDamage.Keys)
        {
            if (_ent.EntityExists(uid))
                continue;

            (dead ??= new List<EntityUid>()).Add(uid);
        }

        if (dead == null)
            return;

        foreach (var uid in dead)
            _lastDamage.Remove(uid);
    }
}
