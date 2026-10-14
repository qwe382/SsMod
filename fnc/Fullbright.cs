using System.Reflection;
using Content.Client.UserInterface.Systems.MenuBar;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace SsMod.Ui;

// --- write-guard (раньше лежало в fnc/Guard.cs) ---
// После первого нажатия "fb" мод становится единственным, кто может писать
// LightManager.Enabled; все прочие записи блокируются (см. LightEnabledGuard).
public static class SsWrite
{
    public static bool Active;
    public static bool Locked;

    public static void Run(Action action)
    {
        Locked = true;
        Active = true;
        try
        {
            action();
        }
        finally
        {
            Active = false;
        }
    }
}

[HarmonyPatch(typeof(LightManager), "set_Enabled")]
public static class LightEnabledGuard
{
    public static bool Prefix()
    {
        return !SsWrite.Locked || SsWrite.Active;
    }
}

// --- кнопка fb ---
[HarmonyPatch(typeof(GameTopMenuBarUIController), nameof(GameTopMenuBarUIController.LoadButtons))]
public static class FullbrightPatch
{
    private static Button? _btn;

    public static void Postfix(GameTopMenuBarUIController __instance)
    {
        try
        {
            if (_btn != null && _btn.Parent != null)
                return;

            var bar = GetProp(__instance, "GameTopMenuBar") as Control;
            if (bar == null)
                return;

            var container = GetProp(bar, "MenuButtons") as Control;
            if (container == null)
                return;

            var btn = new Button
            {
                Text = "fb",
                Margin = new Thickness(0),
            };
            btn.OnPressed += _ =>
            {
                try
                {
                    var light = IoCManager.Resolve<ILightManager>();
                    SsWrite.Run(() => light.Enabled = !light.Enabled);
                    Console.WriteLine($"[SsMod] fullbright {(light.Enabled ? "off" : "on")}");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[SsMod] fullbright failed: {e}");
                }
            };

            container.AddChild(btn);
            _btn = btn;
            Console.WriteLine("[SsMod] fullbright button added");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] FullbrightPatch failed: {e}");
        }
    }

    private static object? GetProp(object obj, string name)
    {
        return obj.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);
    }
}
