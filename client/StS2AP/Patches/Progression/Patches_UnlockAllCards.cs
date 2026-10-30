using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using StS2AP.Utils;

namespace StS2AP.Patches;

/// <summary>Removes epoch locks from the game's card pools.</summary>
[HarmonyPatch(typeof(CardPoolModel), nameof(CardPoolModel.GetUnlockedCards))]
public static class Patches_UnlockAllCards
{
    [HarmonyPostfix]
    private static void Postfix(
        CardPoolModel __instance,
        CardMultiplayerConstraint multiplayerConstraint,
        ref IEnumerable<CardModel> __result)
    {
        // Only the character and colorless pools filter through epochs; every other game pool
        // already returns all its cards, so unlocking every game pool changes nothing for them
        // and covers characters added in a newer game version. Mod pools keep their own rules.
        if (!VanillaCharacters.IsVanilla(__instance))
            return;

        __result = __instance.AllCards.Where(card => multiplayerConstraint switch
        {
            CardMultiplayerConstraint.MultiplayerOnly =>
                card.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly,
            CardMultiplayerConstraint.SingleplayerOnly =>
                card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly,
            _ => true,
        }).ToList();
    }
}
