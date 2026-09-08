using System.Text.Json;
using StS2AP.DomainAdapters;
using StS2AP.Persistence;
using Xunit;

namespace StS2AP.RegressionTests;

public sealed class ApCardRevealTests
{
    private static ApMirroredRewardSpec Recipe() => new()
    {
        ApSlotId = 7, ReceivedItemIndex = 42, OwnerNetId = 123,
        Kind = ApMirroredRewardKind.Card, CardRewardActIndex = 1,
        MaterializationStrategyId = "ap_rng_owner_final_v1",
    };

    private static ApMirroredRewardSpec Final() => new()
    {
        ApSlotId = 7, ReceivedItemIndex = 42, OwnerNetId = 123,
        Kind = ApMirroredRewardKind.Card, CardRewardActIndex = 1,
        MaterializationStrategyId = "ap_rng_owner_final_v1", CardHasBeenRevealed = true,
        SerializedModels = ["{\"id\":\"CARD.A\",\"upgraded\":true}"],
        AppliedEffects = [new() { EffectId = "silver_crucible_times_used_v1", BeforeValue = 1, AfterValue = 2 }],
    };

    [Fact]
    public void TwelveUnopenedRewardsRoundTripAsRecipesWithoutAssignmentsOrEffects()
    {
        var menu = new ApRewardMenuSpec();
        for (int index = 0; index < 12; index++)
        {
            var spec = Recipe();
            spec.ReceivedItemIndex = index;
            menu.Rewards.Add(spec);
        }
        var roundTrip = JsonSerializer.Deserialize<ApRewardMenuSpec>(JsonSerializer.Serialize(menu))!;
        foreach (var spec in roundTrip.Rewards)
        {
            var reward = MirroredRewardAdapter.Decode(spec, 3);
            Assert.True(reward.Match(card => card.IsDeferred, _ => false, _ => false, _ => false, _ => false));
            Assert.Empty(reward.Effects);
        }
        Assert.Throws<InvalidOperationException>(() =>
            MirroredRewardAdapter.DecodeSavedCardAssignment(42, new ApCardAssignmentState(), 123));
    }

    [Fact]
    public void RevealTransfersFinalCardsAndCounterTransitionWhichReopenDoesNotReplay()
    {
        var owner = Final();
        var replica = Recipe();
        ApCardRevealCodec.DecodeInto(replica, ApCardRevealCodec.Encode(owner));
        replica.SerializedModels = owner.SerializedModels.ToList();
        var decoded = MirroredRewardAdapter.Decode(replica, 3);
        Assert.True(replica.CardHasBeenRevealed);
        Assert.True(MirroredRewardAdapter.NeedsApplication(decoded.Effects[0], 1, "7:42"));
        Assert.False(MirroredRewardAdapter.NeedsApplication(decoded.Effects[0], 2, "7:42"));
        var saved = new ApCardAssignmentState
        {
            SerializedCards = replica.SerializedModels, IsRare = false, RewardActIndex = 1,
            HasBeenRevealed = true, MaterializationStrategyId = replica.MaterializationStrategyId,
            AppliedEffects = replica.AppliedEffects,
        };
        saved = JsonSerializer.Deserialize<ApCardAssignmentState>(JsonSerializer.Serialize(saved))!;
        var restored = MirroredRewardAdapter.DecodeSavedCardAssignment(42, saved, 123).Card;
        Assert.Equal(owner.SerializedModels, restored.Models);
        Assert.False(restored.IsDeferred);
        Assert.False(MirroredRewardAdapter.NeedsApplication(restored.Configuration.Effects[0], 2, "7:42"));
    }

