using System.Numerics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace SsMod.Fnc;

// Настройки фрейма «Персонажи». Поля публичные: их регистрирует Config
// (RegisterTree("characters", ...)), поэтому имя поля = хвост ключа конфига.
public static class CharacterSettings
{
    public static bool AutoRefresh = true;
    public static float Interval = 0.5f;
    public static bool AliveOnly;
    public static float MaxDistance;
    public static bool ShowSelf = true;
}

// Один персонаж, как его видит клиент.
public sealed class CharacterInfo
{
    public EntityUid Uid;
    public string Name = "";
    public string Job = "";
    public string StateText = "";
    public MobState State;
    public Priority Priority;
    public bool IsPlayer;
    public bool Self;
    public bool SameMap;

    // Дистанция в тайлах (= метрах). Для другой карты — бесконечность.
    public float Distance;

    public bool HasHealth;
    public float Health;
    public float MaxHealth;

    // Цвет имени — по метке приоритета (Make Friend / Make Enemy).
    public Color Color => Priorities.ColorOf(Priority);
}

// ---------------------------------------------------------------------------
// Снимок: кто есть рядом. Пересобирается по таймеру фрейма «Персонажи»; наружу
// отдаётся готовый список, UI его только рисует.
// ---------------------------------------------------------------------------
public static class Characters
{
    public static readonly List<CharacterInfo> List = new();

    // Живых сначала копируем uid'ами: энумератор живого словаря нельзя держать,
    // пока читаем компоненты (вход нового игрока ломает итерацию).
    private static readonly List<EntityUid> Snapshot = new();

    public static void Refresh(IEntityManager ent, EntityUid self, TimeSpan now)
    {
        var transform = ent.System<SharedTransformSystem>();
        var selfPos = transform.GetWorldPosition(self);
        var selfMap = transform.GetMapId(self);

        Snapshot.Clear();
        var query = ent.AllEntityQueryEnumerator<MobStateComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out _))
            Snapshot.Add(uid);

        List.Clear();

        foreach (var uid in Snapshot)
        {
            if (!ent.TryGetComponent(uid, out MobStateComponent? mob))
                continue;

            if (!ent.TryGetComponent(uid, out MetaDataComponent? meta) || meta.Deleted)
                continue;

            var isSelf = uid == self;

            if (isSelf && !CharacterSettings.ShowSelf)
                continue;

            if (CharacterSettings.AliveOnly && Compat.GetMobState(mob) != MobState.Alive)
                continue;

            if (!ent.HasComponent<TransformComponent>(uid))
                continue;

            var sameMap = transform.GetMapId(uid) == selfMap;
            var distance = sameMap
                ? (transform.GetWorldPosition(uid) - selfPos).Length()
                : float.PositiveInfinity;

            if (CharacterSettings.MaxDistance > 0f && distance > CharacterSettings.MaxDistance)
                continue;

            // fish 340: CurrentState — поле, читаем через Compat.
            var mobState = Compat.GetMobState(mob) ?? MobState.Dead;

            var info = new CharacterInfo
            {
                Uid = uid,
                Name = meta.EntityName,
                State = mobState,
                StateText = Vitals.StateText(mobState),
                Priority = Priorities.Get(uid),
                IsPlayer = ent.HasComponent<ActorComponent>(uid),
                Self = isSelf,
                SameMap = sameMap,
                Distance = distance,
                Job = Jobs.Of(ent, uid, now, out _) ?? "",
            };

            if (Vitals.Health(ent, uid, out var hp, out var hpMax))
            {
                info.HasHealth = true;
                info.Health = hp;
                info.MaxHealth = hpMax;
            }

            List.Add(info);
        }

        List.Sort(Compare);
    }

    // Игроки сверху, NPC — в конце; внутри группы ближние первыми.
    private static int Compare(CharacterInfo a, CharacterInfo b)
    {
        if (a.IsPlayer != b.IsPlayer)
            return a.IsPlayer ? -1 : 1;

        var byDistance = a.Distance.CompareTo(b.Distance);

        // Равные — по имени: порядок должен быть устойчивым, иначе строки
        // прыгали бы на каждом замере.
        return byDistance != 0
            ? byDistance
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
    }

    // ---------- вкладка окна ----------

    public static Control BuildTab() => new CharactersPanel();
}

// ---------------------------------------------------------------------------
// Фрейм «Персонажи»: таблица всех, кого видит клиент. Пересобирается по таймеру,
// пока фрейм открыт (FrameUpdate зовётся у видимых контролов), поэтому закрытый
// фрейм ничего не стоит.
// ---------------------------------------------------------------------------
public sealed class CharactersPanel : BoxContainer
{
    private const int Columns = 6;

    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly GridContainer _rows = new() { Columns = Columns };

    private double _sinceRefresh;

