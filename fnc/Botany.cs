using System.Globalization;
using System.Reflection;
using System.Text;
using Content.Client.Examine;
using Content.Shared.Botany.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using HarmonyLib;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace SsMod.Fnc;

public static class BotanyRead
{
    public const string LabelName = "SsModBotanyInfo";

    private static bool? _available;

    // Есть ли на этой сборке штатная ботаника. На форках (например Corvax Frontier)
    // компонентов Content.Shared.Botany.Components.* нет — там фича молча выключена.
    public static bool Available
    {
        get
        {
            if (_available.HasValue)
                return _available.Value;

            _available = Compat.FindType("Content.Shared.Botany.Components.PlantHolderComponent") != null
                         || Compat.FindType("Content.Shared.Botany.Components.PlantComponent") != null;
            return _available.Value;
        }
    }

    public static string? BuildText(EntityUid target, string existingText)
    {
        var ent = IoCManager.Resolve<IEntityManager>();

        ent.TryGetComponent(target, out PlantComponent? plant);
        ent.TryGetComponent(target, out PlantHolderComponent? holder);
        ent.TryGetComponent(target, out PlantDataComponent? data);
        ent.TryGetComponent(target, out PlantChemicalsComponent? chemicals);

        if (plant == null && holder == null)
            return null;

        var lines = new List<string>();

        if (data != null)
            lines.Add($"Вид: {Loc.GetString(data.Name)}");

        if (plant != null)
        {
            lines.Add($"Потенция: {N(plant.Potency)} • Урожай: {plant.Yield}");
            lines.Add($"Живучесть: {N(plant.Endurance)} • Созревание: {N(plant.Maturation)} • Производство: {N(plant.Production)}");
            lines.Add($"Срок жизни: {N(plant.Lifespan)} • Стадий роста: {plant.GrowthStages}");
        }

        if (holder != null)
        {
            lines.Add($"Возраст: {holder.Age} • Здоровье: {N(holder.Health)}");

            // fish 340: поля MutationLevel нет — пишем то, что знаем.
            var mutation = HolderField(holder, "MutationLevel");
            if (mutation.HasValue)
                lines.Add($"Мутации: {N(mutation.Value)}/{N(holder.MaxMutationLevel)} (шанс ×{N(holder.MutationMod)}) • Мод. урожая ×{holder.YieldMod}");
            else
                lines.Add($"Мод. урожая ×{holder.YieldMod}");

            var flags = new List<string>();
            if (holder.Dead)
                flags.Add("[color=#d9534f]мёртв[/color]");
            if (holder.ReadyForHarvest)
                flags.Add("[color=#5bc0de]готов к сбору[/color]");
            if (holder.ImproperHeat)
                flags.Add("[color=#d9534f]плохая температура[/color]");
            if (holder.ImproperPressure)
                flags.Add("[color=#d9534f]плохое давление[/color]");
            if (holder.MissingGas)
                flags.Add("[color=#d9534f]нет газов[/color]");
            if (flags.Count > 0)
                lines.Add(string.Join(" • ", flags));
        }

        if (plant != null || holder != null)
        {
            var traits = TraitsText(ent, target, existingText);
            if (traits.Count > 0)
                lines.Add($"Черты: {string.Join(", ", traits)}");
        }

        if (chemicals != null && chemicals.Chemicals.Count > 0)
        {
            var proto = IoCManager.Resolve<IPrototypeManager>();
            var chems = new List<string>();

            foreach (var (reagentId, quantity) in chemicals.Chemicals)
            {
                var name = proto.TryIndex<ReagentPrototype>(reagentId.Id, out var reagent)
                    ? reagent.LocalizedName
                    : reagentId.Id;
                var min = (float)quantity.Min;
                var max = (float)quantity.Max;
                chems.Add($"{name} ({N(min)}–{N(max)})");
            }

            lines.Add($"Химикаты: {string.Join(", ", chems)}");
        }

        if (lines.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("[b][color=#8fd38f]Ботаника[/color][/b]");
        foreach (var line in lines)
            sb.Append('\n').Append(line);

        return sb.ToString();
    }

    // Черты растения. Тип компонента ищем по имени: на «маринах» (движок 264)
    // пространства Content.Shared.Botany.Traits.Components нет вовсе, и прямое
    // обращение к нему роняло загрузку типа, а с ним и PatchAll. Поле TraitState
    // читаем рефлексией по той же причине.
    private static List<string> TraitsText(IEntityManager ent, EntityUid target, string existingText)
    {
        var result = new List<string>();

        var type = Compat.FindType("Content.Shared.Botany.Traits.Components.PlantTraitsComponent");
        if (type is null)
            return result;

        var field = type.GetField("TraitState", BindingFlags.Public | BindingFlags.Instance);
        var property = type.GetProperty("TraitState", BindingFlags.Public | BindingFlags.Instance);

        try
        {
            foreach (var (uid, comp) in ent.GetAllComponents(type))
            {
                if (uid != target)
                    continue;

                var state = field?.GetValue(comp) ?? property?.GetValue(comp);
                if (state is null)
                    continue;

                var id = state.ToString();
                if (string.IsNullOrEmpty(id))
                    continue;

                var text = Loc.GetString(id!);
                if (result.Contains(text) || existingText.Contains(text))
                    continue;

                result.Add(text);
            }
        }
        catch
        {
            // Сборка без этих компонентов — просто не показываем черты.
        }

        return result;
    }

    private static string N(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // Поле PlantHolderComponent, которого может не быть на другой сборке
    // (fish 340 убрал MutationLevel). null — поля нет.
    private static float? HolderField(object holder, string name)
    {
        try
        {
            return holder.GetType().GetField(name) is not { } field
                ? null
                : (float) (FixedPoint2) (field.GetValue(holder) ?? FixedPoint2.Zero);
        }
        catch
        {
            return null;
        }
    }
}

[HarmonyPatch(typeof(ExamineSystem), nameof(ExamineSystem.UpdateTooltipInfo))]
public static class BotanyExaminePatch
{
    public static void Postfix(ExamineSystem __instance, EntityUid target, FormattedMessage message)
    {
        // На сборках без штатной ботаники выходим сразу: BuildText там даже не JIT-ится,
        // иначе наружу летит TypeLoadException на каждый осмотр.
        if (!BotanyRead.Available)
            return;

        try
        {
            var field = typeof(ExamineSystem).GetField("_examineTooltipOpen", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(__instance) is not Popup popup)
                return;

            if (popup.ChildCount == 0 || popup.GetChild(0).ChildCount == 0)
                return;

            if (popup.GetChild(0).GetChild(0) is not BoxContainer vBox)
                return;

            Control? old = null;
            Control? buttons = null;
            foreach (var child in vBox.Children)
            {
                if (child.Name == BotanyRead.LabelName)
                    old = child;
                else if (child.Name == "ExamineButtonsHBox")
                    buttons = child;
            }

            if (old != null)
                vBox.RemoveChild(old);

            var text = BotanyRead.BuildText(target, PlainText(message));
            if (text == null)
                return;

            var label = new RichTextLabel
            {
                Name = BotanyRead.LabelName,
                Margin = new Thickness(4, 4, 0, 4),
            };
            // 264 не имеет SetMessage(FormattedMessage, Color?) — общая перегрузка
            // с tagsAllowed есть во всех версиях.
            label.SetMessage(FormattedMessage.FromMarkupPermissive(text), null, null);
            vBox.AddChild(label);

            if (buttons != null)
            {
                vBox.RemoveChild(buttons);
                vBox.AddChild(buttons);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] botany examine: {e}");
        }
    }

    private static string PlainText(FormattedMessage message)
    {
        var sb = new StringBuilder();
        foreach (var node in message.Nodes)
        {
            if (node.Name != null)
                continue;

            var text = node.Value.StringValue;
            if (!string.IsNullOrWhiteSpace(text))
                sb.Append(text);
        }

        return sb.ToString();
    }
}
