using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Utils;

namespace StS2AP.Patches;

/// <summary>
/// Enforces <see cref="RelicRules"/>' run rules wherever the game or the mod asks whether a relic
/// may appear.
/// </summary>
[HarmonyPatch]
public static class Patches_RelicRules
{
    // From 0.113 IsAllowed is not virtual (relics override IsAllowedInternal), so the base method
    // covers every relic. Earlier versions let a relic override IsAllowed without calling the base
    // method, as White Star and Juzu Bracelet do, so a rule relic's own override is patched too.
    // The targets come from the rule table, so a new rule needs no new patch.
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] parameters = [typeof(IRunState)];
        yield return AccessTools.DeclaredMethod(typeof(RelicModel), nameof(RelicModel.IsAllowed), parameters);

        foreach (Type relicType in RelicRules.RunRuleRelicTypes)
        {
            MethodInfo? ownOverride = AccessTools.DeclaredMethod(relicType, nameof(RelicModel.IsAllowed), parameters);
            if (ownOverride != null)
                yield return ownOverride;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(RelicModel __instance, IRunState runState, ref bool __result)
    {
        if (__result && RelicRules.IsKeptOutOfRun(__instance, runState))
            __result = false;
    }
}
