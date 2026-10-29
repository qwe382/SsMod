using System.Collections;
using System.Numerics;
using System.Reflection;
using Content.Shared.Hands.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Твик: над заведённой гранатой висит таймер — сколько секунд осталось до взрыва.
// Если граната в руке, таймер рисуется у прицела (под курсором), чтобы не смотреть
// на миниатюрный спрайт в руке.
//
// Сервер не отдаёт клиенту ActiveTimerTriggerComponent (он не сетевой), поэтому
// точное «TimeRemaining» недоступно. Зато клиенту приходят:
//   * OnUseTimerTriggerComponent.Delay — настроенная длительность таймера;
//   * AppearanceComponent с TriggerVisuals.VisualState = Primed.
// Момент Primed и считаем стартом: остаток = Delay - (сейчас - старт).
// Если граната стала видна уже заведённой (прилетела из-за PVS), считаем, что
// таймер только что стартовал, — точнее клиент узнать не может.
public static class GrenadeTimer
{
    internal static bool Enabled { get; private set; }

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Таймер гранаты",
            Pressed = Enabled,
            ToolTip = "Сколько секунд осталось до взрыва заведённой гранаты (в руке — у прицела)",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;

        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
        {
            if (!overlays.HasOverlay<GrenadeTimerOverlay>())
                overlays.AddOverlay(new GrenadeTimerOverlay());
        }
        else
        {
            overlays.RemoveOverlay<GrenadeTimerOverlay>();
        }

        Console.WriteLine($"[SsMod] таймер гранаты = {on}");
    }
}

