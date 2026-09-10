using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Unlocks;
using StS2AP.Utils;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace StS2AP.Patches;

/// <summary>
/// AP character locks control run selection, not Kaleidoscope's other-character choices.
/// Replace only this relic's pool lookup and retain its native generation and reward lifecycle.
/// </summary>
[HarmonyPatch]
public static class Patches_Kaleidoscope
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type? stateMachine = AccessTools.Method(typeof(Kaleidoscope), nameof(Kaleidoscope.AfterObtained))
            ?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        MethodInfo? moveNext = stateMachine == null ? null : AccessTools.Method(stateMachine, "MoveNext");
        if (moveNext == null)
        {
            LogUtility.Warn("Could not locate Kaleidoscope reward generation; leaving native behavior unchanged");
            yield break;
        }
        yield return moveNext;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo getter = AccessTools.PropertyGetter(typeof(UnlockState), nameof(UnlockState.CharacterCardPools));
        List<CodeInstruction> lookups = code.Where(instruction => instruction.Calls(getter)).ToList();
        if (lookups.Count != 1)
        {
            LogUtility.Warn($"Expected one Kaleidoscope card-pool lookup, found {lookups.Count}; leaving native behavior unchanged");
            return code;
        }

        // Both public and beta use this getter in AfterObtained's async state machine.
        // Mutate the instruction in place to preserve its branch labels and exception blocks.
        lookups[0].opcode = OpCodes.Call;
        lookups[0].operand = AccessTools.Method(typeof(Patches_Kaleidoscope), nameof(GetCardPools));
        return code;
    }

    private static IEnumerable<CardPoolModel> GetCardPools(UnlockState unlockState)
    {
        if (ArchipelagoClient.Settings == null
            || GameUtility.CurrentPlayer is not { } player
            || !ReferenceEquals(player.UnlockState, unlockState))
        {
            return unlockState.CharacterCardPools;
        }

        CardPoolModel[] pools = ModelDb.AllCharacterCardPools.ToArray();
        LogUtility.Info($"Kaleidoscope: using {pools.Length} character card pools independently of AP character locks");
        return pools;
    }
}
