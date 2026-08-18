namespace StS2AP.Models;

/// <summary>
/// Owner-private persistence for discrete AP grants. This is deliberately separate from
/// MegaCrit's shared run save and is scoped by AP room/team/slot plus STS run identity.
/// </summary>
public sealed class MultiplayerGrantState
{
    public int SchemaVersion { get; set; } = 1;

    public List<MultiplayerGrantRunState> Runs { get; set; } = new();
}

public sealed class MultiplayerGrantRunState
{
    public string ApRoomSeed { get; set; } = string.Empty;

    public int ApTeamId { get; set; }

    public int ApSlotId { get; set; }

    public string StsRunIdentity { get; set; } = string.Empty;

    public Dictionary<int, MultiplayerGrantRecord> Grants { get; set; } = new();
}

public sealed class MultiplayerGrantRecord
{
    public ApMirroredRewardKind Kind { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public bool Applied { get; set; }

    public bool IsRareCardReward { get; set; }

    public int? CardRewardActIndex { get; set; }

    public bool CardCanReroll { get; set; }

    public List<string> SerializedModels { get; set; } = new();

    public string? LastAttempt { get; set; }
}
