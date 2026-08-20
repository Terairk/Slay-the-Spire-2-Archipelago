using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rewards;
using StS2AP.UI;

namespace StS2AP.Patches
{
    /// <summary>
    /// Rejects an AP-owned native row before MegaCrit emits RewardSelectedMessage. This keeps
    /// multiplayer combat and other expected claim gates as true non-selections.
    /// </summary>
    [HarmonyPatch(typeof(NRewardButton), "OnRelease")]
    public static class GateNativeApRewardSelection
    {
        [HarmonyPrefix]
        public static bool Prefix(NRewardButton __instance) =>
            ArchipelagoRewardUI.CanSelectNativeReward(__instance);
    }

    /// <summary>
    /// Vanilla removes an empty nonterminal reward screen immediately. AP intentionally exposes
    /// an empty persistent inventory, so retain only AP-owned screens that started empty.
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "UpdateScreenState")]
    public static class KeepEmptyApRewardScreenOpen
    {
        [HarmonyPrefix]
        public static bool Prefix(RewardsSet ____rewardsSet) =>
            !ArchipelagoRewardUI.ShouldKeepEmptyScreenOpen(____rewardsSet);
    }

    /// <summary>
    /// Empty and vanilla-guest AP screens have no live synchronized set to skip. Their native
    /// proceed button is presentation-only and simply closes the overlay.
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    public static class CloseUnsynchronizedApRewardScreen
    {
        [HarmonyPrefix]
        public static bool Prefix(NRewardsScreen __instance, RewardsSet ____rewardsSet)
        {
            if (!ArchipelagoRewardUI.ShouldHandleProceedWithoutNativeSkip(____rewardsSet))
                return true;
            ArchipelagoRewardUI.CloseWithoutNativeSkip(__instance);
            return false;
        }
    }

    /// <summary>Releases AP lifecycle state whenever the native overlay finishes closing.</summary>
    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.AfterOverlayClosed))]
    public static class FinishNativeApRewardScreen
    {
        [HarmonyPostfix]
        public static void Postfix(NRewardsScreen __instance, RewardsSet ____rewardsSet) =>
            ArchipelagoRewardUI.NotifyNativeScreenClosed(__instance, ____rewardsSet);
    }

    /// <summary>
    /// AP registers its own map hotkey while the native reward screen is active. A direct top-bar
    /// click calls Open with isOpenedFromTopBar=true, so defer that path until AP has closed.
    /// </summary>
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
    public static class OpenMapAfterClosingAPRewards
    {
        [HarmonyPrefix]
        public static bool Prefix(
            NMapScreen __instance,
            bool isOpenedFromTopBar,
            ref NMapScreen __result)
        {
            if (!isOpenedFromTopBar || !ArchipelagoRewardUI.IsActive)
            {
                return true;
            }

            ArchipelagoRewardUI.CloseToMap();
            __result = __instance;
            return false;
        }
    }

    /// <summary>
    /// Apply the symmetric behaviour to deck requests. AP's blocker handles the
    /// equivalent hotkey while AP owns input; this catches the direct button path.
    /// </summary>
    [HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen.ShowScreen))]
    public static class OpenDeckAfterClosingAPRewards
    {
        [HarmonyPrefix]
        public static bool Prefix(ref NDeckViewScreen? __result)
        {
            if (!ArchipelagoRewardUI.IsActive)
            {
                return true;
            }

            ArchipelagoRewardUI.CloseToDeck();
            __result = null;
            return false;
        }
    }
}
