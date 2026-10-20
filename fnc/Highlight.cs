using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Подсветка персонажа из фрейма «Персонажи»: как у мелбота — линия от нас до
// цели, но круг вокруг неё заметно больше. Два режима:
//   * Add — разовая подсветка на 2 секунды (гаснет сама);
//   * TogglePin — постоянная, горит, пока её не выключат (кнопка-переключатель).
public static class Highlight
{
    // Сколько горит разовая подсветка.
    public const float Lifetime = 2f;

    // Круг в тайлах: у мелбота дуги на 0.5, тут — втрое больше.
    public const float RadiusTiles = 1.5f;

    // Толщина круга и линии в пикселях.
    public const float CircleWidth = 4f;
    public const float LineWidth = 2f;

    private const int Max = 32;

    private static readonly Color CircleColor = new(0.4f, 0.95f, 1f, 0.9f);
    private static readonly Color LineColor = new(0.4f, 0.95f, 1f, 0.45f);

    // Постоянная подсветка: то же самое, но не гаснет, поэтому линия чуть плотнее.
    public static readonly Color CircleSteady = CircleColor;
    public static readonly Color LineSteady = LineColor.WithAlpha(0.6f);

    // (цель, начало, конец). Список читает и чистит слой подсветки.
    public static readonly List<(EntityUid Target, TimeSpan Start, TimeSpan Until)> Active = new();

    // Замки: подсветка горит, пока замок не снимут. Список, а не набор: вверху
    // экрана замки показываются в порядке постановки.
    public static readonly List<EntityUid> Pinned = new();

    // Сколько замков держим одновременно: больше — уже не читается на экране.
    public const int MaxPinned = 12;

    // Растёт на каждое изменение Pinned: по нему список замков понимает, что
    // пора пересобраться.
    internal static int Version;

    // Диагностика: сколько первых кадров подсветки распечатать. Взводится на
    // каждое нажатие «Подсветить», чтобы по логу было видно, что слой рисует
    // и в какие точки экрана.
    internal static int DiagLeft;

    public static void Add(EntityUid target)
    {
        // Слой и список замков поднимаем при первой же подсветке: до этого они не нужны.
        Attach();

        var now = IoCManager.Resolve<IGameTiming>().CurTime;

        // Повторное нажатие продлевает подсветку, а не добавляет вторую запись.
        for (var i = 0; i < Active.Count; i++)
        {
            if (Active[i].Target != target)
                continue;

            Active.RemoveAt(i);
            break;
        }

        Active.Add((target, now, now + TimeSpan.FromSeconds(Lifetime)));

        if (Active.Count > Max)
            Active.RemoveAt(0);

        DiagLeft = 3;
    }

    // Замок: возвращает true, если он встал, и false, если снялся (повторное
    // нажатие). Разовая подсветка той же цели при этом не мешает.
    public static bool TogglePin(EntityUid target)
    {
        Attach();

        var on = !Pinned.Remove(target);
        if (on)
        {
            Pinned.Add(target);

            // Перебор — снимаем самый старый замок.
            while (Pinned.Count > MaxPinned)
                Pinned.RemoveAt(0);
        }

        Version++;
        DiagLeft = 3;
        return on;
    }

    // Снять замок (из списка вверху экрана или из строки таблицы).
    public static void Unpin(EntityUid target)
    {
        if (!Pinned.Remove(target))
            return;

        Version++;
    }

    public static bool IsPinned(EntityUid target) => Pinned.Contains(target);

    // Поднять UI подсветки: слой линий и список замков. Зовётся лениво, уже в игре.
    private static void Attach()
    {
        HighlightLayer.Attach();
        LockList.Attach();
    }

    public static Color CircleOf(TimeSpan now, TimeSpan until)
        => CircleColor.WithAlpha(CircleColor.A * Fade(now, until));

    public static Color LineOf(TimeSpan now, TimeSpan until)
        => LineColor.WithAlpha(LineColor.A * Fade(now, until));

    // Затухание в последние полсекунды: так подсветка не пропадает рывком.
    private static float Fade(TimeSpan now, TimeSpan until)
    {
        var left = (float) (until - now).TotalSeconds;
        return Math.Clamp(left / 0.5f, 0f, 1f);
    }
}

