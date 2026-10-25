using System;
using System.Reflection;
using Content.Client.Drowsiness;
using Content.Client.Drugs;
using Content.Client.Drunk;
using Content.Client.Eye.Blinding;
using Content.Client.Flash;
using HarmonyLib;
using Robust.Client.UserInterface.Controls;

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
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SsMod] no effects: {type.FullName}: {e.Message}");
            }
        }

        // Потеря крови, удушье, критическое состояние, смерть — DamageOverlay.
        // Атрибутом не патчим: на fish 340 тип переехал в другой неймспейс,
        // класс патча с прямой ссылкой не загружался и молча выпадал из PatchAll.
        var damageType = Compat.FindType("Content.Client.UserInterface.Systems.DamageOverlays.Overlays.DamageOverlay")
                         ?? Compat.FindType("Content.Client.DamageOverlay.DamageOverlay");
        if (damageType is not null)
        {
            MethodInfo? damageDraw = null;
            foreach (var m in damageType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "Draw" && m.DeclaringType == damageType)
                {
                    damageDraw = m;
                    break;
                }
            }

            if (damageDraw is not null)
            {
                try
                {
                    harmony.Patch(damageDraw, prefix: prefix);
                    patched++;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[SsMod] no effects: {damageType.FullName}: {e.Message}");
                }
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
                                  $"{patched}, добавление заблокировано");
                return;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SsMod] no effects: AddOverlay: {e.Message}");
            }
        }

        Console.WriteLine($"[SsMod] no screen effects: vision-оверлеев закрыто {patched}");
    }

    // Экранный эффект зрения: имя содержит «Vision» и кончается на Overlay.
    // Отдельно — RetroMonitorOverlay: CRT/VHS-экран IPC (КПБ) на фронтире,
    // включается, когда играешь за IPC. Свои оверлеи мода (MelBotOverlay,
    // InteractQueueOverlay и т.д.) сюда не попадают, а лишний матч при
    // включённом твике просто не рисуется.
    internal static bool IsVisionOverlay(Type type, Type? overlayType)
    {
        if (overlayType is not null && !overlayType.IsAssignableFrom(type))
            return false;

        var name = type.Name;
        return name.EndsWith("Overlay")
               && (name.Contains("Vision") || name.Contains("RetroMonitor"));
    }

    private static bool VisionDrawPrefix() => !Enabled;

    private static bool BlockVisionAddPrefix(object overlay)
    {
        return !(Enabled && IsVisionOverlay(overlay.GetType(), null));
    }
}

// Опьянение.
[HarmonyPatch(typeof(DrunkOverlay), "Draw")]
internal static class NoEffectsDrunkPatch
{
    private static bool Prefix() => !NoEffects.Enabled;
}

// Потеря крови, удушье, критическое состояние, смерть — DamageOverlay
// патчится рефлексией в PatchVisionOverlays: тип переехал на fish 340,
// прямая ссылка роняла загрузку класса патча.

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
