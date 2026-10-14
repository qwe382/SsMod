using HarmonyLib;

// Global namespace on purpose (MarseyLoader reflection lookup).
public static class SubverterPatch
{
    public static string Name = "SsMod";
    public static string Description = "Minimal UI panel mod";
    public static Harmony Harm = new("local.ssmod");
}