// ---------------------------------------------------------------------------
// Слой подсветки: тот же визуал, что у оверлеев мелбота, но поверх окон.
//
// Почему не оверлей. Оверлеи в ScreenSpace рисуются внутри игрового вьюпорта,
// а окна мода (SsModWindow) лежат в WindowRoot и рисуются ПОСЛЕ него — линия и
// круг уходили под открытый фрейм «Персонажи», из которого подсветку и жмут
// (а строки отсортированы по близости, то есть цель почти всегда за окном).
// Поэтому рисуем из UI-слоя: он добавлен в PopupRoot, а тот идёт после
// WindowRoot (см. UserInterfaceManager._initializeCommon) — поверх всех окон.
// ---------------------------------------------------------------------------
public sealed class HighlightLayer : Control
{
    private static HighlightLayer? _instance;

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly SharedTransformSystem _transform;

    private HighlightLayer()
    {
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
        MouseFilter = MouseFilterMode.Ignore;
    }

    // Поднять слой поверх окон. Зовётся из Highlight.Add, то есть уже в игре:
    // к этому моменту UI (включая PopupRoot) точно инициализирован.
    public static void Attach()
    {
        if (_instance is { Disposed: false })
            return;

        var ui = IoCManager.Resolve<IUserInterfaceManager>();

        var layer = new HighlightLayer();
        LayoutContainer.SetAnchorPreset(layer, LayoutContainer.LayoutPreset.Wide);
        ui.PopupRoot.AddChild(layer);

        _instance = layer;
        Console.WriteLine("[SsMod] highlight layer: поднят поверх окон");
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        try
        {
            DrawHighlights(handle);
        }
        catch (Exception e)
        {
            // Слой рисуется каждый кадр: исключение здесь завалило бы рендер UI.
            Console.Error.WriteLine($"[SsMod] highlight draw failed: {e.Message}");
        }
    }

    private void DrawHighlights(DrawingHandleScreen handle)
    {
        var active = Highlight.Active;
        var pinned = Highlight.Pinned;

        if (active.Count == 0 && pinned.Count == 0)
            return;

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        // Главный вид игры (его же ставит Content) и его точка в мире.
        if (_eye.MainViewport is not { } viewport || viewport is not Control { Visible: true })
            return;

        // Начало отсчёта нашего слоя: он растянут на весь экран, но вычитаем
        // позицию на всякий случай — тогда рисовать можно из любого места.
        var origin = (Vector2) GlobalPixelPosition;
        var now = _timing.CurTime;
        var selfScreen = viewport.WorldToScreen(_transform.GetWorldPosition(self)) - origin;

        // Идём с конца: просроченные записи удаляем прямо здесь.
        for (var i = active.Count - 1; i >= 0; i--)
        {
            var (target, _, until) = active[i];

            if (until <= now
                || !_ent.EntityExists(target)
                || !_ent.HasComponent<TransformComponent>(target))
            {
                active.RemoveAt(i);
                continue;
            }

            DrawTarget(handle, viewport, origin, selfScreen, target,
                Highlight.CircleOf(now, until), Highlight.LineOf(now, until));
        }

        if (pinned.Count > 0)
            DrawPinned(handle, viewport, origin, selfScreen);
    }

    // Постоянные подсветки: те же фигуры, но без затухания. Умершие цели
    // убираем из набора прямо тут — отдельного таймера для этого нет.
    private void DrawPinned(DrawingHandleScreen handle, IViewportControl viewport, Vector2 origin, Vector2 selfScreen)
    {
        List<EntityUid>? gone = null;

        foreach (var target in Highlight.Pinned)
        {
            if (!_ent.EntityExists(target) || !_ent.HasComponent<TransformComponent>(target))
            {
                (gone ??= new List<EntityUid>()).Add(target);
                continue;
            }

            DrawTarget(handle, viewport, origin, selfScreen, target,
                Highlight.CircleSteady, Highlight.LineSteady);
        }

        if (gone == null)
            return;

        foreach (var uid in gone)
            Highlight.Unpin(uid);
    }

