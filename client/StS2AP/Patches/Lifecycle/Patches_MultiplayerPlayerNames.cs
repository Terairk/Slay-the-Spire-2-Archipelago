using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using StS2AP.Multiplayer;

namespace StS2AP.Patches;

[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
public static class Patches_MultiplayerPlayerNames
{
    [HarmonyPostfix]
    public static void AddApPlayerNumber(NMultiplayerPlayerState __instance)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || __instance.Player.RunState is not RunState runState
            || !ApRunData.TryGetPlayerState(runState,
                __instance.Player.NetId, out var state)
            || state.Participation != ApParticipationKind.OwnApSlot
            || state.SlotSettings?.PlayerNumber is not (>= 1 and <= 4))
        {
            return;
        }

        MegaLabel? label = __instance.GetNodeOrNull<MegaLabel>("%NameplateLabel");
        if (label == null)
            return;

        string name = PlatformUtil.GetPlayerNameRaw(
            RunManager.Instance.NetService.Platform, __instance.Player.NetId);
        label.SetTextAutoSize($"{name} (P{state.SlotSettings.PlayerNumber})");
    }
}
