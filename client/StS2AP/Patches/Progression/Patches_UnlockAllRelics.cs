using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Unlocks;
using StS2AP.Utils;

namespace StS2AP.Patches;

/// <summary>Removes unlock filtering from the playable and shared relic pools.</summary>
[HarmonyPatch]
public static class Patches_UnlockAllRelics
{
    // The base method returns every relic; the game's shared and per-character pools override
    // it to apply epoch locks. Patching each game override, rather than naming the pools, also
    // covers characters added in a newer game version. Mod pools are left alone. If a game update
    // removes every override, Harmony throws and the mod fails to load instead of leaving locks.
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods() =>
        GameOverrides.Find(typeof(RelicPoolModel), nameof(RelicPoolModel.GetUnlockedRelics), typeof(UnlockState));

    [HarmonyPostfix]
    private static void Postfix(RelicPoolModel __instance, ref IEnumerable<RelicModel> __result) =>
        __result = __instance.AllRelics;
}
