using System.Reflection;
using HarmonyLib;

namespace SsMod;

public static class ModEntry
{
    public const string HarmonyId = "local.ssmod";

    public static void Entry()
    {
        try
        {
            if (!TryWaitForAssembly("Content.Client"))
            {
                Console.Error.WriteLine("[SsMod] Content.Client not found, mod not loaded");
                return;
            }

            // Patch while the assembly is still visible (see MarseyEntry for why order matters).
            //
            // ВАЖНО: PatchAll накладывает все патчи разом и падает на первом же
            // проблемном — тогда не работает вообще ничего. Поэтому накладываем
            // каждый класс отдельно и переживаем сбой одного из них.
            var harmony = new Harmony(HarmonyId);
            var assembly = Assembly.GetExecutingAssembly();
            var applied = 0;
            var failed = 0;

            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<HarmonyPatch>() == null)
                    continue;

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (Exception e)
                {
                    failed++;
                    Console.Error.WriteLine(
                        $"[SsMod] patch failed: {type.FullName}: {e.InnerException?.Message ?? e.Message}");
                }
            }

            Console.WriteLine($"[SsMod] Harmony patched: {applied} ok, {failed} failed");

            // Safety net: if the marsey loader routed here directly (not via MarseyEntry),
            // hide the assembly now that patching is done. Idempotent.
            Sedition.hideDelegate?.Invoke(typeof(ModEntry).Assembly);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] Entry failed: {e}");
        }
    }

    private static bool TryWaitForAssembly(string assemblyName)
    {
        for (var i = 0; i < 50; i++)
        {
            if (FindAssembly(assemblyName) != null)
                return true;

            Thread.Sleep(200);
        }

        return false;
    }

    private static Assembly? FindAssembly(string assemblyName)
    {
        // NOTE: this runs before hiding, so plain GetAssemblies() is fine here.
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.FullName?.Contains(assemblyName) == true);
    }
}