    private void DrawTarget(DrawingHandleScreen handle, IViewportControl viewport, Vector2 origin,
        Vector2 selfScreen, EntityUid target, Color circle, Color line)
    {
        var world = _transform.GetWorldPosition(target);
        var screen = viewport.WorldToScreen(world) - origin;
        var radius = Radius(viewport, world, Highlight.RadiusTiles);

        if (Highlight.DiagLeft > 0)
        {
            Highlight.DiagLeft--;
            Console.WriteLine(
                $"[SsMod] highlight draw: n={Highlight.Active.Count}+{Highlight.Pinned.Count} я={selfScreen} цель={screen} r={radius:0.0} viewport={viewport.GetType().Name}");
        }

        CombatVisuals.ThickLine(handle, selfScreen, screen, Highlight.LineWidth, line);
        CombatVisuals.Circle(handle, screen, radius, Highlight.CircleWidth, circle);
    }

    // Радиус круга в экранных пикселях: две точки вида на расстоянии tiles
    // тайлов. Так радиус не зависит ни от зума, ни от масштаба вьюпорта.
    private static float Radius(IViewportControl viewport, Vector2 world, float tiles)
    {
        var center = viewport.WorldToScreen(world);
        var edge = viewport.WorldToScreen(world + new Vector2(tiles, 0f));
        return (edge - center).Length();
    }
}

// ---------------------------------------------------------------------------
// Список замков: вверху экрана по центру перечислены те, на кого поставлен
// замок. Нажатие на имя снимает замок. Живёт в PopupRoot, то есть поверх игры
// и поверх окон мода; сам по себе мышь не ловит — только кнопки имён.
// ---------------------------------------------------------------------------
public sealed class LockList : BoxContainer
{
    private static LockList? _instance;

    [Dependency] private readonly IEntityManager _ent = default!;

    // Имена — вертикально, по центру экрана. Отступ от верха — Margin корня.
    private readonly BoxContainer _names = new()
    {
        Orientation = LayoutOrientation.Vertical,
        HorizontalAlignment = HAlignment.Center,
        MouseFilter = MouseFilterMode.Ignore,
    };

    private int _version = -1;

    private LockList()
    {
        IoCManager.InjectDependencies(this);

        Orientation = LayoutOrientation.Vertical;
        MouseFilter = MouseFilterMode.Ignore;
        Margin = new Thickness(0f, 8f, 0f, 0f);

        AddChild(_names);
    }

    // Поднять список поверх окон. Зовётся из Highlight.Attach, то есть уже в игре.
    public static void Attach()
    {
        if (_instance is { Disposed: false })
            return;

        var ui = IoCManager.Resolve<IUserInterfaceManager>();

        var list = new LockList();
        LayoutContainer.SetAnchorPreset(list, LayoutContainer.LayoutPreset.Wide);
        ui.PopupRoot.AddChild(list);

        _instance = list;
        Console.WriteLine("[SsMod] список замков: поднят поверх окон");
    }

    // Пересобирать каждый кадр незачем: набор меняется только по нажатиям и
    // когда цель исчезает. Highlight.Version растёт ровно на такие события.
    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (_version == Highlight.Version)
            return;

        _version = Highlight.Version;

        // FrameUpdate идёт из UserInterfaceManager.Update без try/catch, поэтому
        // исключение здесь завалило бы обновление всего UI до конца кадра.
        try
        {
            Rebuild();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] список замков: {e.Message}");
        }
    }

    private void Rebuild()
    {
        _names.RemoveAllChildren();

        var any = false;

        foreach (var target in Highlight.Pinned)
        {
            if (!_ent.EntityExists(target))
                continue;

            var uid = target;
            var name = NameOf(uid);

            var button = new Button
            {
                Text = name,
                ToolTip = "Снять замок",
                Margin = new Thickness(0f, 2f, 0f, 2f),
            };

            button.OnPressed += _ =>
            {
                Highlight.Unpin(uid);
                Console.WriteLine($"[SsMod] замок снят: {name}");
            };

            _names.AddChild(button);
            any = true;
        }

        // Пустой список не должен висеть невидимой полосой поверх игры. Прячем
        // именно контейнер имён: скрытый корень перестал бы получать FrameUpdate
        // (см. Control.DoFrameUpdateRecursive) и список больше не появился бы.
        _names.Visible = any;
    }

    private string NameOf(EntityUid uid)
    {
        if (_ent.TryGetComponent(uid, out MetaDataComponent? meta) && !meta.Deleted && meta.EntityName.Length > 0)
            return meta.EntityName;

        return "?";
    }
}
