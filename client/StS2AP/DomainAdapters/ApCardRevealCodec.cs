using StS2AP.Domain;

namespace StS2AP.DomainAdapters;

/// <summary>
/// Small, versioned metadata accompanying each native PlayerChoiceResult mutable-card offer.
/// Bind the payload to the expected receipt/recipe before applying any persistent relic effects.
/// </summary>
internal static class ApCardRevealCodec
{
    private const int Version = 1;

    internal static List<int> Encode(ApMirroredRewardSpec spec)
    {
        if (spec.Kind != ApMirroredRewardKind.Card || !spec.CardHasBeenRevealed || spec.SerializedModels.Count == 0)
            throw new InvalidOperationException("Cannot publish an unfinished AP card reveal.");
        _ = MirroredRewardAdapter.Decode(spec, 3);
        var values = new List<int>
        {
            Version, spec.ApSlotId, spec.ReceivedItemIndex, spec.IsRareCardReward ? 1 : 0,
            spec.CardRewardActIndex ?? -1, spec.CardCanReroll ? 1 : 0, spec.AppliedEffects.Count,
        };
        foreach (ApRewardEffectSpec effect in spec.AppliedEffects)
        {
            values.Add(effect.EffectId switch
            {
                "silken_tress_used_v1" => 0,
                "silver_crucible_times_used_v1" => 1,
                _ => throw new InvalidOperationException("Unknown AP card reveal effect."),
            });
            values.Add(effect.BeforeValue);
            values.Add(effect.AfterValue);
        }
        return values;
    }

    internal static void DecodeInto(ApMirroredRewardSpec expected, IReadOnlyList<int> values)
    {
        if (expected.Kind != ApMirroredRewardKind.Card || values.Count < 7
            || values[0] != Version || values[1] != expected.ApSlotId
            || values[2] != expected.ReceivedItemIndex
            || values[3] != (expected.IsRareCardReward ? 1 : 0)
            || values[4] != (expected.CardRewardActIndex ?? -1)
            || values[5] is < 0 or > 1 || values[6] is < 0 or > 2
            || values.Count != 7 + 3 * values[6])
        {
            throw new InvalidOperationException($"AP card reveal did not match receipt {expected.GrantId}.");
        }

        var effects = new List<RewardEffect>();
        for (int offset = 7; offset < values.Count; offset += 3)
        {
            effects.Add(values[offset] switch
            {
                0 => MirroredRewardAdapter.ObserveSilkenTress(values[offset + 1], values[offset + 2], expected.GrantId.ToString()),
                1 => MirroredRewardAdapter.ObserveSilverCrucible(values[offset + 1], values[offset + 2], expected.GrantId.ToString()),
                _ => throw new InvalidOperationException("Unknown AP card reveal effect."),
            });
        }
        if (effects.Select(effect => effect.EffectId).Distinct().Count() != effects.Count)
            throw new InvalidOperationException("AP card reveal repeated a persistent effect.");

        // An existing offer may gain Egg upgrades, but refreshing it cannot spend a generation
        // effect again, forget an earlier effect, or restore a spent native reroll.
        if (expected.SerializedModels.Count > 0)
        {
            var previous = expected.AppliedEffects
                .Select(effect => (effect.EffectId, effect.BeforeValue, effect.AfterValue))
                .OrderBy(effect => effect.EffectId, StringComparer.Ordinal);
            var incoming = effects
                .Select(effect => (effect.EffectId, effect.BeforeValue, effect.AfterValue))
                .OrderBy(effect => effect.EffectId, StringComparer.Ordinal);
            if (!previous.SequenceEqual(incoming) || expected.CardCanReroll != (values[5] == 1))
                throw new InvalidOperationException($"AP card refresh changed generation state for receipt {expected.GrantId}.");
        }

        expected.CardHasBeenRevealed = true;
        expected.CardCanReroll = values[5] == 1;
        expected.AppliedEffects = MirroredRewardAdapter.EncodeEffects(effects);
    }
}