    [Theory]
    [InlineData(0, 99)] // protocol
    [InlineData(1, 8)] // slot
    [InlineData(2, 43)] // receipt
    [InlineData(3, 1)] // rarity
    [InlineData(4, 2)] // assigned act
    [InlineData(5, 2)] // invalid boolean
    [InlineData(6, 3)] // excessive effects
    [InlineData(7, 99)] // unknown effect
    [InlineData(9, 4)] // skipped Crucible transition
    public void MismatchedOrMalformedRevealIsRejectedBeforeEffects(int offset, int replacement)
    {
        var metadata = ApCardRevealCodec.Encode(Final());
        metadata[offset] = replacement;
        var target = Recipe();
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.DecodeInto(target, metadata));
        Assert.Empty(target.AppliedEffects);
        Assert.False(target.CardHasBeenRevealed);
    }

    [Fact]
    public void TruncatedAndRepeatedEffectsAreRejected()
    {
        var metadata = ApCardRevealCodec.Encode(Final());
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.DecodeInto(Recipe(), metadata.Take(9).ToList()));
        metadata.AddRange(metadata.Skip(7).ToArray());
        metadata[6] = 2;
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.DecodeInto(Recipe(), metadata));
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.Encode(Recipe()));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void PreviousMenuSchemaIsRejectedBeforeStartingNativeChoices(int version)
    {
        var spec = Recipe();
        spec.SchemaVersion = version;
        Assert.Throws<InvalidOperationException>(() => MirroredRewardAdapter.Decode(spec, 3));
    }

    [Fact]
    public void CrucibleTransitionsFollowRevealOrderRatherThanReceiptOrder()
    {
        var first = Final();
        first.ReceivedItemIndex = 99;
        first.AppliedEffects[0].BeforeValue = 0;
        first.AppliedEffects[0].AfterValue = 1;
        var second = Final();
        second.ReceivedItemIndex = 2;
        int counter = 0;
        foreach (var entry in MirroredRewardAdapter.OrderedEffects([
                     MirroredRewardAdapter.Decode(second, 3), MirroredRewardAdapter.Decode(first, 3)]))
        {
            Assert.True(MirroredRewardAdapter.NeedsApplication(entry.Effect, counter, entry.Origin.ReceiptIdentity));
            counter = entry.Effect.AfterValue;
        }
        Assert.Equal(2, counter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshedOfferPreservesEffectsAndRerollStateAcrossWireSaveAndDelta(bool canReroll)
    {
        var before = Final();
        before.CardCanReroll = canReroll;
        // Opaque MegaCrit model payloads: these tests exercise transport/persistence, not the
        // native Egg hook. Keep identity, order, enchantments, and unrelated card props intact.
        before.SerializedModels =
        [
            """{"id":"CARD.SKILL","current_upgrade_level":0,"enchantment":{"id":"GLAM"}}""",
            """{"id":"CARD.ATTACK","current_upgrade_level":0,"props":{"custom":7}}""",
        ];
        before.AppliedEffects.Add(new()
        {
            EffectId = "silken_tress_used_v1", BeforeValue = 0, AfterValue = 1,
        });
        var owner = RoundTrip(before);
        owner.SerializedModels[0] = owner.SerializedModels[0].Replace("\"current_upgrade_level\":0", "\"current_upgrade_level\":1");
        var replica = RoundTrip(before);
        ApCardRevealCodec.DecodeInto(replica, ApCardRevealCodec.Encode(owner));
        replica.SerializedModels = owner.SerializedModels.ToList();

        var oldProgress = new ApRunProgressState { CardAssignments = { [42] = Assignment(before) } };
        var newProgress = new ApRunProgressState { CardAssignments = { [42] = Assignment(replica) } };
        ApProgressDelta delta = RoundTrip(ApProgressDelta.Between(oldProgress, newProgress));
        Assert.Single(delta.CardAssignmentUpserts);
        Assert.Empty(delta.UsedItemsAdded); // Refresh is not consumption.
        ApRunProgressState restored = RoundTrip(delta.ApplyToCopy(oldProgress));
        var decoded = MirroredRewardAdapter.DecodeSavedCardAssignment(42, restored.CardAssignments[42], 123).Card;
        Assert.Equal(owner.SerializedModels, decoded.Models);
        Assert.True(decoded.Configuration.HasBeenRevealed);
        Assert.Equal(canReroll, decoded.Configuration.CanReroll);
        Assert.Equal(2, decoded.Configuration.Effects.Count);
        foreach (var effect in decoded.Configuration.Effects)
            Assert.False(MirroredRewardAdapter.NeedsApplication(effect, effect.AfterValue, "7:42"));
        Assert.False(ApProgressDelta.Between(restored, newProgress).HasChanges);
        Assert.Contains("\"current_upgrade_level\":0", oldProgress.CardAssignments[42].SerializedCards[0]);

        // Repeated synchronization carries the same offer and cannot replay a counter transition.
        for (int i = 0; i < 3; i++)
            ApCardRevealCodec.DecodeInto(replica, ApCardRevealCodec.Encode(owner));
        Assert.Equal(JsonSerializer.Serialize(owner.AppliedEffects), JsonSerializer.Serialize(replica.AppliedEffects));
    }

    [Theory]
    [InlineData("spend")]
    [InlineData("forget")]
    [InlineData("add")]
    [InlineData("reroll")]
    public void RefreshRejectsChangedGenerationStateBeforeMutatingAssignment(string change)
    {
        var replica = Final();
        var owner = RoundTrip(replica);
        switch (change)
        {
            case "spend":
                owner.AppliedEffects[0].BeforeValue++;
                owner.AppliedEffects[0].AfterValue++;
                break;
            case "forget": owner.AppliedEffects.Clear(); break;
            case "add":
                owner.AppliedEffects.Add(new() { EffectId = "silken_tress_used_v1", BeforeValue = 0, AfterValue = 1 });
                break;
            case "reroll": owner.CardCanReroll = true; break;
        }
        string original = JsonSerializer.Serialize(replica);
        Assert.Throws<InvalidOperationException>(() =>
            ApCardRevealCodec.DecodeInto(replica, ApCardRevealCodec.Encode(owner)));
        Assert.Equal(original, JsonSerializer.Serialize(replica));
    }

    private static ApCardAssignmentState Assignment(ApMirroredRewardSpec spec) => new()
    {
        SerializedCards = spec.SerializedModels.ToList(), CanReroll = spec.CardCanReroll,
        IsRare = spec.IsRareCardReward, RewardActIndex = spec.CardRewardActIndex,
        HasBeenRevealed = spec.CardHasBeenRevealed, MaterializationStrategyId = spec.MaterializationStrategyId,
        AppliedEffects = RoundTrip(spec.AppliedEffects),
    };

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
