using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Content.Client.Examine;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using HarmonyLib;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace SsMod.Fnc;

// Твик: при осмотре (Shift) дописываем в текст осмотра содержимое растворов —
// сколько чего и, если реагент известен, его цветом.
//
// Раствор — это отдельная сущность со своим сетевым SolutionComponent
// (Solution.Contents — список ReagentQuantity: id реагента + количество).
// Она лежит внутри контейнера цели, а содержимое контейнеров клиенту приходит
// (ContainerManagerComponent сетевой), поэтому обходим ВСЕ контейнеры цели.
// Имена контейнеров угадывать нельзя: в 276/290 это отдельный слот solution@<имя>
// на каждый раствор, в 277 — один общий контейнер "solutions", и словарь
// SolutionManagerComponent.Solutions клиенту вообще не уезжает (в авто-стейте
// только имя контейнера, а сам словарь помечен ViewVariables).
//
// Тип компонента-менеджера тоже разный (276/290 — SolutionContainerManagerComponent,
// 277 — SolutionManagerComponent), поэтому его ищем по имени и читаем рефлексией:
// прямая ссылка на любой из них = MissingMemberException на чужой сборке.
//
// Цвет берём из прототипа реагента (ReagentPrototype.SubstanceColor),
// имена — из LocalizedName (локализованные).
public static class ReagentExamine
{
    // Включено по умолчанию: фича просилась именно как поведение осмотра.
    internal static bool Enabled { get; private set; } = true;

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Осмотр: вещества",
            Pressed = Enabled,
            ToolTip = "При осмотре показывает содержимое растворов: сколько чего, текстом цвета вещества",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;
        Console.WriteLine($"[SsMod] examine reagents = {on}");
    }

    // Одно и то же сообщение может прийти в отрисовку не один раз —
    // помечаем обработанные, чтобы не задвоить строки.
    private static readonly ConditionalWeakTable<FormattedMessage, object> Seen = new();

    // Однократные отметки в логе: по ним видно, что хук осмотра вообще живёт.
    private static bool _loggedHook;
    private static bool _loggedHit;

    // Если раствор не нашли — печатаем разбор цели, но не больше нескольких раз
    // за сессию, чтобы не залить лог.
    private static int _diagLogged;

    internal static void Append(FormattedMessage message, EntityUid target)
    {
        if (!_loggedHook)
        {
            _loggedHook = true;
            Console.WriteLine($"[SsMod] examine reagents: осмотр перехватывается (цель {target})");
        }

        if (Seen.TryGetValue(message, out _))
            return;

        Seen.Add(message, message);

        var ent = IoCManager.Resolve<IEntityManager>();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        List<Entry>? entries = null;
        HashSet<Solution> done = new();
        var foundContainers = new List<string>();
        var reagentsSeen = 0;

        // 1. Сам носитель может быть раствором (гипоспрей, шприц и т. п.).
        var self = ent.TryGetComponent(target, out SolutionComponent? own);

        if (self)
            Collect(own!.Solution);

        // 2. Сущности-растворы внутри контейнеров цели.
        if (ent.TryGetComponent(target, out ContainerManagerComponent? containers))
        {
            foreach (var (id, container) in containers.Containers)
            {
                foreach (var child in container.ContainedEntities)
                {
                    if (!ent.TryGetComponent(child, out SolutionComponent? solution))
                        continue;

                    foundContainers.Add(id);
                    Collect(solution.Solution);
                }
            }
        }

        // 3. Словарь компонента-менеджера (старые сборки; в 277 он пуст на клиенте).
        var manager = Manager(ent, target);
        var dictSize = 0;

        if (manager != null && SolutionsDict(manager) is { } dict)
        {
            dictSize = dict.Count;

            foreach (DictionaryEntry item in dict)
            {
                switch (item.Value)
                {
                    case Solution solution:
                        Collect(solution);
                        break;
                    case Entity<SolutionComponent> solutionEntity:
                        Collect(solutionEntity.Comp.Solution);
                        break;
                }
            }
        }

        if (entries == null)
        {
            Diag(ent, target, self, containers, manager, dictSize, foundContainers);
            return;
        }

        // Крупное — вперёд, одинаковые объёмы по алфавиту.
        entries.Sort((a, b) =>
        {
            var byAmount = b.Amount.CompareTo(a.Amount);
            return byAmount != 0 ? byAmount : string.CompareOrdinal(a.Name, b.Name);
        });

        var lines = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var value = entry.Amount.ToString("0.##", CultureInfo.InvariantCulture);
            var text = $"{FormattedMessage.EscapeText(entry.Name)}: {value}u";

            lines.Add(entry.Color.Length == 0 ? text : $"[color={entry.Color}]{text}[/color]");
        }

        message.AddMarkupOrThrow("\n" + string.Join("\n", lines));

        if (!_loggedHit)
        {
            _loggedHit = true;
            Console.WriteLine($"[SsMod] examine reagents: растворов {done.Count}, строк {lines.Count}");
        }

        void Collect(Solution solution)
        {
            if (!done.Add(solution))
                return;

            foreach (var reagent in solution.Contents)
            {
                var amount = (float) reagent.Quantity;
                if (amount <= 0f)
                    continue;

                var name = reagent.Reagent.Prototype;
                var color = string.Empty;

                if (prototypes.TryIndex(name, out ReagentPrototype? proto))
                {
                    name = proto.LocalizedName;
                    // Цвет вещества в вид #RRGGBB — как и в самом тексте осмотра.
                    var substance = proto.SubstanceColor;
                    color = $"#{substance.R:X2}{substance.G:X2}{substance.B:X2}";
                }

                (entries ??= new List<Entry>()).Add(new Entry(name, color, amount));
            }
        }
    }

    // Разбор цели, у которой растворов не нашли: видно, что именно клиент получил.
    private static void Diag(IEntityManager ent, EntityUid target, bool self,
        ContainerManagerComponent? containers, object? manager, int dictSize, List<string> foundContainers)
    {
        if (_diagLogged >= 5)
            return;

        _diagLogged++;

        var proto = "?";
        if (ent.TryGetComponent(target, out MetaDataComponent? meta))
            proto = meta.EntityPrototype?.ID ?? meta.EntityName;

        var all = containers == null
            ? "нет"
            : string.Join(",", containers.Containers.Select(c => $"{c.Key}:{c.Value.ContainedEntities.Count}"));

        Console.WriteLine($"[SsMod] examine diag: цель={proto} сам_раствор={self} " +
                          $"менеджер={(manager == null ? "нет" : _managerType?.Name)} словарь={dictSize} " +
                          $"контейнеры=[{all}] с_раствором=[{string.Join(",", foundContainers)}]");
    }

    // Компонент-менеджер растворов: и тип, и словарь достаём рефлексией и держим
    // закэшированными — прямой ссылки на этот тип быть не должно (см. шапку файла).
    private static Type? _managerType;
    private static FieldInfo? _solutionsField;
    private static bool _managerResolved;

    private static object? Manager(IEntityManager ent, EntityUid target)
    {
        if (!_managerResolved)
        {
            _managerResolved = true;

            _managerType = Compat.FindType("Content.Shared.Chemistry.Components.SolutionManager.SolutionManagerComponent")
                           ?? Compat.FindType("Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent");
            _solutionsField = _managerType?.GetField("Solutions", BindingFlags.Public | BindingFlags.Instance);

            if (_managerType == null)
                Console.WriteLine("[SsMod] examine reagents: компонент-менеджер растворов не найден");
        }

        if (_managerType == null)
            return null;

        ent.TryGetComponent(target, _managerType, out var manager);
        return manager;
    }

    private static IDictionary? SolutionsDict(object manager)
        => _solutionsField?.GetValue(manager) as IDictionary;

    private readonly record struct Entry(string Name, string Color, float Amount);
}

// Осмотр строит Content.Client.Examine.ExamineSystem: сервер присылает готовый
// текст осмотра, клиент кладёт его в тултип. Дописываем свои строки до отрисовки.
//
// В 276 у UpdateTooltipInfo пять параметров (verbs, getVerbs), в 290 — четыре,
// поэтому цепляемся по имени, а из параметров берём только общие для обеих версий.
[HarmonyPatch(typeof(ExamineSystem), "UpdateTooltipInfo")]
internal static class ReagentExaminePatch
{
    private static void Prefix(EntityUid target, FormattedMessage message)
    {
        if (!ReagentExamine.Enabled)
            return;

        try
        {
            ReagentExamine.Append(message, target);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] examine reagents failed: {e.Message}");
        }
    }
}
