using System.Numerics;
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

// Пока блокировка включена — принудительно пишем ноль.
//
// Отдельный патч EyeCursorOffsetSystem.OffsetAfterMouse убран: его Harmony-копия
// ломала JIT на fish 340 (BadImageFormatException каждый тик, 600+ ошибок),
// а весь сдвиг всё равно проходит через SetOffset, который уже перекрыт выше.
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