public sealed class GrenadeTimerOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    // Системы берём лениво: в EntryPoint.PostInit IEntityManager.System<T>()
    // падает с NRE (см. комментарии в Esp/MelBot).
    private SharedAppearanceSystem? _appearanceCache;
    private SpriteSystem? _spriteCache;
    private SharedTransformSystem? _transformCache;

    private SharedAppearanceSystem Appearance => _appearanceCache ??= _ent.System<SharedAppearanceSystem>();
    private SpriteSystem Sprite => _spriteCache ??= _ent.System<SpriteSystem>();
    private SharedTransformSystem Transform => _transformCache ??= _ent.System<SharedTransformSystem>();

    private readonly Font _font;

    // Найденные типы компонентов: ищем по имени, чтобы не сломаться на чужой
    // версии контента, где тип мог переехать.
    private bool _resolved;
    private Type? _timerType;
    private MemberInfo? _delayMember;
    private Enum? _appearanceKey;

    // Граната -> момент, когда клиент впервые увидел её заведённой.
    private readonly Dictionary<EntityUid, TimeSpan> _primed = new();

    private readonly List<EntityUid> _primedNow = new();
    private readonly HashSet<EntityUid> _alive = new();
    private FieldInfo? _handsField;
    private TimeSpan _lastScan;
    private bool _scanned;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);

    public GrenadeTimerOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(
            _cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            DrawTimers(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] таймер гранаты: отрисовка упала: {e.Message}");
        }
    }

    private void DrawTimers(in OverlayDrawArgs args)
    {
        if (!GrenadeTimer.Enabled || args.Viewport.Eye is not { } eye)
            return;

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        Resolve();

        var now = _timing.RealTime;
        Poll(now);

        if (_primed.Count == 0)
            return;

        var handle = args.ScreenHandle;
        var mapId = eye.Position.MapId;
        var inHand = HeldBySelf(self);

        foreach (var (uid, start) in _primed)
        {
            if (!_ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.Deleted)
                continue;

            if (!_ent.TryGetComponent(uid, _timerType!, out var comp) || comp == null)
                continue;

            var delay = DelayOf(comp);
            if (delay <= 0f)
                continue;

            var left = delay - (float) (now - start).TotalSeconds;
            if (left <= 0f)
                continue;

            var text = $"{left:0.0}с";
            var color = ColorFor(left);

            // В руке — у прицела, иначе — над спрайтом гранаты.
            if (inHand(uid))
            {
                var cursor = CombatVisuals.CursorScreen(args, out _);
                var width = text.Length * 5.5f;
                var pos = new Vector2(cursor.X - width / 2f, cursor.Y - 30f);
                Compat.DrawText(handle, _font, pos + new Vector2(1f, 1f), text, 1f, Color.Black.WithAlpha(0.75f));
                Compat.DrawText(handle, _font, pos, text, 1f, color);
                continue;
            }

            var mapPos = Transform.GetMapCoordinates(uid);
            if (mapPos.MapId != mapId)
                continue;

            var world = mapPos.Position;
            var top = 0.35f;
            if (_ent.TryGetComponent(uid, out SpriteComponent? sprite) && sprite.Visible)
            {
                var bounds = Sprite.GetLocalBounds((uid, sprite));
                if (bounds.Height > 0f)
                    top = bounds.Height / 2f + 0.1f;
            }

            var screen = CombatVisuals.Screen(args, world + new Vector2(0f, top));
            var textWidth = text.Length * 5.5f;
            var textPos = new Vector2(screen.X - textWidth / 2f, screen.Y - _font.GetLineHeight(1f));
            Compat.DrawText(handle, _font, textPos + new Vector2(1f, 1f), text, 1f, Color.Black.WithAlpha(0.75f));
            Compat.DrawText(handle, _font, textPos, text, 1f, color);
        }
    }

    // Раз в 100 мс обновляем список заведённых гранат. Между сканами рисуем
    // по закэшированному словарю, а время досчитываем сами.
    private void Poll(TimeSpan now)
    {
        if (_scanned && now - _lastScan < ScanInterval)
            return;

        _scanned = true;
        _lastScan = now;
        _primedNow.Clear();

        foreach (var uid in _ent.GetEntities())
        {
            if (_ent.Deleted(uid))
                continue;

            if (!_ent.TryGetComponent(uid, _timerType!, out var comp) || comp == null)
                continue;

            if (DelayOf(comp) <= 0f || !IsPrimed(uid))
            {
                _primed.Remove(uid);
                continue;
            }

            if (!_primed.ContainsKey(uid))
                _primed[uid] = now;

            _primedNow.Add(uid);
        }

        // Всё, что перестало быть заведённым или ушло из PVS, забываем.
        _alive.Clear();
        foreach (var uid in _primedNow)
            _alive.Add(uid);

        if (_primed.Count == 0)
            return;

        var stale = new List<EntityUid>();
        foreach (var uid in _primed.Keys)
        {
            if (!_alive.Contains(uid))
                stale.Add(uid);
        }

        foreach (var uid in stale)
            _primed.Remove(uid);
    }

    // «Граната в руках персонажа»: перебираем контейнеры всех рук, а не только
    // активной. EnumerateHeld различается по сигнатуре между версиями движка,
    // а тип Hand в 276 — struct, в 290 — class (прямая ссылка даёт TypeLoadException),
    // поэтому словарь рук читаем через необобщённый IDictionary: имя контейнера = имя руки.
    private Func<EntityUid, bool> HeldBySelf(EntityUid self)
    {
        var held = new HashSet<EntityUid>();
        try
        {
            _handsField ??= typeof(HandsComponent).GetField("Hands", BindingFlags.Public | BindingFlags.Instance);

            if (_handsField?.GetValue(HandsOf(self)) is IDictionary hands
                && _ent.TryGetComponent(self, out ContainerManagerComponent? containers))
            {
                foreach (DictionaryEntry entry in hands)
                {
                    if (entry.Key is not string handName)
                        continue;

                    if (!containers.Containers.TryGetValue(handName, out var container))
                        continue;

                    foreach (var child in container.ContainedEntities)
                        held.Add(child);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] таймер гранаты: руки не прочитались: {e.Message}");
        }

        return held.Contains;
    }

    private HandsComponent? HandsOf(EntityUid self)
    {
        return _ent.TryGetComponent(self, out HandsComponent? hands) ? hands : null;
    }

    // ------------------------------------------------------------- компоненты

    private void Resolve()
    {
        if (_resolved)
            return;

        _resolved = true;

        _timerType = Compat.FindType("Content.Shared.Explosion.Components.OnUseTimerTriggerComponent")
                     ?? FindTypeByName("OnUseTimerTriggerComponent", isEnum: false);

        if (_timerType != null)
        {
            _delayMember = (MemberInfo?) _timerType.GetField("Delay", BindingFlags.Public | BindingFlags.Instance)
                           ?? _timerType.GetProperty("Delay", BindingFlags.Public | BindingFlags.Instance);
        }

        var visuals = Compat.FindType("Content.Shared.Trigger.TriggerVisuals")
                      ?? FindTypeByName("TriggerVisuals", isEnum: true);

        if (visuals is { IsEnum: true } && Enum.TryParse(visuals, "VisualState", out var key) && key is Enum e)
            _appearanceKey = e;

        if (_timerType == null || _delayMember == null || _appearanceKey == null)
        {
            Console.WriteLine("[SsMod] таймер гранаты: компонент таймера или Appearance не найден");
        }
    }

    private static Type? FindTypeByName(string name, bool isEnum)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.Name == name && type.IsEnum == isEnum)
                    return type;
            }
        }

        return null;
    }

    private bool IsPrimed(EntityUid uid)
    {
        if (_appearanceKey == null)
            return false;

        return Appearance.TryGetData(uid, _appearanceKey, out object? value)
               && string.Equals(value?.ToString(), "Primed", StringComparison.Ordinal);
    }

    private float DelayOf(object component)
    {
        if (_delayMember == null)
            return 0f;

        try
        {
            var value = _delayMember is FieldInfo field
                ? field.GetValue(component)
                : ((PropertyInfo) _delayMember).GetValue(component);

            return value switch
            {
                float f => f,
                double d => (float) d,
                int i => i,
                _ => 0f,
            };
        }
        catch
        {
            return 0f;
        }
    }

    private static Color ColorFor(float left)
    {
        if (left <= 2f)
            return new Color(1f, 0.2f, 0.2f);

        if (left <= 5f)
            return new Color(1f, 0.65f, 0.15f);

        return new Color(1f, 0.95f, 0.5f);
    }
}
