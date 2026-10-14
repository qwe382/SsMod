using Content.Client.Entry;
using HarmonyLib;
using Robust.Shared.IoC;
using Robust.Shared.Log;
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

           // SsModUi.Create();

            _initialized = true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] Postfix failed: {e}");
        }
    }
}
