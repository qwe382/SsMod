using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Content.Client.Examine;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using HarmonyLib;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace SsMod.Fnc;

// Твик: при осмотре (Shift) показываем, что в растворе.
//
// Содержимое растворов сервер клиенту почти не отдаёт, поэтому источников три,
// по убыванию точности:
//
//  1) Раствор, который клиенту всё-таки отдали. Это либо SolutionComponent на самой
//     цели (гипоспрей, шприц, пакет сока), либо сущность-раствор внутри контейнера,
//     либо словарь компонента-менеджера (старые сборки). Тогда количества точные.
//
//  2) Верб "Раствор" (химический сканер): сервер присылает ОТДЕЛЬНЫЙ тултип с точным
//     составом через ExamineSystem.SendExamineTooltip -> UpdateTooltipInfo, то есть
//     через тот же хук. Состав запоминаем по цели и потом показываем его на каждом
//     обычном осмотре — сканировать второй раз не нужно.
//
//  3) Обычный осмотр контейнера с ExaminableSolution сервер дополняет строкой
//     "Оно содержит [color=#RRGGBB]облачное[/color] вещество.": цвет считается по
//     содержимому (Solution.GetColor — у чистого раствора это ровно цвет реагента),
//     а описание — физическое описание основного реагента. По цвету и описанию
//     находим реагент-прототип и подсказываем его. Это догадка, поэтому пишем
//     "похоже", а не выдаём за точный состав.
//
// Тип компонента-менеджера разный (276/290 — SolutionContainerManagerComponent,
// 277 — SolutionManagerComponent), поэтому его ищем по имени и читаем рефлексией:
// прямая ссылка на любой из них = MissingMemberException на чужой сборке.
// Фича без выключателя: осмотр с составом растворов работает всегда.
public static class ReagentExamine
{
    // Одно и то же сообщение может прийти в отрисовку не один раз —
    // помечаем обработанные, чтобы не задвоить строки.
    private static readonly ConditionalWeakTable<FormattedMessage, object> Seen = new();

    private static bool _loggedHook;

    // Разбор того, что не опознали, пишем в лог, но не больше нескольких раз за сессию.
    private static int _diagLogged;

    // --- разбор серверного текста -------------------------------------------------

    // "- 30ед. [color=#A0E0FF]Вода[/color]" — верб сканера (scannable-solution-chemical),
    // ru ("ед.") и en ("u").
    private static readonly Regex ChemicalLine = new(
        @"-\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:ед\.|u)\s*\[color=(#[0-9A-Fa-f]{6})\]([^\[]+)\[/color\]",
        RegexOptions.Compiled);

    // "Контейнер содержит [color=white]30/30ед[/color]." — объём при осмотре.
    private static readonly Regex VolumeLine = new(
        @"\[color=white\]\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:/\s*([0-9]+(?:[.,][0-9]+)?))?\s*(?:ед|u)\s*\[/color\]",
        RegexOptions.Compiled);

    // "Оно содержит [color=#A0E0FF]облачное[/color] вещество." — цвет и описание вещества.
    private static readonly Regex SubstanceLine = new(
        @"\[color=(#[0-9A-Fa-f]{6})\]([^\[]+)\[/color\]\s*(?:вещество|смесь веществ|substance|mixture of substances)",
        RegexOptions.Compiled);

    internal static void Append(FormattedMessage message, EntityUid target)
    {
        if (!_loggedHook)
        {
            _loggedHook = true;
            Console.WriteLine("[SsMod] examine reagents: осмотр перехватывается");
        }

        if (Seen.TryGetValue(message, out _))
            return;

        Seen.Add(message, message);

        var ent = IoCManager.Resolve<IEntityManager>();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        var markup = message.ToMarkup();
        var volume = ParseVolume(markup);

        // 1. Тултип верба сканера: состав точный, запоминаем его для этой цели.
        //    Сам тултип уже показывает состав, дописывать нечего.
        if (ParseChemicals(markup) is { Count: > 0 } scanned)
        {
            Remember(ent, target, scanned, Sum(scanned));

            if (_diagLogged++ < 5)
                Console.WriteLine($"[SsMod] examine reagents: сканер, реагентов {scanned.Count} (цель {target})");

            return;
        }

        // 2. Раствор, который клиенту реально отдали.
        if (ClientSolutions(ent, target, prototypes) is { Count: > 0 } own)
        {
            Remember(ent, target, own, volume?.Current ?? Sum(own));
            Show(message, own);
            return;
        }

        // 3. Ранее снятый состав (сканер или прошлый осмотр), если объём не менялся.
        if (Known(ent, target, volume) is { Count: > 0 } known)
        {
            Show(message, known);
            return;
        }

        // 4. Догадка по серверной строке с цветом и описанием вещества.
        if (ParseSubstance(markup) is { } substance)
        {
            if (Guess(substance.Color, substance.Desc, volume) is { } guess)
                message.AddMarkupOrThrow("\n" + guess);
            else if (_diagLogged++ < 5)
                Console.WriteLine($"[SsMod] examine reagents: под описание '{substance.Desc}' реагент не найден");

            return;
        }

        if (_diagLogged++ < 5)
            Console.WriteLine($"[SsMod] examine reagents: цель {target} — сервер про раствор ничего не сказал");
    }

