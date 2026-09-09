using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;
using StS2AP.Data;
using StS2AP.Extensions;

namespace StS2AP.Utils;

internal static class BonusRewardUtility
{
    public static bool ConvertToGold(long itemId) =>
        itemId == (long)ItemTable.APItem.BonusWaxRelic
        && MultiplayerSupport.IsMultiplayerScope
        && !MultiplayerSupport.ShouldRunReplicatedConstruction(MultiplayerFeature.BonusItems);

    public static RelicModel? GetOrAssign(IndexedItemInfo receipt, Player player)
    {
        var progress = ArchipelagoClient.Progress;
        if (progress.BonusRelicAssignments.TryGetValue(receipt.Index, out string? saved))
        {
            var serialized = JsonSerializer.Deserialize<SerializableRelic>(saved, SerializationUtility.CombinedOptions)
                ?? throw new InvalidOperationException($"Invalid bonus assignment at receipt {receipt.Index}.");
            return RelicModel.FromSerializable(serialized);
        }

        if (!ApPlayerContextResolver.TryGetRewardSettings(player, out var settings))
            return null;
        int ordinal = progress.AllReceivedItems.Count(other =>
            other.Index < receipt.Index && other.Item.ItemId == receipt.Item.ItemId);
        var definitions = settings.BonusItemsFor(BonusItemDefinition.WaxRelicCategory);
        if (ordinal >= definitions.Count)
            return null;

        var definition = definitions[ordinal];
        RelicModel? selected;
        if (definition.HasExplicitValue)
        {
            selected = BonusRelicResolver.ResolveValue(definition.Value!, rejectPickupEffectRelics: true);
            if (selected != null && !selected.IsAllowed(player.RunState))
                selected = null;
        }
        else
        {
            selected = BonusRelicResolver.BuildPoolCandidates(definition.Pools, rejectPickupEffectRelics: true)
                .Where(relic => relic.IsAllowed(player.RunState))
                .OrderBy(relic => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"sts2ap-bonus-relic-v1|{player.RunState.Rng.StringSeed}|WAX_RELIC:{ordinal}|{relic.Id}"))), StringComparer.Ordinal)
                .FirstOrDefault();
        }
        if (selected == null)
            return null;

        RelicModel mutable = selected.ToMutable();
        mutable.IsWax = true;
        progress.BonusRelicAssignments[receipt.Index] =
            JsonSerializer.Serialize(mutable.ToSerializable(), SerializationUtility.CombinedOptions);
        LogUtility.Info($"Assigned bonus wax relic {mutable.Id}: player={player.NetId}, receipt={receipt.Index}, ordinal={ordinal}");
        return mutable;
    }
}