    public CharactersPanel()
    {
        IoCManager.InjectDependencies(this);

        Orientation = LayoutOrientation.Vertical;
        AddChild(_rows);

        RefreshNow();
    }

    // Пока фрейм открыт — раз в Interval пересобираем таблицу.
    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (!CharacterSettings.AutoRefresh)
            return;

        _sinceRefresh += args.DeltaSeconds;
        if (_sinceRefresh < Math.Clamp(CharacterSettings.Interval, 0.1f, 10f))
            return;

        RefreshNow();
    }

    private void RefreshNow()
    {
        // FrameUpdate идёт из UserInterfaceManager.Update без try/catch, поэтому
        // исключение здесь завалило бы обновление и раскладку всего UI.
        try
        {
            RefreshInner();
            _lastError = "";
        }
        catch (Exception e)
        {
            if (e.Message == _lastError)
                return;

            _lastError = e.Message;
            Console.Error.WriteLine($"[SsMod] таблица персонажей: {e}");
        }
    }

    private string _lastError = "";

    private void RefreshInner()
    {
        _sinceRefresh = 0;
        _rows.RemoveAllChildren();

        AddCell("Имя", 170f);
        AddCell("Профессия", 120f);
        AddCell("Кто", 55f);
        AddCell("Состояние", 80f);
        AddCell("HP", 90f);
        AddCell("Действия", 210f);

        if (_player.LocalEntity is not { Valid: true } self)
            return;

        Characters.Refresh(_ent, self, _timing.CurTime);

        foreach (var info in Characters.List)
        {
            AddCell(info.Self ? $"{info.Name} (ты)" : info.Name, 170f, info.Color);
            AddCell(info.Job.Length > 0 ? info.Job : "—", 120f);
            AddCell(info.Self ? "я" : info.IsPlayer ? "игрок" : "NPC", 55f);
            AddCell(info.StateText, 80f, StateColor(info.State));
            AddCell(info.HasHealth ? $"{(int) info.Health}/{(int) info.MaxHealth}" : "—", 90f);
            _rows.AddChild(Actions(info));
        }
    }

    private void AddCell(string text, float minWidth, Color? color = null)
    {
        var label = new Label { Text = text, MinWidth = minWidth };
        if (color is { } value)
            label.Modulate = value;

        _rows.AddChild(label);
    }

    // Кнопки действий по строке: три цветных квадрата-метки (друг / враг /
    // нейтрал) и две кнопки подсветки — постоянная (лампочка) и разовая на
    // 2 секунды (часы).
    private Control Actions(CharacterInfo info)
    {
        var box = new BoxContainer { Orientation = LayoutOrientation.Horizontal };
        box.AddChild(Mark(info, Priority.Friend, "Друг — зелёный квадрат"));
        box.AddChild(Mark(info, Priority.Enemy, "Враг — красный квадрат"));
        box.AddChild(Mark(info, Priority.Neutral, "Нейтрал (снять метку) — жёлтый квадрат"));

        // Замок: подсветка горит, пока замок не снимут (в строке таблицы или
        // по имени в списке вверху экрана).
        var pinned = Highlight.IsPinned(info.Uid);
        var toggle = Icons.Button(Icons.Lock, 20f,
            pinned ? "Снять замок" : "Замок: держать подсветку",
            () =>
            {
                var on = Highlight.TogglePin(info.Uid);
                Console.WriteLine($"[SsMod] замок {(on ? "поставлен" : "снят")}: {info.Name}");
                RefreshNow();
            });
        toggle.Modulate = pinned ? Color.White : new Color(0.62f, 0.62f, 0.62f);

        // Разовая подсветка — как было: линия и круг на 2 секунды.
        box.AddChild(toggle);
        box.AddChild(Icons.Button(Icons.Clock, 20f, "Подсветить на 2 секунды", () =>
        {
            Highlight.Add(info.Uid);
            Console.WriteLine($"[SsMod] подсветка: {info.Name}");
        }));

        return box;
    }

    // Квадрат-метка. Живёт ровно как кнопка: своего состояния не хранит,
    // текущую метку видно по цвету имени в таблице.
    private Control Mark(CharacterInfo info, Priority priority, string tooltip)
        => Icons.Square(Priorities.ColorOf(priority), 20f, tooltip,
            () => SetPriority(info.Uid, priority));

    private void SetPriority(EntityUid uid, Priority priority)
    {
        Priorities.Set(_ent.GetNetEntity(uid), priority);
        RefreshNow();
    }

    private static Color StateColor(MobState state) => state switch
    {
        MobState.Alive => new Color(0.85f, 0.95f, 0.85f),
        MobState.Critical => new Color(1f, 0.65f, 0.25f),
        _ => new Color(0.6f, 0.6f, 0.6f),
    };
}
