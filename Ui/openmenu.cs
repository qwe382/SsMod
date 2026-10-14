using System.Reflection;
using Content.Client.UserInterface.Systems.MenuBar;
using HarmonyLib;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace SsMod.Ui;

[HarmonyPatch(typeof(GameTopMenuBarUIController), nameof(GameTopMenuBarUIController.LoadButtons))]
public static class OpenMenuPatch
{
    private static Button? _btn;
    private static SsModWindow? _window;

    public static void Postfix(GameTopMenuBarUIController __instance)
    {
        try
        {
            if (_btn != null && _btn.Parent != null)
                return;

            var bar = GetProp(__instance, "GameTopMenuBar") as Control;
            if (bar == null)
            {
                Console.WriteLine("[SsMod] GameTopMenuBar not found");
                return;
            }

            var container = GetProp(bar, "MenuButtons") as Control;
            if (container == null)
            {
                Console.WriteLine("[SsMod] MenuButtons not found");
                return;
            }

            _window ??= new SsModWindow();

            var btn = new Button { Text = "SsMod" };
            btn.OnPressed += _ =>
            {
                if (_window.IsOpen)
                    _window.Close();
                else
                    _window.OpenCentered();
            };

            container.AddChild(btn);
            _btn = btn;
            Console.WriteLine("[SsMod] button added");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] OpenMenu failed: {e}");
        }
    }

    private static object? GetProp(object obj, string name)
    {
        return obj.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);
    }
}
