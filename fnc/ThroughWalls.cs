using System;
using System.Reflection;
using HarmonyLib;
using Robust.Client.UserInterface.Controls;

namespace SsMod.Fnc;

// Твик: ПКМ-меню и осмотр работают сквозь стены.
//
// Клиент отсекает сущности без прямой видимости в ExamineSystem.CanExamine —
// на него завязаны сбор списка для ПКМ-меню, чистка открытого меню и
// авто-закрытие тултипа осмотра. Пока твик включён, CanExamine всегда true:
// предметы за стенами остаются в меню и осматриваются.
public static class ThroughWalls
{
    internal static bool Enabled { get; private set; }

    public static CheckBox BuildCheckBox()
    {
        return new CheckBox
        {
            Text = "ПКМ и осмотр за стенами",
            Pressed = Enabled,
            ToolTip = "Предметы за стенами видны в ПКМ-меню и осматриваются",
        };
    }

    public static void Toggle(bool on)
    {
        Enabled = on;
        Console.WriteLine($"[SsMod] через стены = {on}");
    }

    // CanExamine перегружен (EntityUid и MapCoordinates), в части версий
    // клиентский тип его переопределяет, в части — наследует из shared.
    // Патчим все перегрузки по имени, чтобы не завязываться на версию.
    public static void Patch(Harmony harmony)
    {
        var examine = Compat.FindType("Content.Client.Examine.ExamineSystem");
        if (examine is null)
            return;

        var prefix = new HarmonyMethod(typeof(ThroughWalls)
            .GetMethod(nameof(ExaminePrefix), BindingFlags.NonPublic | BindingFlags.Static)!);

        foreach (var m in examine.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "CanExamine")
                continue;

            try
            {
                harmony.Patch(m, prefix: prefix);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SsMod] через стены: {m}: {e.Message}");
            }
        }
    }

    private static bool ExaminePrefix(ref bool __result)
    {
        if (!Enabled)
            return true;

        __result = true;
        return false;
    }
}
