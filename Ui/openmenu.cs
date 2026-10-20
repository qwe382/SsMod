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

            _window = EnsureWindow();

            var btn = new Button { Text = "SsMod" };
            btn.OnPressed += _ => Toggle();

            container.AddChild(btn);
            _btn = btn;
            Console.WriteLine("[SsMod] button added");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] OpenMenu failed: {e}");
        }
    }

    // Окно живёт в WindowRoot и переживает раунды, но после сброса UI (или
    // исключения при сборке фрейма) может оказаться disposed. Тогда кнопка
    // молча перестаёт открывать окно — поэтому пересоздаём его по требованию.
    private static SsModWindow EnsureWindow()
    {
        if (_window is { Disposed: false })
            return _window;

        _window = null;
        _window = new SsModWindow();
        return _window;
    }

    private static void Toggle()
    {
        try
        {
            var window = EnsureWindow();

            if (window.IsOpen)
                window.Close();
            else
                window.OpenCentered();
        }
        catch (Exception e)
        {
            // Сломанное окно больше не переиспользуем: следующее нажатие
            // соберёт новое.
            Console.Error.WriteLine($"[SsMod] окно: {e}");
            _window = null;
        }
    }

    private static object? GetProp(object obj, string name)
    {
        return obj.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);
    }
}
