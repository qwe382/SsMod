using Content.Client.Drowsiness;
using Content.Client.Drugs;
using Content.Client.Drunk;
using Content.Client.Eye.Blinding;
using Content.Client.Flash;
using Content.Client.UserInterface.Systems.DamageOverlays.Overlays;
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
            ToolTip = "Опьянение, потеря крови, без сознания, слепота, вспышка, размытие, радуга",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;
        Console.WriteLine($"[SsMod] no screen effects = {on}");
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
