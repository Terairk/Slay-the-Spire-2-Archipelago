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
        MaterializationStrategyId = "ap_rng_replicated_card_v1",
    };

    private static ApMirroredRewardSpec Final()
    {
        var spec = Recipe();
        spec.CardHasBeenRevealed = true;
        spec.SerializedModels = ["""{"id":"CARD.A","upgrade":1,"enchantment":{"id":"GLAM"}}""",
                                 """{"id":"CARD.B","upgrade":0}"""];
        return spec;
    }

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
        foreach (var spec in RoundTrip(menu).Rewards)
        {
            var reward = MirroredRewardAdapter.Decode(spec, 3);
            Assert.True(reward.Match(card => card.IsDeferred, _ => false, _ => false, _ => false, _ => false, _ => false));
            Assert.Empty(reward.Effects);
        }
        Assert.Throws<InvalidOperationException>(() =>
            MirroredRewardAdapter.DecodeSavedCardAssignment(42, new ApCardAssignmentState(), 123));
    }

    [Fact]
    public void IndependentlyConstructedOffersAgreeWithoutCopyingCardsOrEffects()
    {
        var owner = Final();
        var replica = Final();
        // Dictionary/JSON property ordering is not gameplay order.
        replica.SerializedModels[0] = """{"enchantment":{"id":"GLAM"},"upgrade":1,"id":"CARD.A"}""";
        var local = ApCardRevealCodec.Encode(replica);
        var remote = ApCardRevealCodec.Encode(owner);
        ApCardRevealCodec.Verify(local, remote, "7:42");
        Assert.Equal(9, remote.Count); // Version plus SHA-256, no final-model transport.
        Assert.Empty(replica.AppliedEffects);
    }

    [Theory]
    [InlineData("firstReveal")]
    [InlineData("receipt")]
    [InlineData("owner")]
    [InlineData("act")]
    [InlineData("slot")]
    [InlineData("rare")]
    [InlineData("order")]
    [InlineData("card")]
    [InlineData("upgrade")]
    [InlineData("enchantment")]
    [InlineData("reroll")]
    public void DisagreementIsRejectedWithoutMutatingTheReplica(string change)
    {
        var owner = Final();
        var replica = Final();
        switch (change)
        {
            case "receipt": owner.ReceivedItemIndex++; break;
            case "owner": owner.OwnerNetId++; break;
            case "act": owner.CardRewardActIndex++; break;
            case "slot": owner.ApSlotId++; break;
            case "rare": owner.IsRareCardReward = true; owner.CardRewardActIndex = null; break;
            case "order": owner.SerializedModels.Reverse(); break;
            case "card": owner.SerializedModels[0] = """{"id":"CARD.C"}"""; break;
            case "upgrade": owner.SerializedModels[1] = owner.SerializedModels[1].Replace("\"upgrade\":0", "\"upgrade\":1"); break;
            case "enchantment": owner.SerializedModels[0] = owner.SerializedModels[0].Replace("GLAM", "NIMBLE"); break;
            case "reroll": owner.CardCanReroll = true; break;
        }
        string original = JsonSerializer.Serialize(replica);
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.Verify(
            ApCardRevealCodec.Encode(replica),
            ApCardRevealCodec.Encode(owner, firstReveal: change != "firstReveal"), "7:42"));
        Assert.Equal(original, JsonSerializer.Serialize(replica));
    }

    [Fact]
    public void VerificationRejectsIncompleteLegacyAndMalformedPayloads()
    {
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.Encode(Recipe()));
        var legacy = Final();
        legacy.MaterializationStrategyId = "ap_rng_owner_final_v1";
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.Encode(legacy));
        var effects = Final();
        effects.AppliedEffects.Add(new() { EffectId = "silken_tress_used_v1", BeforeValue = 0, AfterValue = 1 });
        Assert.Throws<InvalidOperationException>(() => MirroredRewardAdapter.Decode(effects, 3));
        var digest = ApCardRevealCodec.Encode(Final());
        Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.Verify(digest, digest.Take(8).ToList(), "7:42"));
        foreach (int version in new[] { 1, 2 })
        {
            var oldProtocol = digest.ToList();
            oldProtocol[0] = version;
            Assert.Throws<InvalidOperationException>(() => ApCardRevealCodec.Verify(digest, oldProtocol, "7:42"));
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void PreviousMenuSchemaIsRejectedBeforeStartingNativeChoices(int version)
    {
        var spec = Recipe();
        spec.SchemaVersion = version;
        Assert.Throws<InvalidOperationException>(() => MirroredRewardAdapter.Decode(spec, 3));
    }

    [Fact]
    public void RefreshedOfferSurvivesSaveAndProgressDeltaWithoutReplayInstructions()
    {
        var before = Final();
        var owner = RoundTrip(before);
        owner.SerializedModels[1] = owner.SerializedModels[1].Replace("\"upgrade\":0", "\"upgrade\":1");
        var replica = RoundTrip(owner); // Represents independently refreshed final cards.
        ApCardRevealCodec.Verify(ApCardRevealCodec.Encode(replica, firstReveal: false),
            ApCardRevealCodec.Encode(owner, firstReveal: false), "7:42");
        var oldProgress = new ApRunProgressState { CardAssignments = { [42] = Assignment(before) } };
        var newProgress = new ApRunProgressState { CardAssignments = { [42] = Assignment(replica) } };
        ApProgressDelta delta = RoundTrip(ApProgressDelta.Between(oldProgress, newProgress));
        Assert.Single(delta.CardAssignmentUpserts);
        Assert.Empty(delta.UsedItemsAdded);
        ApRunProgressState restored = RoundTrip(delta.ApplyToCopy(oldProgress));
        var decoded = MirroredRewardAdapter.DecodeSavedCardAssignment(42, restored.CardAssignments[42], 123).Card;
        Assert.Equal(owner.SerializedModels, decoded.Models);
        Assert.True(decoded.Configuration.HasBeenRevealed);
        Assert.Empty(decoded.Configuration.Effects);
        Assert.Equal("ap_rng_replicated_card_v1", decoded.Configuration.Policy.StrategyId);
        Assert.False(ApProgressDelta.Between(restored, newProgress).HasChanges);
    }

    private static ApCardAssignmentState Assignment(ApMirroredRewardSpec spec) => new()
    {
        SerializedCards = spec.SerializedModels.ToList(), CanReroll = spec.CardCanReroll,
        IsRare = spec.IsRareCardReward, RewardActIndex = spec.CardRewardActIndex,
        HasBeenRevealed = spec.CardHasBeenRevealed, MaterializationStrategyId = spec.MaterializationStrategyId,
    };

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
