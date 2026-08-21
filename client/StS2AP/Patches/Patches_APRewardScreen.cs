using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rewards;
using StS2AP.UI;
using StS2AP.Utils;

namespace StS2AP.Patches
{
    /// <summary>
    /// Keeps MegaCrit's native reward controls while restoring the compact AP row spacing,
    /// two-line origin label, and Ancient tint used by the previous reward catalog.
    /// </summary>
    [HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton._Ready))]
    public static class StyleNativeApRewardButton
    {
        private static readonly Color AncientBackgroundTint = new(0.52f, 0.27f, 0.66f);

        [HarmonyPostfix]
        public static void Postfix(NRewardButton __instance)
        {
            if (__instance.Reward is not ApNativeRewardMenu.IApNativeReward reward)
                return;

            if (reward.HasOriginText)
            {
                __instance.CustomMinimumSize = new Vector2(
                    __instance.CustomMinimumSize.X,
                    Mathf.Max(__instance.CustomMinimumSize.Y, 74f)
                );
            }

            if (reward.UseAncientStyle
                && __instance.GetNodeOrNull<TextureRect>("%Background") is { } background)
            {
                background.SelfModulate = AncientBackgroundTint;
            }
        }
    }

    /// <summary>Uses the previous AP catalog's compact gap on the native reward container.</summary>
    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen._Ready))]
    public static class SpaceNativeApRewardRows
    {
        [HarmonyPostfix]
        public static void Postfix(NRewardsScreen __instance, RewardsSet ____rewardsSet)
        {
            if (!ArchipelagoRewardUI.IsApRewardSet(____rewardsSet))
                return;
            if (__instance.GetNodeOrNull<BoxContainer>("%RewardsContainer") is { } container)
                container.AddThemeConstantOverride("separation", 10);
        }
    }

    /// <summary>Prevents unavailable AP rows from entering the native selection lifecycle.</summary>
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
    /// An empty AP screen has no active RewardsSet left to skip. Its native proceed button is
    /// presentation-only and simply closes the overlay.
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    public static class CloseEmptyApRewardScreen
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
                return true;

            ArchipelagoRewardUI.CloseToMap();
            __result = __instance;
            return false;
        }
    }

    /// <summary>
    /// Apply the symmetric behaviour to deck requests. AP's blocker handles the equivalent hotkey
    /// while AP owns input; this catches the direct top-bar button path.
    /// </summary>
    [HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen.ShowScreen))]
    public static class OpenDeckAfterClosingAPRewards
    {
        [HarmonyPrefix]
        public static bool Prefix(ref NDeckViewScreen? __result)
        {
            if (!ArchipelagoRewardUI.IsActive)
                return true;

            ArchipelagoRewardUI.CloseToDeck();
            __result = null;
            return false;
        }
    }
}
