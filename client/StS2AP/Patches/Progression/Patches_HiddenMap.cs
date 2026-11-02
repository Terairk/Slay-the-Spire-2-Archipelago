using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using StS2AP.Utils;

namespace StS2AP.Patches;

/// <summary>
/// The Hidden Map challenge. Rooms show the unknown icon until the player can move to them. The
/// boss and Ancient icons stay visible, and so does the row layout: experienced players can still
/// guess the game's fixed rows, such as the rest sites right before the boss.
/// </summary>
public static class Patches_HiddenMap
{
    /// <summary>
    /// UpdateIcon runs when a map point is created and each time its state changes, so a room
    /// shows its real icon as soon as it becomes reachable.
    /// </summary>
    [HarmonyPatch(typeof(NNormalMapPoint), nameof(NNormalMapPoint.UpdateIcon))]
    public static class HideRoomIcon
    {
        [HarmonyPostfix]
        public static void Postfix(NNormalMapPoint __instance)
        {
            if (!MapSight.IsHidden(__instance))
                return;
            string unknownIcon = NNormalMapPoint.IconName(MapPointType.Unknown);
            __instance._icon.Texture = ResourceLoader.Load<Texture2D>(
                NNormalMapPoint.IconPath(unknownIcon), null, ResourceLoader.CacheMode.Reuse);
            __instance._outline.Texture = ResourceLoader.Load<Texture2D>(
                NNormalMapPoint.OutlinePath(unknownIcon), null, ResourceLoader.CacheMode.Reuse);
        }
    }

    /// <summary>
    /// Hovering a map legend entry animates every room of that type, which would point out the
    /// hidden shops and elites. Hidden rooms answer to the unknown entry instead, like they look.
    /// </summary>
    [HarmonyPatch(typeof(NNormalMapPoint), nameof(NNormalMapPoint.OnHighlightPointType))]
    public static class HideLegendHighlight
    {
        [HarmonyPrefix]
        public static bool Prefix(NNormalMapPoint __instance, MapPointType pointType)
        {
            if (!MapSight.IsHidden(__instance))
                return true;
            if (pointType == MapPointType.Unknown)
                __instance.AnimHover();
            else
                __instance.AnimUnhover();
            return false;
        }
    }
}
