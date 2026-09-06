using System.Text.Json;
using StS2AP.Utils;
using Xunit;

namespace StS2AP.RegressionTests;

public sealed class ApSessionIdentityTests
{
    [Theory]
    [InlineData("other.example:38281", "seed", 0, 1)]
    [InlineData("ap.example:38282", "seed", 0, 1)]
    [InlineData("ap.example:38281", "other-seed", 0, 1)]
    [InlineData("ap.example:38281", "seed", 1, 1)]
    [InlineData("ap.example:38281", "seed", 0, 2)]
    public void DifferentDestinationsCannotShareReconnectOrOutboxIdentity(
        string server, string seed, int team, int slot)
    {
        var expected = ApSessionIdentity.Create("ap.example:38281", "seed", 0, 1);
        var candidate = ApSessionIdentity.Create(server, seed, team, slot);

        Assert.NotEqual(expected, candidate);
        Assert.NotEqual(expected.GetFileKey(), candidate.GetFileKey());
    }

    [Fact]
    public void EquivalentAddressAndPersistedIdentityRemainEqual()
    {
        var expected = ApSessionIdentity.Create("ap.example:38281", "seed", 0, 1);
        var candidate = ApSessionIdentity.Create(" AP.EXAMPLE:38281/ ", "seed", 0, 1);
        var restored = JsonSerializer.Deserialize<ApSessionIdentity>(
            JsonSerializer.Serialize(candidate));

        Assert.Equal(expected, candidate);
        Assert.Equal(expected, restored);
        Assert.Equal(expected.GetFileKey(), candidate.GetFileKey());
    }
}
