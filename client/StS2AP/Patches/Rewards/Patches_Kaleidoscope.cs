using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Utils;

namespace StS2AP.Patches;

/// <summary>Uses the same owner-scoped pools as Prismatic Gem and Colourful Philosophers.</summary>
[HarmonyPatch(typeof(Kaleidoscope), nameof(Kaleidoscope.AfterObtained))]
internal static class Patches_Kaleidoscope
{
    [HarmonyPrefix]
    private static bool Prefix(Kaleidoscope __instance, ref Task __result)
    {
        if (!CrossCharacterCardPoolUtility.TryGetPools(__instance.Owner, out var pools))
            return true;
        __result = OfferRewards(__instance, pools);
        return false;
    }

    private static async Task OfferRewards(Kaleidoscope relic, IReadOnlyList<CardPoolModel> pools)
    {
        Player player = relic.Owner;
        var rewards = new List<Reward>();
#if STS2_0_107_1
        var rerollOptions = CardCreationOptions.ForNonCombatWithDefaultOdds(Array.Empty<CardModel>());
#else
        var rerollOptions = CardCreationOptions.ForNonCombatWithDefaultOdds(Array.Empty<CardPoolModel>());
#endif
        for (int i = 0; i < relic.DynamicVars.Cards.IntValue; i++)
        {
            var cards = new List<CardModel>();
            foreach (CardPoolModel pool in pools.Where(pool => pool.Id != player.Character.CardPool.Id)
                         .ToList().StableShuffle(player.RunState.Rng.Niche).Take(3))
            {
                var options = new CardCreationOptions([pool], CardCreationSource.Other,
                    CardRarityOddsType.RegularEncounter).WithFlags(CardCreationFlags.NoCardPoolModifications);
                cards.Add(CardFactory.CreateForReward(player, 1, options).First().Card);
            }
            rewards.Add(new CardReward(cards, CardCreationSource.Other, player, rerollOptions));
        }
        await RewardsCmd.OfferCustom(player, rewards);
    }
}
