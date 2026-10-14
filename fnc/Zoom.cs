using System.Numerics;
using System.Reflection;
using Content.Client.Entry;
using HarmonyLib;
using Robust.Client.Input;
using Robust.Shared.IoC;

namespace SsMod.Fnc;

[HarmonyPatch(typeof(EntryPoint), nameof(EntryPoint.PostInit))]
public static class ZoomPatch
{
    private static bool _subscribed;
    private static IInputManager? _input;

    public static void Postfix()
    {
        try
        {
            if (_subscribed)
                return;

            var input = IoCManager.Resolve<IInputManager>();
            _input = input;
            var clyde = input.GetType()
                .GetField("_clyde", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(input);
            if (clyde == null)
            {
                Console.Error.WriteLine("[SsMod] zoom: _clyde not found");
                return;
            }

            var iface = Type.GetType("Robust.Client.Graphics.IClydeInternal, Robust.Client");
            var evt = iface?.GetEvent("MouseWheel")
                      ?? clyde.GetType().GetEvent("MouseWheel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (evt == null)
            {
                Console.Error.WriteLine("[SsMod] zoom: MouseWheel event not found");
                return;
            }

            var handler = Delegate.CreateDelegate(
                evt.EventHandlerType!,
                typeof(ZoomPatch).GetMethod(nameof(OnMouseWheel), BindingFlags.NonPublic | BindingFlags.Static)!);
            evt.AddEventHandler(clyde, handler);

            _subscribed = true;
            Console.WriteLine("[SsMod] zoom hooked");

            // Зум камеры теперь живёт в моде: взводим блокировку.
            ViewRadius.Lock();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] zoom hook failed: {e}");
        }
    }

    private static void OnMouseWheel(MouseWheelEventArgs e)
    {
        try
        {
            var ctrl = _input != null &&
                       (_input.IsKeyDown(Keyboard.Key.Control) ||
                        _input.IsKeyDown(Keyboard.Key.LSystem) ||
                        _input.IsKeyDown(Keyboard.Key.RSystem));
            if (!ctrl)
                return;

            var factor = e.Delta.Y > 0 ? 1f / 1.1f : 1.1f;
            // Пишем только через ViewRadius — это единственная разрешённая точка записи.
            ViewRadius.Step(factor);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SsMod] zoom failed: {ex}");
        }
    }
}
