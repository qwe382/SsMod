// MarseyEntry.Entry() is invoked by Marsey's Doorbreak AFTER Sedition.InitSedition()
// has bound the delegates.
//
// ORDER MATTERS: Harmony.PatchAll(assembly) -> AccessTools.GetTypesFromAssembly(assembly)
// -> assembly.GetTypes(), and Marsey postfixes Assembly.GetTypes with its Hidesey filter.
// If we hide the assembly BEFORE patching, our own [HarmonyPatch] types get filtered out
// and the mod silently stops working. So: patch first, hide second.
public static class MarseyEntry
{
    public static void Entry()
    {
        // 1) Install Harmony patches first (types must still be visible).
        SsMod.ModEntry.Entry();

        // 2) Now hide this whole assembly from game-side reflection/probes:
        //    AppDomain.GetAssemblies, Assembly.GetTypes/GetReferencedAssemblies,
        //    AssemblyLoadContext.Assemblies/All, ReflectionManager.*,
        //    BaseModLoader.LoadedModules, Type.GetType, Assembly.Location, Environment.StackTrace.
        Sedition.hideDelegate?.Invoke(typeof(MarseyEntry).Assembly);
    }
}
