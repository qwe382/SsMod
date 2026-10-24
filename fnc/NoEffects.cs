using System;
using System.Collections;
using System.Reflection;
using Content.Client.Drowsiness;
using Content.Client.Drugs;
using Content.Client.Drunk;
using Content.Client.Eye.Blinding;
using Content.Client.Flash;
using Content.Client.UserInterface.Systems.DamageOverlays.Overlays;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;

namespace SsMod.Fnc;

// Твик: убираем экранные эффекты — опьянение, боль/потеря крови, без сознания,
// слепота, размытие, вспышка, «радуга» от наркотиков.
//
// Каждый эффект — отдельный Overlay, который рисует поверх экрана шейдером.
// Патчим его Draw: при включённом твике он просто не рисуется (эффект прозрачный).
public static class NoEffects
{
    internal static bool Enabled { get; private set; }

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Убрать эффекты экрана",
            Pressed = Enabled,
            ToolTip = "Опьянение, потеря крови, без сознания, слепота, вспышка, размытие, радуга, робо-зрение",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;
        Console.WriteLine($"[SsMod] no screen effects = {on}");

        // Оверлеи, добавленные до включения твика (или до наложения патчей),
        // остаются в менеджере: вычищаем их вручную.
        if (on)
            Purge();
    }

    // Удаляет из менеджера уже добавленные vision-оверлеи.
    private static void Purge()
    {
        try
        {
            var overlays = IoCManager.Resolve<IOverlayManager>();
            var managerType = Compat.FindType("Robust.Client.Graphics.OverlayManager");
            var field = managerType?.GetField("_overlays",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field?.GetValue(overlays) is not IDictionary dict)
                return;

            var removed = 0;
            var keys = new List<Type>();
            foreach (Type type in dict.Keys)
                keys.Add(type);

            foreach (var type in keys)
            {
                if (!IsVisionOverlay(type, null))
                    continue;

                overlays.RemoveOverlay(type);
                removed++;
                Console.WriteLine($"[SsMod] no effects: убран добавленный оверлей {type.Name}");
            }

            if (removed > 0)
                Console.WriteLine($"[SsMod] no effects: вычищено оверлеев: {removed}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] no effects: чистка: {e.Message}");
        }
    }

    // Зрение робота/борга (Frontier, КПБ) и прочие «vision»-оверлеи —
    // NightVisionOverlay, ThermalVisionOverlay и т.п. Эти типы есть не во всех
    // версиях клиента (в 276 их нет вовсе, в Sunrise-форках другие неймспейсы),
    // поэтому атрибутом не патчим — ищем рефлексией прямо в Content.Client.
    //
    // Фильтр широкий: зелёный фильтр ночного зрения у части форков живёт не в
    // *VisionOverlay, а в классах вроде NightVisionFilterOverlay и
    // HalfNightVisionBrightnessOverlay — по старому фильтру они не матчились,
    // и зрение борга оставалось включённым.
    public static void PatchVisionOverlays(Harmony harmony)
    {
        var client = Array.Find(AppDomain.CurrentDomain.GetAssemblies(),
            a => a.GetName().Name == "Content.Client");
        if (client is null)
            return;

        Type? overlayType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            overlayType = assembly.GetType("Robust.Client.Graphics.Overlay", false);
            if (overlayType is not null)
                break;
        }

        Type[] types;
        try
        {
            types = client.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = Array.FindAll(e.Types, t => t is not null)!;
        }

        var prefix = new HarmonyMethod(typeof(NoEffects)
            .GetMethod(nameof(VisionDrawPrefix), BindingFlags.NonPublic | BindingFlags.Static)!);

        var patched = 0;
        var closed = new List<string>();
        foreach (var type in types)
        {
            if (!IsVisionOverlay(type, overlayType))
                continue;

            // Патчим только свой Draw, не унаследованный: базовые классы вроде
            // BaseVisionOverlay без переопределения патчить не нужно.
            MethodInfo? draw = null;
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "Draw" && m.DeclaringType == type)
                {
                    draw = m;
                    break;
                }
            }

            if (draw is null)
                continue;

            try
            {
                harmony.Patch(draw, prefix: prefix);
                patched++;
                closed.Add(type.Name);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SsMod] no effects: {type.FullName}: {e.Message}");
            }
        }

        // Страховка: пока твик включён, vision-оверлеи вообще не добавляются в
        // менеджер. Ловит форки, где Draw живёт в базовом классе с нейтральным
        // именем, и случаи, когда оверлей добавили до наложения патча.
        var managerType = Compat.FindType("Robust.Client.Graphics.OverlayManager");
        var add = managerType?.GetMethod("AddOverlay",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { overlayType ?? typeof(object) }, null);
        if (add is not null)
        {
            try
            {
                harmony.Patch(add, prefix: new HarmonyMethod(typeof(NoEffects)
                    .GetMethod(nameof(BlockVisionAddPrefix), BindingFlags.NonPublic | BindingFlags.Static)!));
                Console.WriteLine("[SsMod] no screen effects: vision-оверлеев закрыто " +
                                  $"{patched} ({string.Join(", ", closed)}), добавление заблокировано");
                return;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SsMod] no effects: AddOverlay: {e.Message}");
            }
        }

        Console.WriteLine($"[SsMod] no screen effects: vision-оверлеев закрыто {patched} " +
                          $"({string.Join(", ", closed)})");
    }

    // Экранный эффект зрения: имя содержит «Vision» и кончается на Overlay.
    // Свои оверлеи мода (MelBotOverlay, InteractQueueOverlay и т.д.) сюда не
    // попадают, а лишний матч при включённом твике просто не рисуется.
    internal static bool IsVisionOverlay(Type type, Type? overlayType)
    {
        if (overlayType is not null && !overlayType.IsAssignableFrom(type))
            return false;

        var name = type.Name;
        return name.EndsWith("Overlay") && name.Contains("Vision");
    }

    private static bool VisionDrawPrefix() => !Enabled;

    // Лог всех добавлений оверлеев: первые 40 показывают, какой оверлей
    // рисует роботу его «зрение», даже если фильтр его не ловит.
    private static int _addLogged;

    private static bool BlockVisionAddPrefix(object overlay)
    {
        var type = overlay.GetType();
        var blocked = Enabled && IsVisionOverlay(type, null);

        if (_addLogged++ < 40)
            Console.WriteLine($"[SsMod] no effects: AddOverlay {type.Name} -> " +
                              (blocked ? "заблокирован" : "пропущен"));

        return !blocked;
    }
}

// Опьянение.
[HarmonyPatch(typeof(DrunkOverlay), "Draw")]
internal static class NoEffectsDrunkPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// Потеря крови, удушье, критическое состояние, смерть.
[HarmonyPatch(typeof(DamageOverlay), "Draw")]
internal static class NoEffectsDamagePatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// Слепота.
[HarmonyPatch(typeof(BlindOverlay), "Draw")]
internal static class NoEffectsBlindPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// Размытие (повреждение глаз).
[HarmonyPatch(typeof(BlurryVisionOverlay), "Draw")]
internal static class NoEffectsBlurryPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// Сонливость.
[HarmonyPatch(typeof(DrowsinessOverlay), "Draw")]
internal static class NoEffectsDrowsyPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// Вспышка.
[HarmonyPatch(typeof(FlashOverlay), "Draw")]
internal static class NoEffectsFlashPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// «Радуга» от наркотиков.
[HarmonyPatch(typeof(RainbowOverlay), "Draw")]
internal static class NoEffectsRainbowPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}
