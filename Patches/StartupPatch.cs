using Content.Client.Entry;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using SsMod.Fnc;
using SsMod.Ui;

namespace SsMod.Patches;

[HarmonyPatch(typeof(EntryPoint), nameof(EntryPoint.PostInit))]
public static class StartupPatch
{
    private static bool _initialized;
    private static ISawmill? _sawmill;

    public static void Postfix()
    {
        try
        {
            if (_initialized)
                return;

            _sawmill ??= IoCManager.Resolve<ILogManager>().GetSawmill("ssmod");
            _sawmill.Info("PostInit: SsMod активен");

            // Всплывашки роутера боя (ближний/дальний) у курсора. Роутер работает
            // всегда, поэтому оверлей ставим сразу, а не галочкой.
            var overlays = IoCManager.Resolve<IOverlayManager>();
            if (!overlays.HasOverlay<RouterPopupOverlay>())
                overlays.AddOverlay(new RouterPopupOverlay());

            // «ХП при уроне» — всегда включено (галки в окне больше нет).
            DamagePopup.Toggle(true);

            // Визуалы gun-bot'а (синие дуги) — только если аимбот отмечен в конфиге.
            if (GunBot.Aim)
                GunBot.ToggleOverlay(true);

           // SsModUi.Create();

            _initialized = true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] Postfix failed: {e}");
        }
    }
}
