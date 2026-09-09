using System.Text.Json;
using StS2AP.DomainAdapters;
using StS2AP.Persistence;
using Xunit;

namespace StS2AP.RegressionTests;

public sealed class BonusRewardStateTests
{
    [Fact]
    public void BonusAssignmentsSurviveProgressDeltasAndSaveWithoutReservingRelicCoupons()
    {
        var before = new ApRunProgressState { CombatsSinceLastWaxMelt = 2 };
        var assigned = new ApProgressDelta().ApplyToCopy(before);
        assigned.BonusRelicAssignments[42] = "{\"IsWax\":true,\"id\":\"RELIC.THE_BOOT\"}";
        var delta = ApProgressDelta.Between(before, assigned);
        Assert.True(delta.HasChanges);
        var received = JsonSerializer.Deserialize<ApProgressDelta>(JsonSerializer.Serialize(delta))!.ApplyToCopy(before);
        var restored = JsonSerializer.Deserialize<ApRunProgressState>(JsonSerializer.Serialize(received))!;
        Assert.Equal(assigned.BonusRelicAssignments[42], restored.BonusRelicAssignments[42]);
        Assert.Equal(2, restored.CombatsSinceLastWaxMelt);
        Assert.Empty(restored.RelicChoiceAssignments);
        Assert.Empty(before.BonusRelicAssignments);
        Assert.False(ApProgressDelta.Between(restored, received).HasChanges);

        received.BonusRelicAssignments.Clear();
        var removed = ApProgressDelta.Between(restored, received).ApplyToCopy(restored);
        Assert.Empty(removed.BonusRelicAssignments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void BonusRequiresExactlyOneSerializedRelic(int models)
    {
        var spec = new ApMirroredRewardSpec
        {
            Kind = ApMirroredRewardKind.Bonus,
            SerializedModels = Enumerable.Repeat("{}", models).ToList(),
        };
        Assert.Throws<InvalidOperationException>(() => MirroredRewardAdapter.Decode(spec, 3));
    }
}
