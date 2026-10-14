using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using SsMod.Ui;

namespace SsMod.Debug;

// Переключатель "Block debug" живёт теперь в фрейме 1 окна SsMod
// (Ui/SsModWindow.cs, BuildFrame1). Здесь остаётся только сам оверлей.
//
// Раньше чекбокс вешался патчем конструктора прямо в окно, минуя фреймы,
// поэтому он всегда оказывался не в том месте, куда его просили перенести.

public static class SsModWindowCheckbox
{
    public static CheckBox BuildBlockDebugCheckBox()
    {
        return new CheckBox
        {
            Text = "Block debug",
            Pressed = IoCManager.Resolve<IOverlayManager>().HasOverlay<PassabilityOverlay>(),
            ToolTip = "Подсветка проходимости: зелёный — пройти можно, красный — нельзя",
            };
    }
}
