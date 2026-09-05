using StS2AP.DomainAdapters;
using Xunit;

namespace StS2AP.RegressionTests;

public sealed class RewardMaterializationInteropTests
{
    [Fact]
    public void AdapterReturnsPolicyWithCallableCSharpHandlers()
    {
        var policy = RewardMaterializationAdapter.Decode("ap_rng_owner_final_v1", false, "2:42");

        string result = policy.Match(
            () => "owner",
            () => throw new InvalidOperationException("Unexpected restore handler."),
            () => throw new InvalidOperationException("Unexpected generation handler."));

        Assert.Equal("owner", result);
    }

    [Theory]
    [InlineData("unknown", false, "used an unknown materialization strategy.")]
    [InlineData("ap_rng_owner_final_v1", true, "had an inconsistent materialization contract.")]
    public void AdapterMapsDomainErrorsToExistingExceptions(string strategy, bool replay, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RewardMaterializationAdapter.Decode(strategy, replay, "2:42"));

        Assert.Equal($"AP reward 2:42 {message}", error.Message);
    }
}
