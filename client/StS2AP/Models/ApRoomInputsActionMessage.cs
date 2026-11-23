namespace StS2AP.Models;

/// <summary>
/// The host's view of every AP player's room-construction inputs, sent through the native action
/// queue ahead of a room that builds per-player options, so every replica builds the same ones.
/// </summary>
public sealed class ApRoomInputsActionMessage
{
    public Guid RunId { get; set; }
    public int ActIndex { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<ApPlayerRoomInputs> Players { get; set; } = new();
}

/// <summary>One AP player's progress, as far as campfires and Ancients need it.</summary>
public sealed class ApPlayerRoomInputs
{
    public ulong NetId { get; set; }

    /// <summary>Progressive Ancient counts per character offset.</summary>
    public Dictionary<long, int> ProgressiveAncients { get; set; } = new();

    /// <summary>False until the owner's progress has reached the host at least once.</summary>
    public bool HasCampfireInputs { get; set; }
    public int RestLevel { get; set; }
    public int SmithLevel { get; set; }
    public List<long> CheckedCampfireLocationIds { get; set; } = new();
}