    // Строки "реагент: сколько" цветом вещества.
    private static void Show(FormattedMessage message, List<Entry> entries)
    {
        // Крупное — вперёд, одинаковые объёмы по алфавиту.
        entries.Sort((a, b) =>
        {
            var byAmount = b.Amount.CompareTo(a.Amount);
            return byAmount != 0 ? byAmount : string.CompareOrdinal(a.Name, b.Name);
        });

        var lines = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var text = $"{FormattedMessage.EscapeText(entry.Name)}: {Number(entry.Amount)}ед";
            lines.Add(entry.Color.Length == 0 ? text : $"[color={entry.Color}]{text}[/color]");
        }

        message.AddMarkupOrThrow("\n" + string.Join("\n", lines));
    }

    // --- раствор, который дошёл до клиента ---------------------------------------

    private static List<Entry>? ClientSolutions(IEntityManager ent, EntityUid target, IPrototypeManager prototypes)
    {
        List<Entry>? entries = null;
        var done = new HashSet<Solution>();

        // 1. Сама цель может быть раствором (гипоспрей, шприц и т. п.).
        if (ent.TryGetComponent(target, out SolutionComponent? own))
            Collect(own.Solution);

        // 2. Сущности-растворы внутри контейнеров цели.
        var solutionContainers = 0;

        if (ent.TryGetComponent(target, out ContainerManagerComponent? containers))
        {
            foreach (var (_, container) in containers.Containers)
            {
                foreach (var child in container.ContainedEntities)
                {
                    if (!ent.TryGetComponent(child, out SolutionComponent? solution))
                        continue;

                    solutionContainers++;
                    Collect(solution.Solution);
                }
            }
        }

        // 3. Словарь компонента-менеджера (старые сборки; в 277 он пуст на клиенте).
        var manager = Manager(ent, target);

        if (manager != null && SolutionsDict(manager) is { } dict)
        {
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

        if (entries == null && (own != null || manager != null || solutionContainers > 0) && _diagLogged++ < 5)
        {
            var dictSize = manager != null && SolutionsDict(manager) is { } filled ? filled.Count : 0;
            var managerName = manager == null ? "нет" : _managerType?.Name ?? "?";

            Console.WriteLine($"[SsMod] examine reagents: растворов {done.Count} без содержимого " +
                              $"(сам={(own != null ? "да" : "нет")}, контейнеров_с_раствором={solutionContainers}, " +
                              $"менеджер={managerName}, словарь={dictSize})");
        }

        return entries;

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
                    color = Hex(proto.SubstanceColor);
                }

                (entries ??= new List<Entry>()).Add(new Entry(name, color, amount));
            }
        }
    }

    // --- запомненный состав -------------------------------------------------------

    // Состав помним по цели вместе с объёмом, при котором он снят: если объём
    // изменился, состав уже другой и его надо забыть.
    private readonly record struct Cached(List<Entry> Entries, float Volume);

    private static readonly Dictionary<EntityUid, Cached> KnownReagents = new();

    private static void Remember(IEntityManager ent, EntityUid target, List<Entry> entries, float volume)
    {
        if (!ent.EntityExists(target))
            return;

        if (KnownReagents.Count > 256)
        {
            KnownReagents.Clear();
            Console.WriteLine("[SsMod] examine reagents: кэш составов очищен");
        }

        KnownReagents[target] = new Cached(entries, volume);
    }

    private static List<Entry>? Known(IEntityManager ent, EntityUid target, (float Current, float? Max)? volume)
    {
        if (!KnownReagents.TryGetValue(target, out var cached))
            return null;

        if (!ent.EntityExists(target))
        {
            KnownReagents.Remove(target);
            return null;
        }

        if (volume is { } current && MathF.Abs(current.Current - cached.Volume) > 0.05f)
        {
            KnownReagents.Remove(target);
            return null;
        }

        return cached.Entries;
    }

    // --- догадка по цвету и описанию ---------------------------------------------

    private readonly record struct ReagentInfo(string Name, string Desc, Color Color);

    private static List<ReagentInfo>? _reagents;

    private static List<ReagentInfo> Reagents()
    {
        if (_reagents != null)
            return _reagents;

        var list = new List<ReagentInfo>();

        try
        {
            var prototypes = IoCManager.Resolve<IPrototypeManager>();

            foreach (var proto in prototypes.EnumeratePrototypes<ReagentPrototype>())
            {
                var name = proto.LocalizedName;

                if (string.IsNullOrEmpty(name))
                    continue;

                list.Add(new ReagentInfo(name, proto.LocalizedPhysicalDescription, proto.SubstanceColor));
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] examine reagents: прототипы реагентов не читаются: {e.Message}");
        }

        _reagents = list;
        Console.WriteLine($"[SsMod] examine reagents: реагентов в прототипах {list.Count}");

        return list;
    }

    private static string? Guess(Color color, string desc, (float Current, float? Max)? volume)
    {
        var all = Reagents();

        if (all.Count == 0)
            return null;

        // Описание основного реагента знаем точно — сначала отбираем по нему,
        // цвет потом разводит похожие вещества (у чистого раствора он совпадает).
        var sameDesc = new List<ReagentInfo>();

        foreach (var reagent in all)
        {
            if (reagent.Desc.Length > 0 && reagent.Desc == desc)
                sameDesc.Add(reagent);
        }

        var pool = sameDesc.Count > 0 ? sameDesc : new List<ReagentInfo>(all);
        pool.Sort((a, b) => Distance(a.Color, color).CompareTo(Distance(b.Color, color)));

        var best = Distance(pool[0].Color, color);
        var names = new List<string>(3);

        foreach (var reagent in pool)
        {
            if (names.Count == 3 || Distance(reagent.Color, color) > best + 40f)
                break;

            names.Add($"[color={Hex(reagent.Color)}]{FormattedMessage.EscapeText(reagent.Name)}[/color]");
        }

        if (names.Count == 0)
            return null;

        var line = "SsMod:";

        if (volume is { } current)
        {
            var max = current.Max is { } top ? $"/{Number(top)}" : string.Empty;
            line += $" {Number(current.Current)}{max}ед —";
        }

        line += sameDesc.Count > 0 ? " похоже:" : " по цвету ближе всего:";

        return $"{line} {string.Join(", ", names)}";
    }

    private static float Distance(Color a, Color b)
    {
        var dr = (a.R - b.R) * 255f;
        var dg = (a.G - b.G) * 255f;
        var db = (a.B - b.B) * 255f;

        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    // --- мелочи ------------------------------------------------------------------

    private static (float Current, float? Max)? ParseVolume(string markup)
    {
        var match = VolumeLine.Match(markup);

        if (!match.Success || !Amount(match.Groups[1].Value, out var current))
            return null;

        float? max = null;

        if (match.Groups[2].Success && Amount(match.Groups[2].Value, out var parsed))
            max = parsed;

        return (current, max);
    }

    private static List<Entry>? ParseChemicals(string markup)
    {
        List<Entry>? list = null;

        foreach (Match match in ChemicalLine.Matches(markup))
        {
            if (!Amount(match.Groups[1].Value, out var amount) || amount <= 0f)
                continue;

            (list ??= new List<Entry>()).Add(new Entry(match.Groups[3].Value.Trim(), match.Groups[2].Value, amount));
        }

        return list;
    }

    private static (Color Color, string Desc)? ParseSubstance(string markup)
    {
        var match = SubstanceLine.Match(markup);

        if (!match.Success || !TryHex(match.Groups[1].Value, out var color))
            return null;

        return (color, match.Groups[2].Value.Trim());
    }

    private static float Sum(List<Entry> entries)
    {
        var sum = 0f;

        foreach (var entry in entries)
            sum += entry.Amount;

        return sum;
    }

    private static bool Amount(string text, out float value)
        => float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // Свой разбор "#RRGGBB": Color.FromHex в сборках движка отличается, а нам нужен
    // только перевод в Color для сравнения с цветом прототипа.
    private static bool TryHex(string text, out Color color)
    {
        color = Color.White;

        if (text.Length != 7 || text[0] != '#' ||
            !byte.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;

        color = new Color(r / 255f, g / 255f, b / 255f, 1f);
        return true;
    }

    private static string Hex(Color color)
    {
        static byte Part(float value) => (byte) Math.Clamp((int) MathF.Round(value * 255f), 0, 255);

        return $"#{Part(color.R):X2}{Part(color.G):X2}{Part(color.B):X2}";
    }

    private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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
// Через этот же метод идёт и тултип верба-сканера (ExamineSystem.SendExamineTooltip),
// поэтому точный состав со сканера мы тоже видим.
//
// В 276 у UpdateTooltipInfo пять параметров (verbs, getVerbs), в 290 — четыре,
// поэтому цепляемся по имени, а из параметров берём только общие для обеих версий.
[HarmonyPatch(typeof(ExamineSystem), "UpdateTooltipInfo")]
internal static class ReagentExaminePatch
{
    private static void Prefix(EntityUid target, FormattedMessage message)
    {
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
