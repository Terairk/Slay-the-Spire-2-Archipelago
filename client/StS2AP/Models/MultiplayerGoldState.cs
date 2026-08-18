namespace StS2AP.Models;

/// <summary>
/// Local-only owner persistence for aggregate AP gold redemption. This is not
/// part of the host's canonical STS run save and is never synchronized to peers.
/// </summary>
public sealed class MultiplayerGoldState
{
    public int SchemaVersion { get; set; } = 1;

    public List<MultiplayerGoldRunState> Runs { get; set; } = new();
}

/// <summary>A single AP owner and STS run's cumulative raw redemption cursors.</summary>
public sealed class MultiplayerGoldRunState
{
    public string ApRoomSeed { get; set; } = string.Empty;

    /// <summary>Numeric AP team identity returned after login; unrelated to an STS lobby.</summary>
    public int ApTeamId { get; set; }

    /// <summary>Numeric AP slot identity returned after login; PlayerName is only its login name.</summary>
    public int ApSlotId { get; set; }

    public string StsRunIdentity { get; set; } = string.Empty;

    public Dictionary<long, int> RedeemedRawByCharacter { get; set; } = new();
}
