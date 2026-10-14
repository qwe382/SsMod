using System;
using System.Reflection;

// Global namespace on purpose (MarseyLoader reflection lookup, same as SubverterPatch).
// Marsey's Sedition.InitSedition() finds a type named exactly "Sedition" in this assembly
// and binds its two public static delegate fields to loader bridge methods:
//   hideDelegate         -> Hidesey.HidePatch(asm)                     (hides the whole assembly)
//   hideManifestDelegate -> Hidesey.Apply(systems, comps, ioc, cvars, commands, stackNames)
public static class Sedition
{
    // Assigned by Marsey at load time (Sedition.InitSedition).
    public static Action<Assembly>? hideDelegate;

    public static Action<string[]?, string[]?, string[]?, string[]?, string[]?, string[]?>? hideManifestDelegate;
}
