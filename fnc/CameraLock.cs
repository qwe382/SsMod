using System.Numerics;
using Content.Client.Movement.Systems;
using HarmonyLib;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;

namespace SsMod.Fnc;

// Блокировка камеры на персонаже: любой сдвиг камеры (курсор, отдача оружия,
// прицеливание) обнуляется, камера стоит ровно на персонаже.
public static class CameraLock
{
    internal static bool Enabled { get; private set; }

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "Блокировка камеры",
            Pressed = Enabled,
            ToolTip = "Камера держится на персонаже и не смещается",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;
        Console.WriteLine($"[SsMod] camera lock = {on}");
    }
}

// Главный патч: SharedEyeSystem.SetOffset — единственная точка, где смещение
// глаза вообще выставляется (и отдача, и курсор, и прицел идут через неё).
// Пока блокировка включена — принудительно пишем ноль.
[HarmonyPatch(typeof(SharedEyeSystem), nameof(SharedEyeSystem.SetOffset))]
internal static class CameraLockSetOffsetPatch
{
    private static bool _logged;

    private static void Prefix(EntityUid uid, ref Vector2 value)
    {
        if (!CameraLock.Enabled)
        {
            _logged = false;
            return;
        }

        value = Vector2.Zero;

        if (!_logged)
        {
            _logged = true;
            Console.WriteLine("[SsMod] camera lock active (offset forced to 0)");
        }
    }
}

// Дополнительно: расчёт сдвига к курсору для оружия в руках. Возвращаем null,
// чтобы сдвиг вообще не считался.
[HarmonyPatch(typeof(EyeCursorOffsetSystem), nameof(EyeCursorOffsetSystem.OffsetAfterMouse))]
internal static class CameraLockCursorPatch
{
    private static bool Prefix(ref Vector2? __result)
    {
        if (!CameraLock.Enabled)
            return true;

        __result = null;
        return false;
    }
}
